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

namespace TermDeck.Views;

/// <summary>
/// A tool tab: terminal in the middle, argument box at the bottom, run history on the right.
/// Each Run = one process = one history record; several runs can execute in parallel.
/// </summary>
public partial class ToolTab : UserControl, IDisposable
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

    public ToolDef Tool { get; private set; }
    public string Cwd { get; private set; }

    public event Action<ToolTab>? CwdChanged;
    public event Action<ToolTab>? RunningChanged;

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
        Term.Resized += (c, r) => _attached?.Resize(c, r);

        RunsList.ItemsSource = _runs;

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        _flushTimer.Start();
        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { if (_attached?.IsRunning == true) UpdateInfo(); }, Dispatcher);
        _tickTimer.Start();

        ArgsBox.Text = tool.DefaultArgs;
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
    }

    public void UpdateTool(ToolDef tool)
    {
        Tool = tool;
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
            session.Resize(Term.Cols, Term.Rows);
        }
        else
        {
            var path = _store.LogPath(item.Record);
            var text = await Task.Run(() => CastReader.ReadOutput(path));
            if (_viewId != id) return;
            Term.Write(text.Length > 0 ? text : "\x1b[90m(no output, or the log file was deleted)\x1b[0m\r\n");
        }
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
        RunInfo.Text = "$ " + r.CommandLine;
        RunInfo.ToolTip = $"{r.CommandLine}\nDirectory: {r.Cwd}";
        if (item.IsRunning)
            RunStatus.Text = $"running · {RunSession.FormatDuration(DateTime.Now - r.StartedAt)}";
        else
            RunStatus.Text = item.Meta;
    }

    void UpdateButtons() => StopButton.IsEnabled = _attached?.IsRunning == true;

    // ───────────────────────── Run ─────────────────────────

    public void Run()
    {
        if (string.IsNullOrWhiteSpace(Tool.Path))
        {
            MessageBox.Show("This tool has no binary path. Set it in the tool settings.", "TermDeck");
            return;
        }

        var args = ArgsBox.Text.Trim();
        var spec = CommandBuilder.Build(Tool, args, Cwd);
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
            return;
        }

        var session = new RunSession(record);
        _live[record.Id] = session;
        session.Exited += s => Dispatcher.BeginInvoke(() => OnExited(s));

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

        session.Start(spec, _store.LogPath(record), Term.Cols, Term.Rows);
        RunningChanged?.Invoke(this);
        UpdateInfo();
        UpdateButtons();

        // Keyboard goes to the running process (arrow keys, menus, prompts, Ctrl+C) until it exits.
        if (session.IsRunning) Term.FocusTerminal();
    }

    void OnExited(RunSession s)
    {
        try { _store.Finish(s.Record); } catch { }
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
    }

    public void KillAll()
    {
        foreach (var s in _live.Values) s.Kill();
    }

    public void Dispose()
    {
        _flushTimer.Stop();
        _tickTimer.Stop();
        DetachView();
        Term.Dispose();
    }

    public void OpenFind() => Term.OpenFind();

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

    async void CopyOutput_Click(object sender, RoutedEventArgs e)
    {
        var text = await Term.GetBufferTextAsync();
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    async void SaveOutput_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        var text = await Term.GetBufferTextAsync();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = Cwd,
            FileName = $"{Tool.Name}-{item.Record.StartedAt:yyyyMMdd-HHmmss}.txt",
            Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            File.WriteAllText(dlg.FileName, text + Environment.NewLine, new UTF8Encoding(false));
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        var path = _store.LogPath(item.Record);
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
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
