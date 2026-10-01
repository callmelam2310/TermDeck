using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TermDeck.Core;

namespace TermDeck.Views;

/// <summary>A tool that can be added: found by a scan, or read from a tools file.</summary>
public sealed class ImportRow : Observable
{
    bool _isChecked;

    public required string Name { get; init; }
    public required ToolKind ToolKind { get; init; }
    public string Kind => ToolKind == ToolKind.Wsl ? "WSL" : "Windows";
    public required string Path { get; init; }
    /// <summary>Folder (scan) or collection name (file).</summary>
    public required string Group { get; init; }
    public required string Status { get; init; }
    public string StatusDetail { get; init; } = "";
    public bool IsNew { get; init; }
    public ToolDef? Existing { get; init; }
    public PackTool? PackTool { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }
}

/// <summary>
/// Adds tools in bulk. Scan mode finds executables in WSL folders (~/go/bin, pipx, cargo…) or a Windows folder;
/// file mode imports a termdeck-tools JSON (exported from another machine or shared by a teammate).
/// </summary>
public partial class ImportToolsWindow : Window
{
    const string Ungrouped = "(Ungrouped)";
    const string DefaultDistro = "(default)";

    readonly ObservableCollection<ToolDef> _tools;
    readonly ObservableCollection<ToolCollection> _collections;
    readonly ObservableCollection<ImportRow> _rows = new();
    readonly ToolPack? _pack;
    /// <summary>Distro used by the last WSL scan ("" = default), stored on the imported tools.</summary>
    string _scannedDistro = "";

    public int ImportedCount { get; private set; }

