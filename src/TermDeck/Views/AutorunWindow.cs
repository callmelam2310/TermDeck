using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TermDeck.Core;

namespace TermDeck.Views;

/// <summary>
/// Autorun rules: list on the left, the selected rule on the right — source tool and trigger, what to read,
/// the filter (as editable steps or as a script, both the same text), the target tool and its argument template,
/// and a live preview on a past run of the source tool. Edits a copy of the rules; OK hands them back.
/// </summary>
public sealed class AutorunWindow : Window
{
    readonly HistoryStore? _store;
    readonly List<ToolDef> _tools;
    readonly ObservableCollection<RuleRow> _rows = new();
    readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    readonly Grid _editor = new();
    readonly TextBlock _empty = new() { Text = "No rule selected. Click New to create one.", Margin = new Thickness(20), Foreground = Brushes.Gray };

    // Editor fields
    readonly TextBox _name = Box();
    readonly CheckBox _enabled = new() { Content = "Enabled", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly ComboBox _from = ToolCombo();
    readonly ComboBox _trigger = new() { Width = 250 };
    readonly RadioButton _srcOutput = new() { Content = "Terminal output", GroupName = "src", IsChecked = true };
    readonly RadioButton _srcFile = new() { Content = "File:", GroupName = "src", Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _srcPath = Box(mono: true);
    readonly StackPanel _steps = new();
    readonly TextBox _script = Box(mono: true, multiline: true);
    readonly TabControl _filterTabs = new() { Height = 210 };
    readonly ComboBox _to = ToolCombo();
    readonly TextBox _template = Box(mono: true);
    readonly CheckBox _confirm = new() { Content = "Ask before running", VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _parallel = new() { Width = 40, Margin = new Thickness(4, 0, 14, 0), Padding = new Thickness(2) };
    readonly TextBox _maxItems = new() { Width = 55, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(2) };
    readonly TextBlock _help = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };

    // Preview — paste or load the source tool's output into the (editable) Input box, then Dry run.
    readonly Button _loadRun = new() { Content = "Load output from a run ▾", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _dryRun = new() { Content = "▶ Dry run", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0), FontWeight = FontWeights.SemiBold };
    readonly TextBox _input = Box(mono: true, multiline: true);
    readonly TextBox _result = Box(mono: true, multiline: true, readOnly: true);
    readonly TextBlock _inputHeader = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _resultHeader = new() { FontWeight = FontWeights.SemiBold };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    readonly DispatcherTimer _previewTimer;
    CancellationTokenSource? _previewCts;
    /// <summary>Run that supplies placeholder context ({{cwd}}, {{arg:-o}}…) for the preview; null = a synthetic run on the project folder.</summary>
    RunRecord? _sampleRun;
    /// <summary>True once the user edits the Input box by hand, so selecting a rule no longer overwrites it.</summary>
    bool _inputDirty;
    RuleRow? _current;
    bool _loading;

    public List<AutoRule> Rules => _rows.Select(r => r.Rule).ToList();

    /// <param name="select">Rule to show first; <paramref name="newFrom"/> creates a new rule from a run instead.</param>
    public AutorunWindow(IEnumerable<AutoRule> rules, IEnumerable<ToolDef> tools, HistoryStore? store,
        string? select = null, (ToolDef Tool, RunRecord Run)? newFrom = null)
    {
        _store = store;
        _tools = tools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var r in rules) _rows.Add(new RuleRow(r.Clone(), this));

        Title = "Autorun rules";
        Width = 1180;
        Height = 820;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ChromeBg");

        _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => { _previewTimer!.Stop(); _ = PreviewAsync(); }, Dispatcher);
        _previewTimer.Stop();

        foreach (var t in _tools) { _from.Items.Add(t); _to.Items.Add(t); }
        _trigger.Items.Add("finishes successfully (exit 0)");
        _trigger.Items.Add("finishes (any exit code)");
        _trigger.Items.Add("fails (exit ≠ 0)");

        Content = BuildLayout();
        WireEditor();

        _list.ItemsSource = _rows;
        _list.SelectionChanged += (_, _) => Select(_list.SelectedItem as RuleRow);
        if (newFrom is { } nf) NewRule(nf.Tool, nf.Run);
        else _list.SelectedItem = _rows.FirstOrDefault(r => r.Rule.Id == select) ?? _rows.FirstOrDefault();
        if (_list.SelectedItem == null) Select(null);
        Closed += (_, _) => { _previewTimer.Stop(); _previewCts?.Cancel(); };
    }

