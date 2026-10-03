using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TermDeck.Core;
using TermDeck.Terminal;

namespace TermDeck.Views;

/// <summary>
/// A tool tab: terminal in the middle, argument box at the bottom, run history on the right.
/// Each Run = one process = one history record; several runs can execute in parallel.
/// </summary>
public partial class ToolTab : UserControl, IDocView
{
    readonly HistoryStore _store;
    readonly AppConfig _config;
    readonly Dictionary<long, RunSession> _live = new();
    readonly ObservableCollection<RunItem> _runs = new();
    readonly ConcurrentQueue<(long Id, string Data)> _queue = new();
    readonly DispatcherTimer _flushTimer;
    readonly DispatcherTimer _tickTimer;
    readonly List<string> _argsHistory = new();

    long _viewId = -1;
    RunSession? _attached;
    int _historyIndex = -1;
    bool _suppressSelection;
    string? _pendingFind;

    /// <summary>Effective run-as account for the next Run; starts from the tool default, can be overridden per run.</summary>
    string _runAs = "";
    /// <summary>Run-as account each launched run used, so the info bar can show it (session-only).</summary>
    readonly Dictionary<long, string> _runAsById = new();
    /// <summary>Autorun rule that launched a run ("rule ← source tool"), shown in the info bar (session-only).</summary>
    readonly Dictionary<long, string> _originById = new();
    /// <summary>Runs started through a proxy: profile name, and the endpoints each reached directly (bypassing it).</summary>
    readonly Dictionary<long, string> _proxyById = new();
    readonly Dictionary<long, List<string>> _leaksById = new();

    public ToolDef Tool { get; private set; }
    public string Cwd { get; private set; }

    public event Action<IDocView>? CwdChanged;
    public event Action<IDocView>? RunningChanged;
    /// <summary>A run of this tab finished (its record has the exit code) — autorun rules start from here.</summary>
    public event Action<ToolTab, RunRecord>? RunFinished;
    /// <summary>History menu: run a rule on a past run (rule = null: create a new rule from it).</summary>
    public event Action<ToolTab, RunRecord, AutoRule?>? AutorunRequested;

    public TerminalHost Terminal => Term;

    public string ExportName => CurrentItem is { } item
        ? $"{Tool.Name}-{item.Record.StartedAt:yyyyMMdd-HHmmss}"
        : Tool.Name;

    public ToolTab(ToolDef tool, HistoryStore store, AppConfig config)
    {
        InitializeComponent();
        Tool = tool;
        _store = store;
        _config = config;
        // Every tool opens in the folder of the project it belongs to.
        Cwd = store.ProjectDir;

        HistoryColumn.Width = new GridLength(Math.Max(180, config.HistoryWidth));
        Term.FontFamily = config.TerminalFont;
        Term.FontSize = config.TerminalFontSize;
        Term.Input += s => _attached?.WriteInput(s);
        // When the tool has a fixed width, keep the pty at that width on resize (only rows follow the window).
        Term.Resized += (c, r) => _attached?.Resize(Tool.TerminalCols > 0 ? Tool.TerminalCols : c, r);

        RunsList.ItemsSource = _runs;

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        _flushTimer.Start();
        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { if (_attached?.IsRunning == true) UpdateInfo(); }, Dispatcher);
        _tickTimer.Start();

