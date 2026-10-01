using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TermDeck.Core;
using TermDeck.Terminal;

namespace TermDeck.Views;

/// <summary>
/// A free interactive shell (WSL bash/zsh, PowerShell, cmd) started in the project folder.
/// Each session is recorded in the project history like a tool run, so it shows up in search and reports.
/// </summary>
public sealed class ShellTab : UserControl, IDocView
{
    readonly HistoryStore _store;
    readonly AppConfig _config;
    readonly TerminalHost _term = new();
    readonly TextBlock _info = new();
    readonly TextBlock _status = new();
    readonly Button _restart;
    readonly ConcurrentQueue<string> _queue = new();
    readonly DispatcherTimer _flushTimer;
    readonly DispatcherTimer _tickTimer;
    RunSession? _session;

    public ShellDef Shell { get; }
    public ToolDef Tool { get; }
    public string Cwd { get; private set; }
    /// <summary>Folder the current session was started in (the shell's own cd is not tracked).</summary>
    string _startDir;

    public event Action<IDocView>? CwdChanged;
    public event Action<IDocView>? RunningChanged;

    public ShellTab(ShellDef shell, HistoryStore store, AppConfig config, string startDir)
    {
        Shell = shell;
        Tool = shell.AsTool();
        _store = store;
        _config = config;
        Cwd = _startDir = startDir;

        _term.FontFamily = config.TerminalFont;
        _term.FontSize = config.TerminalFontSize;
        _term.Input += s => _session?.WriteInput(s);
        _term.Resized += (c, r) => _session?.Resize(c, r);
        _term.Ready += () => { if (_session == null) Start(); };

        _info.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        _info.FontFamily = (FontFamily)FindResource("MonoFont");
        _info.FontSize = 12;
        _info.TextTrimming = TextTrimming.CharacterEllipsis;
        _info.VerticalAlignment = VerticalAlignment.Center;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        _status.FontSize = 12;
        _status.Margin = new Thickness(12, 0, 8, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;

        _restart = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "",
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            ToolTip = "Restart the shell (new session in the project folder)",
            Height = 22,
        };
        _restart.Click += (_, _) => Restart();

        var bar = new DockPanel();
        DockPanel.SetDock(_restart, Dock.Right);
        DockPanel.SetDock(_status, Dock.Right);
        bar.Children.Add(_restart);
        bar.Children.Add(_status);
        bar.Children.Add(_info);

        var top = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)), Padding = new Thickness(10, 3, 4, 3), Child = bar };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_term);
        Content = root;

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        _flushTimer.Start();
        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus(), Dispatcher);
        _tickTimer.Start();
    }

    public TerminalHost Terminal => _term;
    public string ExportName => $"{Shell.Title.Split(" (")[0].Replace(" · ", "-")}-{_session?.Record.StartedAt ?? DateTime.Now:yyyyMMdd-HHmmss}";
    public bool HasRunning => _session?.IsRunning == true;
    public int RunningCount => HasRunning ? 1 : 0;

    void Start()
    {
        var spec = Shell.Build(_startDir);
        var record = new RunRecord
        {
            ToolId = Shell.ToolId,
            ToolName = "Shell · " + Shell.Title.Replace(" (default)", ""),
            Args = "",
            CommandLine = spec.Display,
            Cwd = spec.DisplayCwd,
            StartedAt = DateTime.Now,
        };
        try { _store.Insert(record); }
        catch (Exception ex)
        {
            _term.Write($"\x1b[31mCould not write history: {ex.Message}\x1b[0m\r\n");
            return;
        }

        var session = new RunSession(record);
        session.Exited += s => Dispatcher.BeginInvoke(() => OnExited(s));
        _session = session;
        session.Attach((_, data) => _queue.Enqueue(data));
        session.Start(spec, _store.LogPath(record), _term.Cols, _term.Rows);
        _info.Text = "$ " + spec.Display;
        _info.ToolTip = $"{spec.Display}\nDirectory: {spec.DisplayCwd}\nSession #{record.Id} is recorded in the project history.";
        UpdateStatus();
        RunningChanged?.Invoke(this);
        if (IsVisible) _term.FocusTerminal();
    }

    void OnExited(RunSession s)
    {
        try { _store.Finish(s.Record); } catch { }
        var record = s.Record;
        Task.Run(() => { try { _store.IndexOutput(record); } catch { } });
        if (s != _session) return;
        Flush();
        s.Detach();
        _term.Write("\x1b[90mShell closed. Click ⟳ (top right) to start a new session.\x1b[0m\r\n");
        UpdateStatus();
        RunningChanged?.Invoke(this);
    }

    void Restart()
    {
        if (HasRunning &&
            MessageBox.Show(Window.GetWindow(this)!, "The shell is still running. End it and start a new session?", "Restart shell",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var old = _session;
        old?.Detach();
        old?.Kill();
        while (_queue.TryDequeue(out _)) { }
        _startDir = Cwd;
        _term.Reset();
        Start();
    }

    void Flush()
    {
        if (_queue.IsEmpty) return;
        var sb = new StringBuilder();
        while (_queue.TryDequeue(out var chunk)) sb.Append(chunk);
        _term.Write(sb.ToString());
    }

    void UpdateStatus()
    {
        if (_session == null) { _status.Text = "starting…"; return; }
        var r = _session.Record;
        _status.Text = _session.IsRunning
            ? $"running · {RunSession.FormatDuration(DateTime.Now - r.StartedAt)}"
            : $"exited · {RunSession.DescribeExit(r.ExitCode)}";
    }

    /// <summary>The Files panel can move the tab's folder; it is used the next time the shell is restarted (a running shell is not cd'd).</summary>
    public void SetCwd(string dir, bool raise)
    {
        if (string.Equals(dir, Cwd, StringComparison.OrdinalIgnoreCase)) return;
        Cwd = dir;
        if (raise) CwdChanged?.Invoke(this);
    }

    /// <summary>Types the text (e.g. paths dropped from the Files panel) at the shell prompt.</summary>
    public void InsertText(string text)
    {
        _session?.WriteInput(text);
        _term.FocusTerminal();
    }

    public void FocusInput() => _term.FocusTerminal();
    public void OpenFind() => _term.OpenFind();
    public void ApplyFont() => _term.SetFont(_config.TerminalFont, _config.TerminalFontSize);
    public void KillAll() => _session?.Kill();

    public void Dispose()
    {
        _flushTimer.Stop();
        _tickTimer.Stop();
        _session?.Detach();
        _term.Dispose();
    }
}
