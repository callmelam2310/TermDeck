using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TermDeck.Core;
using TermDeck.Core.Ai;
using TermDeck.Terminal;

namespace TermDeck.Views;

/// <summary>
/// The AI agent as a document tab: chat in the middle, this project's saved chat sessions on the right (like a
/// tool tab's run history). It talks to the Claude CLI through <see cref="AgentBridge"/> (wired to TermDeck's MCP)
/// and saves every conversation under the project (<c>&lt;root&gt;\agent\chats</c>). Provider/model profiles live
/// in Settings; only the per-session Auto-approve toggle stays here.
/// </summary>
public sealed class AgentTab : UserControl, IDocView
{
    readonly MainWindow _owner;
    readonly AppConfig _config;
    readonly AgentBridge _bridge;

    readonly RichTextBox _transcript = new()
    {
        IsReadOnly = true,
        IsDocumentEnabled = true,
        BorderThickness = new Thickness(0),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(12, 8, 12, 8),
    };
    readonly TextBox _input;
    readonly CheckBox _autoApprove;
    readonly Button _send;
    readonly Button _stop;
    readonly TextBlock _status;
    readonly TextBlock _profileText = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Ellipse _dot = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly ListBox _sessions = new() { BorderThickness = new Thickness(0) };
    readonly ObservableCollection<AgentChat> _sessionItems = new();
    readonly GridLength _rightWidth;

    string? _sessionId;
    CancellationTokenSource? _turnCts;
    AgentChat _chat = new();
    string? _chatRoot;
    bool _rendering;
    bool _suppressSel;

    public ToolDef Tool { get; } = new() { Name = "AI agent", Kind = ToolKind.Windows, Path = "" };
    public string Cwd { get; private set; }
    public TerminalHost? Terminal => null;
    public string ExportName => "AI-agent";
    public bool HasRunning => _turnCts != null;
    public int RunningCount => _turnCts != null ? 1 : 0;

    public event Action<IDocView>? CwdChanged;
    public event Action<IDocView>? RunningChanged;

    public AgentTab(MainWindow owner, AppConfig config)
    {
        _owner = owner;
        _config = config;
        var server = owner.EnsureControlServer();
        _bridge = new AgentBridge(server.Url, server.Token);
        _chatRoot = owner.CurrentProjectRoot;
        Cwd = owner.CurrentProjectDir ?? AppPaths.DefaultProjectDir;
        _rightWidth = new GridLength(Math.Max(220, config.HistoryWidth));

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 54,
            MaxHeight = 160,
            Padding = new Thickness(6),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _input.PreviewKeyDown += Input_PreviewKeyDown;

        _autoApprove = new CheckBox { Content = "Auto-approve actions", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), IsChecked = config.AgentAutoApprove, ToolTip = "Run/edit actions without asking each time" };
        _autoApprove.Checked += (_, _) => SetAutoApprove(true);
        _autoApprove.Unchecked += (_, _) => SetAutoApprove(false);
        owner.AgentAutoApprove = config.AgentAutoApprove;

        _send = new Button { Content = "Send", Width = 90, Padding = new Thickness(0, 6, 0, 6) };
        _send.Click += (_, _) => Send();
        _stop = new Button { Content = "Stop", Width = 70, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        _stop.Click += (_, _) => _turnCts?.Cancel();
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        _transcript.Document = new System.Windows.Documents.FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 13, PagePadding = new Thickness(2) };

