using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TermDeck.Core;

namespace TermDeck.Views;

/// <summary>One search result: a run plus the text around the first match in its output.</summary>
public sealed class SearchHit
{
    public SearchHit(RunRecord record, string before, string match, string after, int count)
    {
        Run = new RunItem(record, false);
        Before = before;
        Match = match;
        After = after;
        CountText = count switch { 0 => "in command", 1 => "1 match", _ => $"{count} matches" };
    }

    public RunItem Run { get; }
    public string Before { get; }
    public string Match { get; }
    public string After { get; }
    public string CountText { get; }
}

/// <summary>
/// Searches the output, arguments and command of every run in the project (including shell sessions and runs of
/// deleted tools), with a read-only preview of the selected run.
/// </summary>
public partial class SearchWindow : Window
{
    sealed record ToolChoice(string? ToolId, string Label);

    readonly HistoryStore _store;
    readonly Func<RunRecord, string?, bool> _openRun;
    readonly ObservableCollection<SearchHit> _hits = new();
    readonly DispatcherTimer _debounce;
    int _version;
    bool _indexing;
    long _previewId = -1;

    /// <param name="openRun">Opens a run in its tool tab; returns false if the tool no longer exists.</param>
    public SearchWindow(HistoryStore store, Func<RunRecord, string?, bool> openRun)
    {
        InitializeComponent();
        _store = store;
        _openRun = openRun;
        Title = $"Search history — {System.IO.Path.GetFileName(store.ProjectDir.TrimEnd('\\'))}";
        Results.ItemsSource = _hits;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => { _debounce!.Stop(); RunSearch(); }, Dispatcher);
        _debounce.Stop();
        LoadToolFilter();
        Loaded += async (_, _) => await IndexPendingAsync();
        Closed += (_, _) => Preview.Dispose();
    }

    public void FocusQuery(string? query)
    {
        if (query != null) QueryBox.Text = query;
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    string Query => QueryBox.Text.Trim();
    string? ToolId => (ToolFilter.SelectedItem as ToolChoice)?.ToolId;

    void LoadToolFilter()
    {
        var items = new List<ToolChoice> { new(null, "All tools") };
        try { items.AddRange(_store.ToolsWithRuns().Select(t => new ToolChoice(t.ToolId, $"{t.ToolName} ({t.Count})"))); }
        catch (Exception) { }
        ToolFilter.ItemsSource = items;
        ToolFilter.SelectedIndex = 0;
    }

    /// <summary>Older runs (from before search existed, or killed with the app) are indexed the first time the window opens.</summary>
    async Task IndexPendingAsync()
    {
        List<RunRecord> pending;
        try { pending = await Task.Run(_store.Unindexed); }
        catch (Exception ex)
        {
            StatusText.Text = "Could not read the search index: " + ex.Message;
            return;
        }
        if (pending.Count == 0)
        {
            SetIdleStatus();
            return;
        }
        _indexing = true;
        for (var i = 0; i < pending.Count; i++)
        {
            StatusText.Text = $"Indexing the output of older runs… {i + 1}/{pending.Count}";
            var r = pending[i];
            try { await Task.Run(() => _store.IndexOutput(r)); }
            catch (Exception) { /* unreadable log: skipped, retried next time */ }
            if (!IsLoaded) return;
        }
        _indexing = false;
        if (Query.Length > 0) RunSearch(); else SetIdleStatus();
    }

    void SetIdleStatus() =>
        StatusText.Text = "Type to search the output of every run in this project (substring, case-insensitive).";

    void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _debounce.Stop();
                RunSearch();
                e.Handled = true;
                break;
            case Key.Down when _hits.Count > 0:
                Results.SelectedIndex = Math.Max(0, Results.SelectedIndex);
                (Results.ItemContainerGenerator.ContainerFromIndex(Results.SelectedIndex) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
        }
    }

    void ToolFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) RunSearch();
    }

    async void RunSearch()
    {
        var query = Query;
        var toolId = ToolId;
        var version = ++_version;
        if (query.Length == 0)
        {
            _hits.Clear();
            UpdateButtons();
            SetIdleStatus();
            return;
        }

        StatusText.Text = "Searching…";
        List<SearchHit> hits;
        try
        {
            hits = await Task.Run(() =>
            {
                var runs = _store.Search(query, toolId);
                var snippets = _store.Snippets(runs.Select(r => r.Id).ToList(), query);
                return runs.Select(r => MakeHit(r, query, snippets)).ToList();
            });
        }
        catch (Exception ex)
        {
            if (version == _version) StatusText.Text = "Search failed: " + ex.Message;
            return;
        }
        if (version != _version) return; // a newer search started meanwhile

        _hits.Clear();
        foreach (var h in hits) _hits.Add(h);
        var total = hits.Count >= 500 ? "500+ runs" : hits.Count == 1 ? "1 run" : $"{hits.Count} runs";
        StatusText.Text = hits.Count == 0
            ? $"No run contains “{query}”."
            : $"{total} match “{query}” · newest first · double-click to open in its tab" + (_indexing ? " · still indexing older runs…" : "");
        if (hits.Count > 0) Results.SelectedIndex = 0;
        UpdateButtons();
    }

    static SearchHit MakeHit(RunRecord r, string query, Dictionary<long, (string Excerpt, int Offset, int Count)> snippets)
    {
        if (!snippets.TryGetValue(r.Id, out var s) || s.Offset < 0 || s.Offset + query.Length > s.Excerpt.Length)
            return new SearchHit(r, "", "", "", 0);

        // Keep the snippet on the matching line.
        var before = s.Excerpt[..s.Offset];
        var nl = before.LastIndexOf('\n');
        if (nl >= 0) before = before[(nl + 1)..];
        var match = s.Excerpt.Substring(s.Offset, query.Length);
        var after = s.Excerpt[(s.Offset + query.Length)..];
        nl = after.IndexOf('\n');
        if (nl >= 0) after = after[..nl];
        return new SearchHit(r, before.TrimStart().Replace('\t', ' '), match, after.Replace('\t', ' '), s.Count);
    }

    SearchHit? Selected => Results.SelectedItem as SearchHit;

    void UpdateButtons()
    {
        OpenButton.IsEnabled = Selected != null;
        ReportButton.IsEnabled = _hits.Count > 0;
    }

    async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        if (Selected is not { } hit || hit.Run.Record.Id == _previewId) return;
        var r = hit.Run.Record;
        _previewId = r.Id;
        PreviewInfo.Text = "$ " + r.CommandLine;
        PreviewInfo.ToolTip = $"{r.CommandLine}\nDirectory: {r.Cwd}\n{hit.Run.Meta}";
        Preview.Reset();
        var text = await Task.Run(() => CastReader.ReadOutput(_store.LogPath(r)));
        if (_previewId != r.Id) return;
        Preview.Write(text.Length > 0 ? text : "\x1b[90m(no output, or the log file was deleted)\x1b[0m\r\n");
        if (Query.Length > 0) Preview.Find(Query);
    }

    void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected != null) Open_Click(sender, e);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } hit) return;
        if (!_openRun(hit.Run.Record, Query))
            MessageBox.Show(this,
                hit.Run.Record.ToolId.StartsWith("shell:", StringComparison.Ordinal)
                    ? "This is a shell session — its output is shown in the preview on the right."
                    : "The tool of this run no longer exists (or the run is older than the 1000 runs a tab lists). Its output is shown in the preview.",
                "Search history", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void Report_Click(object sender, RoutedEventArgs e)
    {
        var runs = _hits.Select(h => h.Run.Record).ToList();
        new ReportWindow(_store, runs, $"Search results for “{Query}”") { Owner = this }.ShowDialog();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
