using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TermDeck.Core;

namespace TermDeck.Views;

/// <summary>Chooses which runs go into a report (tools, dates, failed runs) and how (HTML/Markdown, output size), then saves it.</summary>
public sealed class ReportWindow : Window
{
    readonly HistoryStore _store;
    readonly List<RunRecord> _runs;
    readonly bool _preset;
    readonly TextBox _title;
    readonly RadioButton _html;
    readonly CheckBox _output;
    readonly ComboBox _maxLines;
    readonly CheckBox _failed;
    readonly StackPanel _tools = new();
    readonly DatePicker _from = new() { Width = 130 };
    readonly DatePicker _to = new() { Width = 130 };
    readonly TextBlock _summary = new() { Margin = new Thickness(0, 8, 0, 0) };
    readonly Button _export;

    /// <param name="runs">Runs chosen elsewhere (search results); null = all runs of the project, filtered here.</param>
    public ReportWindow(HistoryStore store, IReadOnlyList<RunRecord>? runs, string? title = null)
    {
        _store = store;
        _preset = runs != null;
        try { _runs = runs?.ToList() ?? store.ListAll(); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not read history: " + ex.Message, "Report", MessageBoxButton.OK, MessageBoxImage.Warning);
            _runs = new List<RunRecord>();
        }

        Title = "Export report";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ChromeBg");

        var project = Path.GetFileName(store.ProjectDir.TrimEnd('\\'));
        _title = new TextBox { Text = title == null ? $"{project} — TermDeck report" : $"{project} — {title}", Margin = new Thickness(0, 3, 0, 8), Padding = new Thickness(3) };
        _html = new RadioButton { Content = "HTML (colored output, opens in a browser, prints to PDF)", IsChecked = true, GroupName = "fmt" };
        var md = new RadioButton { Content = "Markdown (plain text output)", GroupName = "fmt", Margin = new Thickness(0, 4, 0, 0) };

        var general = new StackPanel();
        general.Children.Add(new TextBlock { Text = "Title" });
        general.Children.Add(_title);
        general.Children.Add(_html);
        general.Children.Add(md);

        // Which runs
        var runsPanel = new StackPanel();
        if (_preset)
        {
            runsPanel.Children.Add(new TextBlock { Text = $"{_runs.Count} run(s) from the search results.", TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            foreach (var g in _runs.GroupBy(r => r.ToolId).OrderByDescending(g => g.Max(r => r.Id)))
            {
                var cb = new CheckBox { Content = $"{g.Last().ToolName}  ({g.Count()})", Tag = g.Key, IsChecked = true, Margin = new Thickness(2) };
                cb.Click += (_, _) => UpdateSummary();
                _tools.Children.Add(cb);
            }
            var pick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            pick.Children.Add(new TextBlock { Text = "Tools", VerticalAlignment = VerticalAlignment.Center, Width = 60 });
            pick.Children.Add(LinkButton("All", () => SetAllTools(true)));
            pick.Children.Add(LinkButton("None", () => SetAllTools(false)));
            runsPanel.Children.Add(pick);
            var scroll = new ScrollViewer { Content = _tools, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            runsPanel.Children.Add(scroll);

            if (_runs.Count > 0)
            {
                _from.SelectedDate = _runs.Min(r => r.StartedAt).Date;
                _to.SelectedDate = _runs.Max(r => r.StartedAt).Date;
            }
            _from.SelectedDateChanged += (_, _) => UpdateSummary();
            _to.SelectedDateChanged += (_, _) => UpdateSummary();
            var dates = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            dates.Children.Add(new TextBlock { Text = "From", VerticalAlignment = VerticalAlignment.Center, Width = 60 });
            dates.Children.Add(_from);
            dates.Children.Add(new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) });
            dates.Children.Add(_to);
            runsPanel.Children.Add(dates);
        }
        _failed = new CheckBox { Content = "Include failed and interrupted runs", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        _failed.Click += (_, _) => UpdateSummary();
        runsPanel.Children.Add(_failed);
        runsPanel.Children.Add(_summary);

        // Output
        _output = new CheckBox { Content = "Include each run's output", IsChecked = true };
        _maxLines = new ComboBox { IsEditable = true, Width = 90, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var n in new[] { "50", "100", "200", "500", "1000", "All" }) _maxLines.Items.Add(n);
        _maxLines.Text = "200";
        _output.Click += (_, _) => _maxLines.IsEnabled = _output.IsChecked == true;
        var lines = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        lines.Children.Add(new TextBlock { Text = "Lines per run (long output keeps its start and end):", VerticalAlignment = VerticalAlignment.Center });
        lines.Children.Add(_maxLines);
        var outputPanel = new StackPanel();
        outputPanel.Children.Add(_output);
        outputPanel.Children.Add(lines);

        _export = new Button { Content = "Export...", Width = 100, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        _export.Click += async (_, _) => await ExportAsync();
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_export);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(Group("Report", general, 0));
        root.Children.Add(Group("Runs", runsPanel, 10));
        root.Children.Add(Group("Output", outputPanel, 10));
        root.Children.Add(buttons);
        Content = root;
        UpdateSummary();
    }

    static GroupBox Group(string header, UIElement content, double top)
    {
        var title = new TextBlock { Text = header, FontSize = 14, FontWeight = FontWeights.SemiBold };
        var g = new GroupBox { Header = title, Padding = new Thickness(10), Content = content, Margin = new Thickness(0, top, 0, 0) };
        g.SetResourceReference(BackgroundProperty, "PanelBg");
        return g;
    }

    static Button LinkButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    void SetAllTools(bool on)
    {
        foreach (CheckBox cb in _tools.Children) cb.IsChecked = on;
        UpdateSummary();
    }

    List<RunRecord> SelectedRuns()
    {
        IEnumerable<RunRecord> q = _runs;
        if (!_preset)
        {
            var tools = _tools.Children.Cast<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToHashSet();
            q = q.Where(r => tools.Contains(r.ToolId));
            if (_from.SelectedDate is { } from) q = q.Where(r => r.StartedAt >= from.Date);
            if (_to.SelectedDate is { } to) q = q.Where(r => r.StartedAt < to.Date.AddDays(1));
        }
        if (_failed.IsChecked != true) q = q.Where(r => r.EndedAt != null && r.ExitCode == 0);
        return q.ToList();
    }

    void UpdateSummary()
    {
        var n = SelectedRuns().Count;
        _summary.Text = n == 1 ? "1 run will be exported." : $"{n} runs will be exported.";
        _export.IsEnabled = n > 0;
    }

    async Task ExportAsync()
    {
        var runs = SelectedRuns();
        if (runs.Count == 0) return;
        int maxLines;
        if (_maxLines.Text.Trim().Equals("All", StringComparison.OrdinalIgnoreCase)) maxLines = 0;
        else if (!int.TryParse(_maxLines.Text.Trim(), out maxLines) || maxLines < 1)
        {
            MessageBox.Show(this, "Lines per run must be a number (or All).", "Report");
            return;
        }

        var options = new ReportOptions
        {
            Format = _html.IsChecked == true ? ReportFormat.Html : ReportFormat.Markdown,
            Title = string.IsNullOrWhiteSpace(_title.Text) ? "TermDeck report" : _title.Text.Trim(),
            IncludeOutput = _output.IsChecked == true,
            MaxLines = maxLines,
        };
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save report",
            InitialDirectory = _store.ProjectDir,
            FileName = ReportBuilder.DefaultFileName(_store.ProjectDir, options.Format),
            Filter = options.Format == ReportFormat.Html ? "HTML (*.html)|*.html" : "Markdown (*.md)|*.md",
        };
        if (dlg.ShowDialog(this) != true) return;

        _export.IsEnabled = false;
        var file = dlg.FileName;
        try
        {
            var total = runs.Count;
            var content = await Task.Run(() => ReportBuilder.Build(runs, _store, options,
                done => Dispatcher.BeginInvoke(() => _summary.Text = $"Building report… {done}/{total}")));
            await File.WriteAllTextAsync(file, content, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not write the report:\n" + ex.Message, "Report", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateSummary();
            return;
        }

        if (MessageBox.Show(this, $"Report saved:\n{file}\n\nOpen it now?", "Report", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { }
        }
        DialogResult = true;
    }
}