        ArgsBox.Text = tool.DefaultArgs;
        _runAs = tool.RunAsUser?.Trim() ?? "";
        ApplyToolUi();
        LoadRuns();
        if (_runs.Count > 0) RunsList.SelectedIndex = 0;
        else ShowEmpty();
    }

    public bool HasRunning => _live.Values.Any(s => s.IsRunning);
    public int RunningCount => _live.Values.Count(s => s.IsRunning);

    // ───────────────────────── Display ─────────────────────────

    void ApplyToolUi()
    {
        var wsl = Tool.Kind == ToolKind.Wsl;
        BadgeText.Text = wsl ? "WSL" : "WIN";
        Badge.Background = (Brush)FindResource(wsl ? "WslBadge" : "WinBadge");
        Badge.ToolTip = Tool.KindLabel;
        ToolPathText.Text = Tool.Path;
        ToolPathText.ToolTip = Tool.Kind == ToolKind.Windows ? AppPaths.ResolveToolPath(Tool.Path) : Tool.Path;
        HistoryHeader.Text = $"History · {Tool.Name}";
        CwdText.Text = PathMapper.Display(Cwd, Tool);
        UpdateRunAsUi();
    }

    void UpdateRunAsUi()
    {
        RunAsLabel.Text = _runAs.Length == 0
            ? (Tool.Kind == ToolKind.Wsl ? "login user" : "me")
            : _runAs;
        var scope = Tool.Kind == ToolKind.Wsl ? "wsl -u" : "logon";
        RunAsButton.ToolTip = _runAs.Length == 0
            ? "Runs as the current account. Click to run as another user."
            : $"Runs as {_runAs} ({scope}). Click to change.";
    }

    public void UpdateTool(ToolDef tool)
    {
        Tool = tool;
        _runAs = tool.RunAsUser?.Trim() ?? "";
        ApplyToolUi();
    }

    public void ApplyFont() => Term.SetFont(_config.TerminalFont, _config.TerminalFontSize);

    public void SetCwd(string dir, bool raise)
    {
        if (string.Equals(dir, Cwd, StringComparison.OrdinalIgnoreCase)) return;
        Cwd = dir;
        CwdText.Text = PathMapper.Display(Cwd, Tool);
        if (raise) CwdChanged?.Invoke(this);
    }

    void LoadRuns()
    {
        List<RunRecord> records;
        try { records = _store.List(Tool.Id); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not read history: " + ex.Message, "TermDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            records = new List<RunRecord>();
        }

        _runs.Clear();
        foreach (var r in records)
        {
            if (_live.TryGetValue(r.Id, out var s)) _runs.Add(new RunItem(s.Record, s.IsRunning));
            else _runs.Add(new RunItem(r, false));
        }

        _argsHistory.Clear();
        _argsHistory.AddRange(records.Select(r => r.Args).Where(a => a.Length > 0).Distinct());
        UpdateHeaderCount();
    }

    void UpdateHeaderCount() => HistoryHeader.Text = $"History · {Tool.Name} ({_runs.Count})";

    void ShowEmpty()
    {
        DetachView();
        _viewId = -1;
        Term.Reset();
        Term.Write("\x1b[90mNo runs for this tool yet.\r\nType arguments in the box below and press Enter to run.\x1b[0m\r\n");
        UpdateInfo();
        UpdateButtons();
    }

    void DetachView()
    {
        _attached?.Detach();
        _attached = null;
        while (_queue.TryDequeue(out _)) { }
    }

    async void Show(RunItem item)
    {
        DetachView();
        var id = item.Record.Id;
        _viewId = id;
        Term.Reset();

        if (_live.TryGetValue(id, out var session) && session.IsRunning)
        {
            Term.Write(session.Attach(Sink));
            _attached = session;
            session.Resize(Tool.TerminalCols > 0 ? Tool.TerminalCols : Term.Cols, Term.Rows);
        }
        else
        {
            var path = _store.LogPath(item.Record);
            var text = await Task.Run(() => CastReader.ReadOutput(path));
            if (_viewId != id) return;
            Term.Write(text.Length > 0 ? text : "\x1b[90m(no output, or the log file was deleted)\x1b[0m\r\n");
        }
        if (_pendingFind is { Length: > 0 } find) Term.Find(find);
        _pendingFind = null;
        UpdateInfo();
        UpdateButtons();
    }

    void Sink(RunSession s, string data) => _queue.Enqueue((s.Record.Id, data));

    void Flush()
    {
        if (_queue.IsEmpty) return;
        var sb = new StringBuilder();
        while (_queue.TryDequeue(out var chunk))
            if (chunk.Id == _viewId) sb.Append(chunk.Data);
        if (sb.Length > 0) Term.Write(sb.ToString());
    }

    RunItem? CurrentItem => _runs.FirstOrDefault(r => r.Record.Id == _viewId);

    void UpdateInfo()
    {
        var item = CurrentItem;
        if (item == null)
        {
            RunInfo.Text = "";
            RunStatus.Text = "";
            return;
        }
        var r = item.Record;
        var asUser = _runAsById.TryGetValue(r.Id, out var u) ? $"[{u}] " : "";
        var origin = _originById.TryGetValue(r.Id, out var o) ? $"[⚡ {o}] " : "";
        _proxyById.TryGetValue(r.Id, out var px);
        var leaks = _leaksById.TryGetValue(r.Id, out var l) ? l : null;
        var bypassed = leaks is { Count: > 0 };
        var proxy = px == null ? ""
            : bypassed ? $"[⚠ bypassing {px}: {leaks![0]}{(leaks.Count > 1 ? $" +{leaks.Count - 1}" : "")}] "
            : $"[🌐 {px}] ";
        RunInfo.Text = proxy + origin + "$ " + asUser + r.CommandLine;
        RunInfo.Foreground = new SolidColorBrush(bypassed ? Color.FromRgb(0xE8, 0xA3, 0x3C) : Color.FromRgb(0xD4, 0xD4, 0xD4));
        RunInfo.ToolTip = $"{r.CommandLine}\nDirectory: {r.Cwd}"
            + (asUser.Length > 0 ? $"\nRun as: {u}" : "")
            + (origin.Length > 0 ? $"\nStarted by autorun: {o}" : "")
            + (px == null ? ""
                : bypassed ? $"\nProxy: {px}\nDirect connections (not through the proxy):\n  " + string.Join("\n  ", leaks!)
                : $"\nProxy: {px}\nNo direct TCP connection seen (raw-socket traffic such as SYN scans or ping is not visible).");
        if (item.IsRunning)
            RunStatus.Text = $"running · {RunSession.FormatDuration(DateTime.Now - r.StartedAt)}";
        else
            RunStatus.Text = item.Meta;
    }

    void UpdateButtons() => StopButton.IsEnabled = _attached?.IsRunning == true;

    // ───────────────────────── Run ─────────────────────────

    public void Run() => Launch(ArgsBox.Text.Trim(), null);

    /// <summary>
    /// Starts one run with the given arguments. <paramref name="origin"/> is set when autorun starts it: the run then
    /// does not take the keyboard, so typing elsewhere is not sent to it.
    /// </summary>
    public RunSession? Launch(string args, string? origin)
    {
        if (string.IsNullOrWhiteSpace(Tool.Path))
        {
            MessageBox.Show("This tool has no binary path. Set it in the tool settings.", "TermDeck");
            return null;
        }

        args = args.Trim();
        var spec = CommandBuilder.Build(Tool, args, Cwd, _runAs, Proxy.Current);

        // Windows run-as needs a password (WSL run-as is baked into the wsl -u command line and needs none).
        WinCredential? cred = null;
        if (spec.WinRunAsUser != null)
        {
            cred = CredentialWindow.Acquire(Window.GetWindow(this), spec.WinRunAsUser);
            if (cred == null) return null; // user cancelled the password prompt
        }

        var record = new RunRecord
        {
            ToolId = Tool.Id,
            ToolName = Tool.Name,
            Args = args,
            CommandLine = spec.Display,
            Cwd = spec.DisplayCwd,
            StartedAt = DateTime.Now,
        };
        try { _store.Insert(record); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not write history: " + ex.Message, "TermDeck", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        if (_runAs.Length > 0) _runAsById[record.Id] = _runAs;
        if (origin != null) _originById[record.Id] = origin;
        if (spec.Proxy != null) _proxyById[record.Id] = spec.Proxy.Profile.Name;

        var session = new RunSession(record);
        _live[record.Id] = session;
        session.Exited += s => Dispatcher.BeginInvoke(() => OnExited(s));
        session.ProxyLeak += (s, ep) => Dispatcher.BeginInvoke(() =>
        {
            if (!_leaksById.TryGetValue(s.Record.Id, out var list)) _leaksById[s.Record.Id] = list = new();
            list.Add(ep);
            if (_viewId == s.Record.Id) UpdateInfo();
        });

        var item = new RunItem(record, true);
        _runs.Insert(0, item);
        UpdateHeaderCount();
        if (args.Length > 0)
        {
            _argsHistory.Remove(args);
            _argsHistory.Insert(0, args);
        }
        _historyIndex = -1;

        // Attach the view before starting so no output byte is missed.
        DetachView();
        _viewId = record.Id;
        Term.Reset();
        session.Attach(Sink);
        _attached = session;

        _suppressSelection = true;
        RunsList.SelectedItem = item;
        RunsList.ScrollIntoView(item);
        _suppressSelection = false;

        session.Start(spec, _store.LogPath(record), Tool.TerminalCols > 0 ? Tool.TerminalCols : Term.Cols, Term.Rows, cred);
        RunningChanged?.Invoke(this);
        UpdateInfo();
        UpdateButtons();

        // Keyboard goes to the running process (arrow keys, menus, prompts, Ctrl+C) until it exits.
        if (session.IsRunning && origin == null) Term.FocusTerminal();
        return session;
    }

    void OnExited(RunSession s)
    {
        try { _store.Finish(s.Record); } catch { }
        var record = s.Record;
        Task.Run(() => { try { _store.IndexOutput(record); } catch { } });
        _live.Remove(s.Record.Id);
        var item = _runs.FirstOrDefault(r => r.Record.Id == s.Record.Id);
        if (item != null)
        {
            item.IsRunning = false;
            item.Refresh();
        }
        var wasAttached = _attached == s;
        if (wasAttached)
        {
            Flush();
            s.Detach();
            _attached = null;
        }
        RunningChanged?.Invoke(this);
        UpdateInfo();
        UpdateButtons();

        // The process that owned the keyboard is gone: hand focus back to the argument box for the next command.
        if (wasAttached && IsVisible && Term.IsKeyboardFocusWithin) FocusInput();
        RunFinished?.Invoke(this, s.Record);
    }

    public void KillAll()
    {
        foreach (var s in _live.Values) s.Kill();
    }

    /// <summary>Stops a running run of this tab by id (used by the AI agent). Returns false if it is not running here.</summary>
    public bool StopRun(long id)
    {
        if (_live.TryGetValue(id, out var s) && s.IsRunning) { s.Stop(); return true; }
        return false;
    }

    public void Dispose()
    {
        // Runs killed with the tab finish later: they must not fire autorun rules.
        RunFinished = null;
        AutorunRequested = null;
        _flushTimer.Stop();
        _tickTimer.Stop();
        DetachView();
        Term.Dispose();
    }

    public void OpenFind() => Term.OpenFind();

    /// <summary>Shows a run from history (e.g. picked in the Search window). Returns false if it is not in this tab's list.</summary>
    public bool SelectRun(long runId, string? find = null)
    {
        SearchBox.Text = "";
        var item = _runs.FirstOrDefault(r => r.Record.Id == runId);
        if (item == null) return false;
        if (RunsList.SelectedItem == item)
        {
            if (!string.IsNullOrEmpty(find)) Term.Find(find);
        }
        else
        {
            _pendingFind = find; // applied once Show() has written the log
            RunsList.SelectedItem = item;
        }
        RunsList.ScrollIntoView(item);
        return true;
    }

    public void FocusInput()
    {
        ArgsBox.Focus();
        ArgsBox.CaretIndex = ArgsBox.Text.Length;
    }

    public void InsertText(string text)
    {
        var i = ArgsBox.CaretIndex;
        var before = ArgsBox.Text[..i];
        var after = ArgsBox.Text[i..];
        var pad = before.Length > 0 && !before.EndsWith(' ') ? " " : "";
        ArgsBox.Text = before + pad + text + after;
        ArgsBox.CaretIndex = i + pad.Length + text.Length;
        ArgsBox.Focus();
    }

    // ───────────────────────── UI events ─────────────────────────

    void Run_Click(object sender, RoutedEventArgs e) => Run();

    void RunAs_Click(object sender, RoutedEventArgs e)
    {
        var label = Tool.Kind == ToolKind.Wsl
            ? "Linux user (empty = login user; e.g. root):"
            : "Windows account (empty = current user; DOMAIN\\user or user):";
        var result = PromptWindow.Ask(Window.GetWindow(this), "Run as", label, _runAs);
        if (result == null) return; // cancelled
        _runAs = result.Trim();
        UpdateRunAsUi();
    }

    void Stop_Click(object sender, RoutedEventArgs e) => _attached?.Stop();

    void PickCwd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Cwd, Title = "Working directory for " + Tool.Name };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) SetCwd(dlg.FolderName, true);
    }

    void ArgsBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Run();
                break;
            case Key.Up when _argsHistory.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Min(_historyIndex + 1, _argsHistory.Count - 1);
                ArgsBox.Text = _argsHistory[_historyIndex];
                ArgsBox.CaretIndex = ArgsBox.Text.Length;
                break;
            case Key.Down when _historyIndex >= 0:
                e.Handled = true;
                _historyIndex--;
                ArgsBox.Text = _historyIndex >= 0 ? _argsHistory[_historyIndex] : "";
                ArgsBox.CaretIndex = ArgsBox.Text.Length;
                break;
        }
    }

    void ArgsBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    void ArgsBox_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            InsertText(string.Join(" ", files.Select(f => PathMapper.ForCommand(f, Tool))));
            e.Handled = true;
        }
    }

    void RunsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (RunsList.SelectedItem is RunItem item) Show(item);
    }

    void RunsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => UseArgs_Click(sender, e);

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        CollectionViewSource.GetDefaultView(_runs).Filter = q.Length == 0
            ? null
            : o => ((RunItem)o).Record.Args.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void Reload_Click(object sender, RoutedEventArgs e)
    {
        var keep = _viewId;
        LoadRuns();
        var item = _runs.FirstOrDefault(r => r.Record.Id == keep);
        if (item != null)
        {
            _suppressSelection = true;
            RunsList.SelectedItem = item;
            _suppressSelection = false;
        }
    }

    void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _config.HistoryWidth = HistoryColumn.ActualWidth;

    RunItem? Selected => RunsList.SelectedItem as RunItem;

    void Rerun_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        ArgsBox.Text = item.Record.Args;
        Run();
    }

    void UseArgs_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        ArgsBox.Text = item.Record.Args;
        FocusInput();
    }

    void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item) Clipboard.SetText(item.Record.CommandLine);
    }

    async void CopyOutput_Click(object sender, RoutedEventArgs e) => await TerminalExport.CopyAsync(Term);

    async void SaveOutput_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        await TerminalExport.SaveAsync(Window.GetWindow(this), Term, ExportName, Cwd);
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        var path = _store.LogPath(item.Record);
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    /// <summary>Fills the "Autorun" submenu with the rules whose source is this tool.</summary>
    void RunsMenu_Opened(object sender, RoutedEventArgs e)
    {
        AutorunMenu.Items.Clear();
        var item = Selected;
        AutorunMenu.IsEnabled = item is { IsRunning: false };
        if (item == null) return;
        foreach (var rule in _config.AutoRules.Where(r => r.FromToolId == Tool.Id))
        {
            var target = _config.Tools.FirstOrDefault(t => t.Id == rule.ToToolId)?.Name ?? "(missing tool)";
            var mi = new MenuItem { Header = $"{rule.Name}  →  {target}", ToolTip = "Run this rule on the selected run (even if it is disabled)" };
            if (!rule.Enabled) mi.Foreground = (Brush)FindResource("TextMuted");
            mi.Click += (_, _) => AutorunRequested?.Invoke(this, item.Record, rule);
            AutorunMenu.Items.Add(mi);
        }
        if (AutorunMenu.Items.Count > 0) AutorunMenu.Items.Add(new Separator());
        var create = new MenuItem { Header = "New rule from this run..." };
        create.Click += (_, _) => AutorunRequested?.Invoke(this, item.Record, null);
        AutorunMenu.Items.Add(create);
    }

    void DeleteRun_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || item.IsRunning) return;
        if (MessageBox.Show($"Delete this run from history?\n\n{item.Record.CommandLine}", "TermDeck",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _store.Delete(item.Record);
        var index = _runs.IndexOf(item);
        _runs.Remove(item);
        UpdateHeaderCount();
        if (_runs.Count == 0) ShowEmpty();
        else RunsList.SelectedIndex = Math.Min(index, _runs.Count - 1);
    }
}