    /// <summary>Scan mode.</summary>
    public ImportToolsWindow(ObservableCollection<ToolDef> tools, ObservableCollection<ToolCollection> collections, string collection)
    {
        InitializeComponent();
        _tools = tools;
        _collections = collections;
        Rows.ItemsSource = _rows;

        foreach (var (path, on) in ToolScanner.WslDefaults)
            FolderChecks.Children.Add(new CheckBox { Content = path, Tag = path, IsChecked = on, Margin = new Thickness(0, 2, 16, 2), FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont") });
        var exeTools = System.IO.Path.Combine(AppPaths.ExeDir, "tools");
        if (System.IO.Directory.Exists(exeTools)) WinFolder.Text = exeTools;

        CollectionBox.Items.Add(Ungrouped);
        foreach (var c in collections) CollectionBox.Items.Add(c.Name);
        CollectionBox.Text = collection.Length > 0 ? collection : Ungrouped;

        StatusText.Text = "Choose where to look, then click Scan.";
        Loaded += async (_, _) =>
        {
            var distros = await Task.Run(WslInfo.ListDistros);
            DistroBox.Items.Add(DefaultDistro);
            foreach (var d in distros) DistroBox.Items.Add(d);
            DistroBox.SelectedIndex = 0;
            if (distros.Count == 0)
            {
                WslRadio.IsEnabled = false;
                WinRadio.IsChecked = true;
            }
        };
        UpdateSourceUi();
    }

    /// <summary>File mode.</summary>
    public ImportToolsWindow(ObservableCollection<ToolDef> tools, ObservableCollection<ToolCollection> collections, ToolPack pack, string fileName)
    {
        InitializeComponent();
        _tools = tools;
        _collections = collections;
        _pack = pack;
        Rows.ItemsSource = _rows;

        Title = "Import tools";
        ScanPanel.Visibility = Visibility.Collapsed;
        CollectionPanel.Visibility = Visibility.Collapsed;
        ConflictPanel.Visibility = Visibility.Visible;
        FileInfo.Visibility = Visibility.Visible;
        FileInfo.Text = $"{fileName} — exported {pack.ExportedAt:yyyy-MM-dd HH:mm}" + (pack.App.Length > 0 ? $" by {pack.App}" : "") +
                        ". Collections are merged by name; tools keep their id so run history still matches.";
        GroupColumn.Header = "Collection";
        ImportLabel.Text = " Import";

        foreach (var c in pack.Collections)
        {
            foreach (var t in c.Tools)
            {
                var byId = tools.FirstOrDefault(x => x.Id == t.Id);
                var byName = byId == null ? tools.FirstOrDefault(x => x.Kind == t.Kind && x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)) : null;
                _rows.Add(new ImportRow
                {
                    Name = t.Name,
                    ToolKind = t.Kind,
                    Path = t.Path,
                    Group = c.Name.Length > 0 ? c.Name : Ungrouped,
                    Status = byId != null ? "Already exists" : byName != null ? "New (same name exists)" : "New",
                    StatusDetail = byId != null ? $"Same tool (id) as “{byId.Name}” — {byId.Path}" : byName != null ? $"Another tool is named “{byName.Name}”: {byName.Path}" : "",
                    IsNew = byId == null,
                    Existing = byId,
                    PackTool = t,
                    IsChecked = byId == null,
                });
            }
        }
        UpdateStatus();
    }

    // ───────────────────────── Scan ─────────────────────────

    void Source_Changed(object sender, RoutedEventArgs e) => UpdateSourceUi();

    void UpdateSourceUi()
    {
        if (WslFolders == null) return; // during InitializeComponent
        var wsl = WslRadio.IsChecked == true;
        WslFolders.IsEnabled = DistroBox.IsEnabled = wsl;
        WinFolder.IsEnabled = Recursive.IsEnabled = !wsl;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with Windows tools", InitialDirectory = WinFolder.Text };
        if (dlg.ShowDialog(this) == true)
        {
            WinFolder.Text = dlg.FolderName;
            WinRadio.IsChecked = true;
        }
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var wsl = WslRadio.IsChecked == true;
        List<FoundTool>? found;
        ScanButton.IsEnabled = false;
        StatusText.Text = "Scanning…";
        try
        {
            if (wsl)
            {
                var distro = DistroBox.SelectedItem as string is { } d && d != DefaultDistro ? d : "";
                var folders = FolderChecks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag)
                    .Concat(OtherFolders.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToList();
                if (folders.Count == 0)
                {
                    StatusText.Text = "Select at least one folder.";
                    return;
                }
                found = await Task.Run(() => ToolScanner.ScanWsl(distro, folders));
                if (found == null)
                {
                    StatusText.Text = "Could not run WSL. Is the distro installed and working?";
                    return;
                }
                _scannedDistro = distro;
            }
            else
            {
                var folder = WinFolder.Text.Trim().Trim('"');
                if (!System.IO.Directory.Exists(folder))
                {
                    StatusText.Text = "Choose an existing folder.";
                    return;
                }
                var recursive = Recursive.IsChecked == true;
                found = await Task.Run(() => ToolScanner.ScanWindows(folder, recursive));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed: " + ex.Message;
            return;
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }

        var kind = wsl ? ToolKind.Wsl : ToolKind.Windows;
        _rows.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in found)
        {
            var existing = FindExisting(kind, f);
            var duplicate = existing == null && !seen.Add(f.Name);
            _rows.Add(new ImportRow
            {
                Name = f.Name,
                ToolKind = kind,
                Path = f.Path,
                Group = f.Folder,
                Status = existing != null ? "Already added" : duplicate ? "Duplicate name" : "New",
                StatusDetail = existing != null ? $"Tool “{existing.Name}”: {existing.Path}" : duplicate ? "A tool with this name was found in an earlier folder" : "",
                IsNew = existing == null && !duplicate,
                Existing = existing,
                IsChecked = existing == null && !duplicate,
            });
        }
        UpdateStatus();
        if (_rows.Count == 0) StatusText.Text = "No executables found in the selected folders.";
    }

    /// <summary>A tool already points at this executable (same path, or same name/bare command of the same kind).</summary>
    ToolDef? FindExisting(ToolKind kind, FoundTool f)
    {
        var cmp = kind == ToolKind.Wsl ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return _tools.FirstOrDefault(t => t.Kind == kind &&
            (string.Equals(t.Path.Trim(), f.Path, cmp) ||
             (kind == ToolKind.Windows && string.Equals(AppPaths.ResolveToolPath(t.Path.Trim()), AppPaths.ResolveToolPath(f.Path), cmp)) ||
             string.Equals(t.Path.Trim(), f.Name, cmp) ||
             string.Equals(t.Name.Trim(), f.Name, StringComparison.OrdinalIgnoreCase)));
    }

    // ───────────────────────── List ─────────────────────────

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = FilterBox.Text.Trim();
        CollectionViewSource.GetDefaultView(_rows).Filter = q.Length == 0
            ? null
            : o => o is ImportRow r && (r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(q, StringComparison.OrdinalIgnoreCase));
        UpdateStatus();
    }