        Content = BuildLayout();
        RefreshProfileText();
        RefreshClaudeStatus();
        Hello();
        ReloadSessions();
    }

    // ───────────────────────── layout ─────────────────────────

    UIElement BuildLayout()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = _rightWidth, MinWidth = 180 });

        // Center: top bar + transcript + input
        var center = new DockPanel();
        var top = new Border { Padding = new Thickness(10, 5, 10, 5) };
        top.SetResourceReference(Border.BackgroundProperty, "ChromeBg");
        top.SetResourceReference(Border.BorderBrushProperty, "ChromeBorder");
        top.BorderThickness = new Thickness(0, 0, 0, 1);
        var topBar = new DockPanel();
        topBar.Children.Add(_dot);
        DockPanel.SetDock(_dot, Dock.Left);
        var profLabel = new TextBlock { Text = "Profile: ", VerticalAlignment = VerticalAlignment.Center };
        profLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        topBar.Children.Add(profLabel);
        DockPanel.SetDock(profLabel, Dock.Left);
        topBar.Children.Add(_profileText);
        DockPanel.SetDock(_profileText, Dock.Left);
        topBar.Children.Add(_autoApprove);
        DockPanel.SetDock(_autoApprove, Dock.Left);
        var scratchBtn = new Button { Content = "Scratch folder", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Open the folder where the agent keeps any files it creates (scripts, screenshots, temp). Safe to clear." };
        scratchBtn.Click += (_, _) => OpenScratch();
        DockPanel.SetDock(scratchBtn, Dock.Right);
        topBar.Children.Add(scratchBtn);
        topBar.Children.Add(new TextBlock()); // filler
        top.Child = topBar;
        DockPanel.SetDock(top, Dock.Top);
        center.Children.Add(top);

        var bottom = new Border { Padding = new Thickness(8, 6, 8, 8) };
        bottom.SetResourceReference(Border.BackgroundProperty, "CommandBarBg");
        bottom.SetResourceReference(Border.BorderBrushProperty, "ChromeBorder");
        bottom.BorderThickness = new Thickness(0, 1, 0, 0);
        var bar = new DockPanel();
        var sendPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 0) };
        sendPanel.Children.Add(_send);
        sendPanel.Children.Add(_stop);
        DockPanel.SetDock(sendPanel, Dock.Right);
        bar.Children.Add(sendPanel);
        bar.Children.Add(_input);
        bottom.Child = bar;
        DockPanel.SetDock(bottom, Dock.Bottom);
        center.Children.Add(bottom);

        var statusBar = new Border { Padding = new Thickness(10, 3, 10, 3) };
        statusBar.Child = _status;
        DockPanel.SetDock(statusBar, Dock.Bottom);
        center.Children.Add(statusBar);

        center.Children.Add(_transcript);
        grid.Children.Add(center);

        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.SetResourceReference(GridSplitter.BackgroundProperty, "ChromeBg");
        splitter.DragCompleted += (_, _) => _config.HistoryWidth = grid.ColumnDefinitions[2].ActualWidth;
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        // Right: sessions
        var right = new DockPanel();
        right.SetResourceReference(Panel.BackgroundProperty, "PanelBg");
        var header = new Border { Padding = new Thickness(8, 4, 8, 4) };
        header.SetResourceReference(Border.BackgroundProperty, "ChromeBg");
        header.SetResourceReference(Border.BorderBrushProperty, "ChromeBorder");
        header.BorderThickness = new Thickness(0, 0, 0, 1);
        var headBar = new DockPanel();
        var newBtn = new Button { Content = "+ New", Padding = new Thickness(8, 1, 8, 1) };
        newBtn.Click += (_, _) => NewChat();
        DockPanel.SetDock(newBtn, Dock.Right);
        headBar.Children.Add(newBtn);
        var headText = new TextBlock { Text = "Chats", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        headBar.Children.Add(headText);
        header.Child = headBar;
        DockPanel.SetDock(header, Dock.Top);
        right.Children.Add(header);

        _sessions.ItemsSource = _sessionItems;
        _sessions.ItemTemplate = SessionTemplate();
        _sessions.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_sessions, ScrollBarVisibility.Disabled);
        _sessions.SelectionChanged += Sessions_SelectionChanged;
        _sessions.ContextMenuOpening += Sessions_ContextMenuOpening;
        right.Children.Add(_sessions);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    static DataTemplate SessionTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(4, 3, 4, 3));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AgentChat.Title)));
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var meta = new FrameworkElementFactory(typeof(TextBlock));
        meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AgentChat.Updated)) { StringFormat = "MMM dd HH:mm" });
        meta.SetValue(TextBlock.FontSizeProperty, 11.0);
        meta.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        panel.AppendChild(title);
        panel.AppendChild(meta);
        return new DataTemplate { VisualTree = panel };
    }

    // ───────────────────────── sessions ─────────────────────────

    void ReloadSessions()
    {
        _sessionItems.Clear();
        if (_chatRoot == null) return;
        foreach (var c in AgentChatStore.List(_chatRoot)) _sessionItems.Add(c);
        SelectCurrentInList();
    }

    void SelectCurrentInList()
    {
        _suppressSel = true;
        _sessions.SelectedItem = _sessionItems.FirstOrDefault(c => c.Id == _chat.Id);
        _suppressSel = false;
    }

    void Sessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSel) return;
        if (_sessions.SelectedItem is AgentChat c && c.Id != _chat.Id) OpenChat(c);
    }

    void Sessions_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_sessions.SelectedItem is not AgentChat c) { e.Handled = true; return; }
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => OpenChat(c);
        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => RenameChat(c);
        var del = new MenuItem { Header = "Delete" };
        del.Click += (_, _) => DeleteChat(c);
        menu.Items.Add(open);
        menu.Items.Add(rename);
        menu.Items.Add(del);
        _sessions.ContextMenu = menu;
    }

    void OpenChat(AgentChat c)
    {
        if (_chatRoot == null) return;
        var full = AgentChatStore.Load(_chatRoot, c.Id) ?? c;
        _chat = full;
        _sessionId = full.SessionId;
        RenderChat(full);
        SetStatus($"Opened “{full.Title}”.");
        SelectCurrentInList();
    }

    void RenameChat(AgentChat c)
    {
        var name = PromptWindow.Ask(Window.GetWindow(this), "Rename chat", "New title:", c.Title);
        if (name == null) return;
        c.Title = name.Trim();
        if (_chatRoot != null) { AgentChatStore.Save(_chatRoot, c); ReloadSessions(); }
        if (c.Id == _chat.Id) _chat.Title = c.Title;
    }

    void DeleteChat(AgentChat c)
    {
        if (MessageBox.Show(Window.GetWindow(this), $"Delete chat “{c.Title}”?", "AI agent", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (_chatRoot != null) AgentChatStore.Delete(_chatRoot, c.Id);
        if (c.Id == _chat.Id) NewChat();
        else ReloadSessions();
    }

    void SaveChat()
    {
        if (_chatRoot == null || _chat.Entries.Count == 0) return;
        _chat.SessionId = _sessionId ?? _chat.SessionId;
        _chat.Updated = DateTime.Now;
        try { AgentChatStore.Save(_chatRoot, _chat); } catch { }
        var existing = _sessionItems.FirstOrDefault(c => c.Id == _chat.Id);
        if (existing == null) { _sessionItems.Insert(0, _chat); SelectCurrentInList(); }
        else { existing.Title = _chat.Title; existing.Updated = _chat.Updated; }
    }

    // ───────────────────────── chat ─────────────────────────

    void Hello()
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        p.Inlines.Add(new Run("TermDeck AI agent. ") { FontWeight = FontWeights.SemiBold });
        p.Inlines.Add(new Run("Ask it to run tools, read run output, search history, or build an autorun chain — e.g. “run subfinder on example.com, then httpx the live hosts”. It drives TermDeck through MCP; run/edit actions ask for confirmation unless you tick Auto-approve. Chats are saved with this project (right)."));
        _transcript.Document.Blocks.Add(p);
    }

    public void NewChatPublic() => NewChat();

    void NewChat()
    {
        _sessionId = null;
        _chat = new AgentChat();
        _chatRoot = _owner.CurrentProjectRoot;
        _transcript.Document.Blocks.Clear();
        Hello();
        _suppressSel = true; _sessions.SelectedItem = null; _suppressSel = false;
        SetStatus("New conversation.");
        FocusInput();
    }

    void RenderChat(AgentChat chat)
    {
        _rendering = true;
        _transcript.Document.Blocks.Clear();
        foreach (var e in chat.Entries)
        {
            switch (e.Role)
            {
                case "user": AppendUser(e.Text); break;
                case "assistant": AppendAssistant(e.Text); break;
                case "tool_use": AppendToolUse(e.Name, e.Text); break;
                case "tool_result": AppendToolResult(e.Text, e.Error); break;
                case "error": AppendError(e.Text); break;
            }
        }
        _rendering = false;
        _transcript.ScrollToHome();
    }

    void Input_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send();
        }
    }

    async void Send()
    {
        if (_turnCts != null) return;
        var prompt = _input.Text.Trim();
        if (prompt.Length == 0) return;
        if (AgentBridge.FindClaude() == null)
        {
            RefreshClaudeStatus();
            AppendError("Claude CLI not found. Install Claude Code and sign in (claude /login), or set a provider in Settings → AI agent.");
            return;
        }

        _input.Clear();
        AppendUser(prompt);

        _turnCts = new CancellationTokenSource();
        var ct = _turnCts.Token;
        Busy(true);
        RunningChanged?.Invoke(this);
        SetStatus("Thinking…");

        var profile = AgentProfiles.Active(AgentProfiles.Load());
        _chat.Profile = profile.Name;
        _chat.Model = profile.Model;
        if (string.IsNullOrEmpty(_chat.Title)) _chat.Title = prompt.Length > 60 ? prompt[..60] + "…" : prompt;
        SaveChat();

        var model = string.IsNullOrWhiteSpace(profile.Model) ? null : profile.Model.Trim();
        var env = profile.BuildEnv();
        var session = _sessionId;
        var projectDir = _owner.CurrentProjectDir;
        var scratchDir = ScratchDir();
        try
        {
            await _bridge.RunTurnAsync(prompt, model, session, env, projectDir, scratchDir,
                ev => Dispatcher.BeginInvoke(() => OnEvent(ev)), ct);
        }
        catch (Exception ex) { AppendError(ex.Message); }
        finally
        {
            _turnCts = null;
            Busy(false);
            RunningChanged?.Invoke(this);
            SetStatus("Ready.");
            SaveChat();
        }
    }

    void OnEvent(AgentEvent ev)
    {
        switch (ev)
        {
            case AgentSession s: _sessionId = s.SessionId; break;
            case AgentText t: AppendAssistant(t.Text); break;
            case AgentToolUse u: AppendToolUse(u.Name, u.Input); SetStatus("Running " + u.Name + "…"); break;
            case AgentToolResult r: AppendToolResult(r.Text, r.IsError); SetStatus("Thinking…"); break;
            case AgentDone d: if (!d.Ok) AppendError(d.Error ?? "error"); break;
        }
    }

    void Busy(bool busy)
    {
        _send.IsEnabled = !busy;
        _stop.IsEnabled = busy;
    }

    // ───────────────────────── rendering ─────────────────────────

    Brush Res(string key) => (Brush)FindResource(key);
    FontFamily Mono => (FontFamily)FindResource("MonoFont");

    void AppendUser(string text)
    {
        var p = new Paragraph { Margin = new Thickness(0, 10, 0, 2) };
        p.Inlines.Add(new Run("You") { FontWeight = FontWeights.Bold, Foreground = Res("Accent") });
        _transcript.Document.Blocks.Add(p);
        foreach (var b in Md.ToBlocks(text, Mono, Res("ChromeBg"))) _transcript.Document.Blocks.Add(b);
        Record("user", "", text);
        ScrollEnd();
    }

    void AppendAssistant(string text)
    {
        var head = new Paragraph { Margin = new Thickness(0, 10, 0, 2) };
        head.Inlines.Add(new Run("Agent") { FontWeight = FontWeights.Bold });
        _transcript.Document.Blocks.Add(head);
        foreach (var b in Md.ToBlocks(text, Mono, Res("ChromeBg"))) _transcript.Document.Blocks.Add(b);
        Record("assistant", "", text);
        ScrollEnd();
    }

    void AppendToolUse(string name, string input)
    {
        Record("tool_use", name, input);
        var shown = input.Length > 300 ? input[..300] + "…" : input;
        var p = new Paragraph { Margin = new Thickness(10, 4, 0, 2), FontFamily = Mono, FontSize = 12, Foreground = Res("TextMuted") };
        p.Inlines.Add(new Run("⚙ " + name + " ") { FontWeight = FontWeights.SemiBold });
        p.Inlines.Add(new Run(shown));
        _transcript.Document.Blocks.Add(p);
        ScrollEnd();
    }

    void AppendToolResult(string text, bool isError)
    {
        Record("tool_result", "", text, isError);
        var preview = text.Replace("\r", "").Trim();
        var lines = preview.Split('\n');
        var firstLines = string.Join("\n", lines[..Math.Min(6, lines.Length)]);
        if (firstLines.Length > 400) firstLines = firstLines[..400] + "…";
        var p = new Paragraph { Margin = new Thickness(10, 0, 0, 4), FontFamily = Mono, FontSize = 12 };
        p.Foreground = isError ? Brushes.IndianRed : Res("TextMuted");
        p.Inlines.Add(new Run("↳ " + (isError ? "error: " : "") + (firstLines.Length == 0 ? "(no output)" : firstLines)));
        _transcript.Document.Blocks.Add(p);
        ScrollEnd();
    }

    void AppendError(string text)
    {
        Record("error", "", text, true);
        var p = new Paragraph { Margin = new Thickness(0, 6, 0, 4) };
        p.Inlines.Add(new Run("⚠ " + text) { Foreground = Brushes.IndianRed });
        _transcript.Document.Blocks.Add(p);
        ScrollEnd();
    }

    void Record(string role, string name, string text, bool error = false)
    {
        if (_rendering) return;
        _chat.Entries.Add(new AgentChatEntry { Role = role, Name = name, Text = text, Error = error });
    }

    void ScrollEnd() => _transcript.ScrollToEnd();
    void SetStatus(string text) => _status.Text = text;

    void SetAutoApprove(bool on)
    {
        _owner.AgentAutoApprove = on;
        _config.AgentAutoApprove = on;
    }

    void RefreshProfileText()
    {
        var p = AgentProfiles.Active(AgentProfiles.Load());
        _profileText.Text = p.Name + (string.IsNullOrWhiteSpace(p.Model) ? "" : $" · {p.Model}");
        _profileText.ToolTip = "Change provider/model profiles in Settings → AI agent";
    }

    /// <summary>Per-project scratch folder for anything the agent writes at runtime (kept with the project).</summary>
    string ScratchDir()
    {
        var root = _owner.CurrentProjectRoot;
        return root != null
            ? System.IO.Path.Combine(root, "agent", "scratch")
            : System.IO.Path.Combine(AppPaths.DataDir, "agent", "scratch");
    }

    void OpenScratch()
    {
        var dir = ScratchDir();
        try { System.IO.Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\""); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "AI agent"); }
    }

    void RefreshClaudeStatus()
    {
        var found = AgentBridge.FindClaude() != null;
        _dot.Fill = found ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        _dot.ToolTip = found ? "Claude CLI found" : "Claude CLI not found — install it or set a provider in Settings";
    }

    // ───────────────────────── IDocView ─────────────────────────

    public void SetCwd(string dir, bool raise) { }
    public void InsertText(string text)
    {
        var i = _input.CaretIndex;
        _input.Text = _input.Text.Insert(i, text);
        _input.CaretIndex = i + text.Length;
        _input.Focus();
    }
    public void FocusInput() => _input.Focus();
    public void OpenFind() { }
    public void ApplyFont() { }
    public void KillAll() => _turnCts?.Cancel();

    /// <summary>Called when the active profile or auto-approve may have changed in Settings.</summary>
    public void RefreshFromSettings()
    {
        RefreshProfileText();
        _autoApprove.IsChecked = _config.AgentAutoApprove;
        _owner.AgentAutoApprove = _config.AgentAutoApprove;
    }

    public void Dispose()
    {
        _turnCts?.Cancel();
        SaveChat();
    }
}