    // ───────────────────────── Layout ─────────────────────────

    UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Left: rules
        var left = new DockPanel();
        var leftButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        leftButtons.Children.Add(SmallButton("New", () => NewRule(null, null)));
        leftButtons.Children.Add(SmallButton("Duplicate", Duplicate));
        leftButtons.Children.Add(SmallButton("Delete", DeleteCurrent));
        leftButtons.Children.Add(SmallButton("↑", () => Move(-1)));
        leftButtons.Children.Add(SmallButton("↓", () => Move(1)));
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        _list.ItemTemplate = RuleTemplate();
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var listBorder = new Border { BorderThickness = new Thickness(1), Child = _list };
        listBorder.SetResourceReference(Border.BorderBrushProperty, "ChromeBorder");
        listBorder.SetResourceReference(Border.BackgroundProperty, "PanelBg");
        left.Children.Add(listBorder);
        root.Children.Add(left);

        // Right: editor (scrolls when the window is small)
        BuildEditor();
        var right = new Grid();
        right.Children.Add(new ScrollViewer { Content = _editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        right.Children.Add(_empty);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);

        // Bottom buttons
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => { if (Validate()) DialogResult = true; };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 1);
        Grid.SetColumnSpan(buttons, 3);
        root.Children.Add(buttons);
        return root;
    }

    void BuildEditor()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };

        // Rule
        var nameRow = new DockPanel();
        DockPanel.SetDock(_enabled, Dock.Right);
        nameRow.Children.Add(_enabled);
        nameRow.Children.Add(_name);
        var rulePanel = new StackPanel();
        rulePanel.Children.Add(Label("Name"));
        rulePanel.Children.Add(nameRow);
        var when = Row(Label("When", 50), _from, Label("  ", 0), _trigger);
        when.Margin = new Thickness(0, 8, 0, 0);
        rulePanel.Children.Add(when);
        stack.Children.Add(Group("1 · Trigger", rulePanel, 0));

        // Source
        var srcRow = new DockPanel();
        srcRow.Children.Add(_srcOutput);
        _srcOutput.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_srcOutput, Dock.Left);
        srcRow.Children.Add(_srcFile);
        DockPanel.SetDock(_srcFile, Dock.Left);
        srcRow.Children.Add(_srcPath);
        var srcPanel = new StackPanel();
        srcPanel.Children.Add(srcRow);
        srcPanel.Children.Add(Hint("File path: absolute, relative to the run's directory, or with {{cwd}}, {{project}}, {{arg:-o}} (the value after -o in the run's arguments). Linux paths work for WSL tools."));
        stack.Children.Add(Group("2 · Read", srcPanel, 10));

        // Filter
        var stepsHost = new DockPanel();
        var addStep = new Button { Content = "+ Add step ▾", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        addStep.Click += (_, _) => ShowAddStepMenu(addStep);
        DockPanel.SetDock(addStep, Dock.Bottom);
        stepsHost.Children.Add(addStep);
        stepsHost.Children.Add(new ScrollViewer { Content = _steps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _filterTabs.Items.Add(new TabItem { Header = "Steps", Content = stepsHost, Padding = new Thickness(10, 2, 10, 2) });
        _filterTabs.Items.Add(new TabItem { Header = "Script", Content = _script, Padding = new Thickness(10, 2, 10, 2) });
        var filterPanel = new StackPanel();
        filterPanel.Children.Add(_filterTabs);
        filterPanel.Children.Add(_help);
        stack.Children.Add(Group("3 · Filter (applied top to bottom; empty = every non-blank line)", filterPanel, 10));

        // Target
        var insert = new Button { Content = "{{ }} ▾", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Insert a placeholder" };
        insert.Click += (_, _) => ShowPlaceholderMenu(insert);
        var tplRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(insert, Dock.Right);
        tplRow.Children.Add(insert);
        tplRow.Children.Add(_template);
        var opts = Row(_confirm, Label("     Parallel runs (per value)", 0), _parallel, Label("Max values", 0), _maxItems);
        opts.Margin = new Thickness(0, 8, 0, 0);
        var targetPanel = new StackPanel();
        targetPanel.Children.Add(Row(Label("Run", 50), _to, Label("  with arguments:", 0)));
        targetPanel.Children.Add(tplRow);
        targetPanel.Children.Add(Hint("{{line}} runs the tool once per value · {{file}} passes a file with all values (e.g. httpx -l {{file}}) · {{lines}} / {{csv}} put them all on one line. Values are quoted so tool output cannot inject shell commands."));
        targetPanel.Children.Add(opts);
        stack.Children.Add(Group("4 · Run next", targetPanel, 10));

        // Preview: left = editable Input (the source tool's output); right = the filtered values that go into the next tool.
        var panes = new Grid { Height = 240, Margin = new Thickness(0, 8, 0, 0) };
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        var inPane = new DockPanel();
        _inputHeader.Text = "Input";
        var inTop = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(_dryRun, Dock.Right);
        DockPanel.SetDock(_loadRun, Dock.Right);
        inTop.Children.Add(_dryRun);
        inTop.Children.Add(_loadRun);
        inTop.Children.Add(_inputHeader);
        DockPanel.SetDock(inTop, Dock.Top);
        inPane.Children.Add(inTop);
        inPane.Children.Add(_input);
        var outPane = new DockPanel();
        DockPanel.SetDock(_resultHeader, Dock.Top);
        outPane.Children.Add(_resultHeader);
        outPane.Children.Add(_result);
        Grid.SetColumn(outPane, 2);
        panes.Children.Add(inPane);
        panes.Children.Add(outPane);
        var previewPanel = new StackPanel();
        previewPanel.Children.Add(Hint("Paste the source tool's output below (or load it from a past run), then Dry run to see the filtered values and the exact commands they produce."));
        previewPanel.Children.Add(panes);
        previewPanel.Children.Add(_status);
        stack.Children.Add(Group("Preview / dry run", previewPanel, 10));

        _editor.Children.Add(stack);
    }

    void WireEditor()
    {
        _name.TextChanged += (_, _) => Edit(r => r.Name = _name.Text);
        _enabled.Click += (_, _) => Edit(r => r.Enabled = _enabled.IsChecked == true);
        _from.SelectionChanged += (_, _) =>
        {
            Edit(r => r.FromToolId = (_from.SelectedItem as ToolDef)?.Id ?? "");
            if (!_loading) AutoLoadInput();
        };
        _trigger.SelectionChanged += (_, _) => Edit(r => r.Trigger = (AutoTrigger)Math.Max(0, _trigger.SelectedIndex));
        _srcOutput.Checked += (_, _) => { Edit(r => r.Source = AutoSource.Output); _srcPath.IsEnabled = false; };
        _srcFile.Checked += (_, _) => { Edit(r => r.Source = AutoSource.File); _srcPath.IsEnabled = true; };
        _srcPath.TextChanged += (_, _) => Edit(r => r.SourceFile = _srcPath.Text);
        _script.TextChanged += (_, _) => Edit(r => r.Filter = _script.Text);
        _filterTabs.SelectionChanged += (s, e) =>
        {
            if (e.OriginalSource != _filterTabs) return;
            if (_filterTabs.SelectedIndex == 0) BuildStepRows();
        };
        _to.SelectionChanged += (_, _) => Edit(r => r.ToToolId = (_to.SelectedItem as ToolDef)?.Id ?? "");
        _template.TextChanged += (_, _) => Edit(r => r.ArgsTemplate = _template.Text);
        _confirm.Click += (_, _) => Edit(r => r.Confirm = _confirm.IsChecked == true);
        _parallel.TextChanged += (_, _) => Edit(r => r.MaxParallel = int.TryParse(_parallel.Text, out var n) ? Math.Clamp(n, 1, 32) : 1);
        _maxItems.TextChanged += (_, _) => Edit(r => r.MaxItems = int.TryParse(_maxItems.Text, out var n) ? Math.Max(0, n) : 500);
        _input.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _inputDirty = true;
            _previewTimer.Stop();
            _previewTimer.Start();
        };
        _dryRun.Click += (_, _) => _ = DryRunAsync();
        _loadRun.Click += (_, _) => ShowLoadRunMenu();
    }

    /// <summary>Applies an editor change to the current rule and schedules a preview.</summary>
    void Edit(Action<AutoRule> change)
    {
        if (_loading || _current == null) return;
        change(_current.Rule);
        _current.Refresh();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    // ───────────────────────── Rules list ─────────────────────────

    void Select(RuleRow? row)
    {
        _current = row;
        _editor.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
        if (row == null) return;

        var r = row.Rule;
        _loading = true;
        _name.Text = r.Name;
        _enabled.IsChecked = r.Enabled;
        _from.SelectedItem = _tools.FirstOrDefault(t => t.Id == r.FromToolId);
        _trigger.SelectedIndex = (int)r.Trigger;
        _srcOutput.IsChecked = r.Source == AutoSource.Output;
        _srcFile.IsChecked = r.Source == AutoSource.File;
        _srcPath.IsEnabled = r.Source == AutoSource.File;
        _srcPath.Text = r.SourceFile;
        _script.Text = r.Filter;
        _to.SelectedItem = _tools.FirstOrDefault(t => t.Id == r.ToToolId);
        _template.Text = r.ArgsTemplate;
        _confirm.IsChecked = r.Confirm;
        _parallel.Text = r.MaxParallel.ToString();
        _maxItems.Text = r.MaxItems.ToString();
        _loading = false;
        _inputDirty = false;
        BuildStepRows();
        AutoLoadInput();
    }

    void NewRule(ToolDef? from, RunRecord? run)
    {
        var rule = new AutoRule
        {
            Name = from != null ? $"{from.Name} → next" : "New rule",
            FromToolId = from?.Id ?? (_tools.FirstOrDefault()?.Id ?? ""),
            Filter = "# keep lines that look like a domain, e.g.\n# grep -o -i [a-z0-9.-]+\\.[a-z]{2,}\nuniq",
            ArgsTemplate = "{{line}}",
        };
        var row = new RuleRow(rule, this);
        _rows.Add(row);
        _list.SelectedItem = row;
        if (run != null) _ = LoadRunText(run);
        _name.Focus();
        _name.SelectAll();
    }

    void Duplicate()
    {
        if (_current == null) return;
        var copy = _current.Rule.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name += " (copy)";
        var row = new RuleRow(copy, this);
        _rows.Insert(_rows.IndexOf(_current) + 1, row);
        _list.SelectedItem = row;
    }

    void DeleteCurrent()
    {
        if (_current == null) return;
        if (MessageBox.Show(this, $"Delete the rule \"{_current.Rule.Name}\"?", "Autorun", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var i = _rows.IndexOf(_current);
        _rows.Remove(_current);
        _list.SelectedItem = _rows.Count == 0 ? null : _rows[Math.Min(i, _rows.Count - 1)];
        if (_rows.Count == 0) Select(null);
    }

    void Move(int step)
    {
        if (_current == null) return;
        var i = _rows.IndexOf(_current);
        var j = i + step;
        if (j < 0 || j >= _rows.Count) return;
        _rows.Move(i, j);
        _list.SelectedItem = _current;
    }

    bool Validate()
    {
        foreach (var row in _rows)
        {
            var r = row.Rule;
            string? problem = null;
            if (string.IsNullOrWhiteSpace(r.Name)) problem = "has no name";
            else if (_tools.All(t => t.Id != r.FromToolId)) problem = "has no source tool";
            else if (_tools.All(t => t.Id != r.ToToolId)) problem = "has no tool to run next";
            else if (r.Source == AutoSource.File && string.IsNullOrWhiteSpace(r.SourceFile)) problem = "reads a file but no path is set";
            else
            {
                try { FilterScript.Parse(r.Filter); }
                catch (AutorunException ex) { problem = $"has a filter error on line {ex.Line}: {ex.Message}"; }
            }
            if (problem == null) continue;
            _list.SelectedItem = row;
            MessageBox.Show(this, $"The rule \"{(r.Name.Length > 0 ? r.Name : "(unnamed)")}\" {problem}.", "Autorun", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    // ───────────────────────── Filter steps ─────────────────────────

    /// <summary>Rebuilds the step rows from the script text (the script is the single source of truth).</summary>
    void BuildStepRows()
    {
        _steps.Children.Clear();
        List<FilterStep> steps;
        try { steps = FilterScript.Parse(_script.Text); }
        catch (AutorunException ex)
        {
            _steps.Children.Add(new TextBlock
            {
                Text = $"The script has an error on line {ex.Line}: {ex.Message}\nFix it in the Script tab.",
                Foreground = Brushes.IndianRed, Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        if (steps.Count == 0)
            _steps.Children.Add(new TextBlock { Text = "No steps: every non-blank line is a value.", Foreground = Brushes.Gray, Margin = new Thickness(4) });
        foreach (var s in steps) _steps.Children.Add(StepRow(s.Op, s.Args));
    }

    UIElement StepRow(string op, string args)
    {
        var combo = new ComboBox { Width = 96, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var o in FilterScript.Ops) combo.Items.Add(o);
        combo.SelectedItem = op;
        var box = Box(mono: true);
        box.Text = args;
        box.ToolTip = FilterScript.Help.GetValueOrDefault(op);
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Tag = (combo, box) };
        foreach (var (glyph, tip, act) in new (string, string, Action)[]
                 {
                     ("", "Remove step", () => { _steps.Children.Remove(row); StepsChanged(); }),
                     ("", "Move down", () => MoveStep(row, 1)),
                     ("", "Move up", () => MoveStep(row, -1)),
                 })
        {
            var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)FindResource("IconButton"), Height = 24, Margin = new Thickness(2, 0, 0, 0) };
            b.Click += (_, _) => act();
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
        }
        DockPanel.SetDock(combo, Dock.Left);
        row.Children.Add(combo);
        row.Children.Add(box);
        combo.SelectionChanged += (_, _) =>
        {
            box.ToolTip = FilterScript.Help.GetValueOrDefault(combo.SelectedItem as string ?? "");
            ShowHelp(combo.SelectedItem as string);
            StepsChanged();
        };
        box.TextChanged += (_, _) => StepsChanged();
        box.GotKeyboardFocus += (_, _) => ShowHelp(combo.SelectedItem as string);
        return row;
    }

    void MoveStep(DockPanel row, int step)
    {
        var i = _steps.Children.IndexOf(row);
        var j = i + step;
        if (j < 0 || j >= _steps.Children.Count) return;
        _steps.Children.RemoveAt(i);
        _steps.Children.Insert(j, row);
        StepsChanged();
    }

    /// <summary>Step rows → script text (which updates the rule through its TextChanged).</summary>
    void StepsChanged()
    {
        var steps = _steps.Children.OfType<DockPanel>()
            .Select(r => ((ValueTuple<ComboBox, TextBox>)r.Tag))
            .Select(t => (Op: t.Item1.SelectedItem as string ?? "grep", Args: t.Item2.Text.Trim()));
        // Keep the comment lines of a hand-written script at the top.
        var comments = _script.Text.Replace("\r\n", "\n").Split('\n').TakeWhile(l => l.TrimStart().StartsWith('#')).ToList();
        var text = FilterScript.ToScript(steps);
        _script.Text = comments.Count > 0 ? string.Join("\n", comments) + (text.Length > 0 ? "\n" + text : "") : text;
    }

    void ShowHelp(string? op) => _help.Text = op != null && FilterScript.Help.TryGetValue(op, out var h) ? h : "";

    void ShowAddStepMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var examples = new (string Op, string Args, string Label)[]
        {
            ("grep", "-o -i [a-z0-9][a-z0-9.-]*\\.[a-z]{2,}", "Extract domains"),
            ("grep", "-o https?://[^\\s\"'<>]+", "Extract URLs"),
            ("grep", "-o \\b(?:\\d{1,3}\\.){3}\\d{1,3}\\b", "Extract IPv4 addresses"),
            ("grep", "-o -i \\b(?:\\d{1,3}\\.){3}\\d{1,3}:\\d+\\b", "Extract IP:port"),
            ("grep", "-i PATTERN", "Keep lines matching a regex"),
            ("grep", "-v -i PATTERN", "Drop lines matching a regex"),
            ("awk", "'{print $1}'", "Take a column (awk)"),
            ("sed", "s/^https?:\\/\\///", "Replace with a regex (sed)"),
            ("uniq", "", "Remove duplicates"),
            ("sort", "", "Sort"),
            ("head", "100", "First N lines"),
            ("lower", "", "Lowercase"),
            ("wsl", "grep -oP 'PATTERN' | sort -u", "Pipe through a WSL command"),
            ("ps", "$input | Where-Object { $_ -match 'PATTERN' }", "Pipe through PowerShell"),
        };
        foreach (var (op, args, label) in examples)
        {
            var mi = new MenuItem { Header = label, InputGestureText = op };
            mi.Click += (_, _) =>
            {
                if (_steps.Children.OfType<DockPanel>().Any() == false) _steps.Children.Clear();
                _steps.Children.Add(StepRow(op, args));
                StepsChanged();
                ShowHelp(op);
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    void ShowPlaceholderMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (token, help) in Autorun.Placeholders)
        {
            var mi = new MenuItem { Header = token, InputGestureText = help };
            mi.Click += (_, _) =>
            {
                var i = _template.CaretIndex;
                _template.Text = _template.Text.Insert(i, token);
                _template.CaretIndex = i + token.Length;
                _template.Focus();
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    // ───────────────────────── Preview ─────────────────────────

    sealed record SampleItem(RunRecord Run)
    {
        public override string ToString() =>
            $"{Run.StartedAt:MMM dd HH:mm} · exit {RunSession.DescribeExit(Run.ExitCode)} · {(Run.Args.Length > 70 ? Run.Args[..70] + "…" : Run.Args)}";
    }

    /// <summary>When a rule is selected and the user has not typed their own input, fill the Input box from the latest run of the source tool.</summary>
    void AutoLoadInput()
    {
        if (_inputDirty) { _ = PreviewAsync(); return; }
        var toolId = _current?.Rule.FromToolId;
        RunRecord? latest = null;
        if (_store != null && !string.IsNullOrEmpty(toolId))
        {
            try { latest = _store.List(toolId, 1).FirstOrDefault(r => r.EndedAt != null); } catch { }
        }
        if (latest != null) _ = LoadRunText(latest);
        else
        {
            _sampleRun = null;
            SetInputText("");
            _ = PreviewAsync();
        }
    }

    /// <summary>Drop-down of recent runs of the source tool; picking one loads its output into the Input box.</summary>
    void ShowLoadRunMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _loadRun, Placement = PlacementMode.Bottom };
        var toolId = _current?.Rule.FromToolId;
        List<RunRecord> runs = new();
        if (_store != null && !string.IsNullOrEmpty(toolId))
        {
            try { runs = _store.List(toolId, 30).Where(r => r.EndedAt != null).ToList(); } catch { }
        }
        if (runs.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No finished run of the source tool yet", IsEnabled = false });
        }
        else
        {
            foreach (var r in runs)
            {
                var mi = new MenuItem { Header = new SampleItem(r).ToString() };
                mi.Click += (_, _) => _ = LoadRunText(r);
                menu.Items.Add(mi);
            }
        }
        menu.IsOpen = true;
    }

    /// <summary>Loads a run's output (or the file it wrote) into the Input box and uses it as placeholder context.</summary>
    async Task LoadRunText(RunRecord run)
    {
        _sampleRun = run;
        var store = _store;
        var rule = _current?.Rule.Clone();
        if (store == null || rule == null) return;
        var src = _tools.FirstOrDefault(t => t.Id == rule.FromToolId);
        string text;
        try { text = await Task.Run(() => Autorun.ReadSource(rule, run, store, src)); }
        catch (AutorunException ex) { SetInputText(""); SetStatus(ex.Message, true); return; }
        catch (Exception ex) { SetInputText(""); SetStatus(ex.Message, true); return; }
        if (_sampleRun != run) return;
        SetInputText(text);
        await PreviewAsync();
    }

    void SetInputText(string text)
    {
        _loading = true;
        _input.Text = Truncate(text, 5000);
        _loading = false;
        _inputDirty = false;
    }

    /// <summary>Explicit Dry run: for a File-source rule, (re)load the file first, then filter.</summary>
    async Task DryRunAsync()
    {
        if (_current?.Rule.Source == AutoSource.File && _sampleRun != null && !_inputDirty)
            await LoadRunText(_sampleRun);
        else
            await PreviewAsync();
    }

    /// <summary>Filters the Input box text through the rule and shows the values + the commands they would produce.</summary>
    async Task PreviewAsync()
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var rule = _current?.Rule.Clone();
        if (rule == null) return;
        var store = _store;
        var src = _tools.FirstOrDefault(t => t.Id == rule.FromToolId);
        var dst = _tools.FirstOrDefault(t => t.Id == rule.ToToolId);
        var text = _input.Text;
        // Placeholder context: the loaded run, or a synthetic run on the project folder for hand-typed input.
        var projectDir = store?.ProjectDir ?? "";
        var run = _sampleRun ?? new RunRecord { Cwd = src?.Kind == ToolKind.Wsl ? PathMapper.ToLinux(projectDir) : projectDir };

        _inputHeader.Text = text.Length == 0 ? "Input (empty)" : "Input";
        _status.Text = "Filtering…";
        _status.Foreground = Brushes.Gray;
        try
        {
            var (plan, values) = await Task.Run(() =>
            {
                var vals = FilterScript.Run(text, rule.Filter, cts.Token);
                var p = dst == null || store == null ? null : Autorun.Build(rule, run, store, src, dst, vals, writeListFile: false);
                return (p, vals);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;

            _result.Text = string.Join("\n", values.Take(1000)) + (values.Count > 1000 ? $"\n… {values.Count - 1000} more" : "");
            _resultHeader.Text = values.Count == 1 ? "Result · 1 value" : $"Result · {values.Count} values";

            if (store == null) { SetStatus("Open a project to preview the produced commands.", false); return; }
            if (dst == null) { SetStatus("Choose the tool to run next (section 4) to see the commands.", false); return; }
            if (plan!.Skipped != null) { SetStatus($"Would not run {dst.Name}: {plan.Skipped}.", true); return; }
            var cmds = plan.ArgsList.Take(4).Select(a => "$ " + CommandBuilder.Build(dst, a, store.ProjectDir).Display);
            var more = plan.ArgsList.Count > 4 ? $"\n… {plan.ArgsList.Count - 4} more run(s)" : "";
            var head = plan.ArgsList.Count == 1 ? $"Would run {dst.Name} once:" : $"Would run {dst.Name} {plan.ArgsList.Count} times ({Math.Max(1, rule.MaxParallel)} at a time):";
            var dropped = plan.Dropped > 0 ? $"\n{plan.Dropped} value(s) over the limit of {rule.MaxItems} would be dropped." : "";
            SetStatus(head + "\n" + string.Join("\n", cmds) + more + dropped, false);
        }
        catch (OperationCanceledException) { }
        catch (AutorunException ex)
        {
            if (cts.IsCancellationRequested) return;
            _result.Text = "";
            _resultHeader.Text = "Result";
            SetStatus(ex.Line > 0 ? $"Filter line {ex.Line}: {ex.Message}" : ex.Message, true);
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) SetStatus(ex.Message, true);
        }
    }

    void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.FontFamily = error ? FontFamily : (FontFamily)FindResource("MonoFont");
        if (error) _status.Foreground = Brushes.IndianRed;
        else _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
    }

    static string Truncate(string s, int maxLines)
    {
        var lines = s.Split('\n');
        return lines.Length <= maxLines ? s : string.Join("\n", lines.Take(maxLines)) + $"\n… {lines.Length - maxLines} more lines";
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>Row in the rules list.</summary>
    sealed class RuleRow(AutoRule rule, AutorunWindow owner) : Observable
    {
        public AutoRule Rule { get; } = rule;
        public string Title => Rule.Name.Length > 0 ? Rule.Name : "(unnamed)";
        public string Flow => $"{owner.ToolName(Rule.FromToolId)} → {owner.ToolName(Rule.ToToolId)}";
        public double Opacity => Rule.Enabled ? 1 : 0.5;
        public void Refresh()
        {
            Raise(nameof(Title));
            Raise(nameof(Flow));
            Raise(nameof(Opacity));
        }
    }

    string ToolName(string id) => _tools.FirstOrDefault(t => t.Id == id)?.Name ?? "?";

    static DataTemplate RuleTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(4, 3, 4, 3));
        panel.SetBinding(OpacityProperty, new System.Windows.Data.Binding("Opacity"));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var flow = new FrameworkElementFactory(typeof(TextBlock));
        flow.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Flow"));
        flow.SetValue(TextBlock.FontSizeProperty, 11.0);
        flow.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        flow.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        panel.AppendChild(title);
        panel.AppendChild(flow);
        return new DataTemplate { VisualTree = panel };
    }

    static TextBox Box(bool mono = false, bool multiline = false, bool readOnly = false)
    {
        var b = new TextBox { Padding = new Thickness(3), IsReadOnly = readOnly, VerticalContentAlignment = VerticalAlignment.Center };
        if (mono) b.SetResourceReference(FontFamilyProperty, "MonoFont");
        if (multiline)
        {
            b.AcceptsReturn = true;
            b.AcceptsTab = false;
            b.TextWrapping = TextWrapping.NoWrap;
            b.VerticalContentAlignment = VerticalAlignment.Top;
            b.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            b.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            b.FontSize = 12;
        }
        return b;
    }

    static ComboBox ToolCombo() => new() { MinWidth = 220, DisplayMemberPath = nameof(ToolDef.Name) };

    static TextBlock Label(string text, double width = 0)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        if (width > 0) t.Width = width;
        return t;
    }

    static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        return t;
    }

    static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    static Button SmallButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4) };
        b.Click += (_, _) => onClick();
        return b;
    }

    static GroupBox Group(string header, UIElement content, double top)
    {
        var title = new TextBlock { Text = header, FontSize = 14, FontWeight = FontWeights.SemiBold };
        var g = new GroupBox { Header = title, Padding = new Thickness(10), Content = content, Margin = new Thickness(0, top, 0, 0) };
        g.SetResourceReference(BackgroundProperty, "PanelBg");
        return g;
    }
}