    IEnumerable<ImportRow> Visible => CollectionViewSource.GetDefaultView(_rows).Cast<ImportRow>();

    void All_Click(object sender, RoutedEventArgs e) { foreach (var r in Visible) r.IsChecked = true; UpdateStatus(); }
    void None_Click(object sender, RoutedEventArgs e) { foreach (var r in Visible) r.IsChecked = false; UpdateStatus(); }
    void NewOnly_Click(object sender, RoutedEventArgs e) { foreach (var r in Visible) r.IsChecked = r.IsNew; UpdateStatus(); }
    void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateStatus();

    /// <summary>Space toggles the selected rows.</summary>
    void Rows_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Rows.SelectedItems.Count == 0) return;
        var rows = Rows.SelectedItems.Cast<ImportRow>().ToList();
        var on = !rows.All(r => r.IsChecked);
        foreach (var r in rows) r.IsChecked = on;
        UpdateStatus();
        e.Handled = true;
    }

    void UpdateStatus()
    {
        var total = _rows.Count;
        var news = _rows.Count(r => r.IsNew);
        var chosen = _rows.Count(r => r.IsChecked);
        StatusText.Text = total == 0 ? StatusText.Text : $"{total} found · {news} new · {chosen} selected";
        ImportButton.IsEnabled = chosen > 0;
        ImportLabel.Text = _pack != null ? $" Import {chosen}" : $" Add {chosen} tool(s)";
    }

    // ───────────────────────── Import ─────────────────────────

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.IsChecked).ToList();
        if (chosen.Count == 0) return;
        ImportedCount = _pack != null ? ImportPack(chosen) : AddScanned(chosen);
        DialogResult = true;
    }

    int AddScanned(List<ImportRow> rows)
    {
        var collectionId = ResolveCollection(CollectionBox.Text.Trim(), null);
        foreach (var r in rows)
        {
            _tools.Add(new ToolDef
            {
                Name = r.Name,
                Kind = r.ToolKind,
                Path = r.Path,
                Distro = r.ToolKind == ToolKind.Wsl ? _scannedDistro : "",
                CollectionId = collectionId,
            });
        }
        return rows.Count;
    }

    int ImportPack(List<ImportRow> rows)
    {
        var mode = ConflictBox.SelectedIndex; // 0 skip, 1 replace, 2 copy
        var count = 0;
        foreach (var r in rows)
        {
            var source = _pack!.Collections.First(c => c.Tools.Contains(r.PackTool!));
            var collectionId = ResolveCollection(source.Name, source.Id);
            var tool = r.PackTool!.ToTool(collectionId);
            if (r.Existing != null)
            {
                if (mode == 0) continue;
                if (mode == 1)
                {
                    var i = _tools.IndexOf(r.Existing);
                    if (i >= 0) _tools[i] = tool; else _tools.Add(tool);
                    count++;
                    continue;
                }
                tool.Id = Guid.NewGuid().ToString("N");
            }
            _tools.Add(tool);
            count++;
        }
        return count;
    }

    /// <summary>Collection id for a name: an existing collection with that name, a new one, or "" for ungrouped.</summary>
    string ResolveCollection(string name, string? preferredId)
    {
        if (name.Length == 0 || name == Ungrouped) return "";
        var existing = _collections.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing.Id;
        var created = new ToolCollection { Name = name };
        if (!string.IsNullOrEmpty(preferredId) && _collections.All(c => c.Id != preferredId)) created.Id = preferredId;
        _collections.Add(created);
        return created.Id;
    }
}
