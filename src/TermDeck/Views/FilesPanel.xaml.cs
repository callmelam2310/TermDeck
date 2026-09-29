using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;
using TermDeck.Core;

namespace TermDeck.Views;

public sealed class FileEntry
{
    static readonly Brush FolderBrush = Frozen(0xC8, 0x93, 0x0C);
    static readonly Brush FileBrush = Frozen(0x70, 0x70, 0x70);

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDir { get; init; }
    public bool IsParent { get; init; }
    public bool IsHidden { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    public string Glyph => IsParent ? "" : IsDir ? "" : "";
    public Brush GlyphBrush => IsDir ? FolderBrush : FileBrush;
    public double TextOpacity => IsHidden ? 0.55 : 1.0;
    public string SizeText => IsDir ? "" : FormatSize(Size);
    public string ModifiedText => IsParent ? "" : Modified.ToString(Modified.Year == DateTime.Now.Year ? "dd/MM HH:mm" : "dd/MM/yyyy");

    static string FormatSize(long b) =>
        b < 1024 ? $"{b} B" : b < 1 << 20 ? $"{b / 1024.0:0.#} KB" : b < 1 << 30 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Sidebar file browser (like MobaXterm's SFTP browser). Always works with Windows paths internally;
/// for WSL tools, paths are displayed/copied/inserted in Linux form.
/// </summary>
public partial class FilesPanel : UserControl
{
    int _loadVersion;
    Point _dragStart;
    bool _suppressFollowEvent;

    public string CurrentDir { get; private set; } = "";
    public string ProjectRoot { get; set; } = "";
    public ToolDef? Tool { get; private set; }

    /// <summary>The user navigated to another folder.</summary>
    public event Action<string>? DirectoryChanged;
    /// <summary>Request to insert (Windows) paths into the active tab's argument box.</summary>
    public event Action<IReadOnlyList<string>>? InsertRequested;
    public event Action<bool>? FollowChanged;

    public FilesPanel()
    {
        InitializeComponent();
    }

    public bool Follow
    {
        get => FollowBox.IsChecked == true;
        set
        {
            _suppressFollowEvent = true;
            FollowBox.IsChecked = value;
            _suppressFollowEvent = false;
        }
    }

    public void SetTool(ToolDef? tool)
    {
        Tool = tool;
        if (CurrentDir.Length > 0) PathBox.Text = PathMapper.Display(CurrentDir, Tool);
    }

    public void Navigate(string dir, bool raise)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        try { dir = Path.GetFullPath(dir); } catch { }
        CurrentDir = dir;
        PathBox.Text = PathMapper.Display(dir, Tool);
        _ = LoadAsync(dir);
        if (raise) DirectoryChanged?.Invoke(dir);
    }

    public void Refresh() => _ = LoadAsync(CurrentDir);

    async Task LoadAsync(string dir)
    {
        var version = ++_loadVersion;
        CountText.Text = "loading...";
        List<FileEntry>? entries = null;
        string? error = null;
        await Task.Run(() =>
        {
            try { entries = Enumerate(dir); }
            catch (Exception ex) { error = ex.Message; }
        });
        if (version != _loadVersion) return;

        var list = new List<FileEntry>();
        var parent = Directory.GetParent(dir);
        if (parent != null)
            list.Add(new FileEntry { Name = "..", FullPath = parent.FullName, IsDir = true, IsParent = true });
        if (entries != null) list.AddRange(entries);
        FileList.ItemsSource = list;
        CountText.Text = error ?? $"{entries?.Count ?? 0} items";
        CountText.Foreground = error != null ? Brushes.Firebrick : Brushes.Gray;
    }

    static List<FileEntry> Enumerate(string dir)
    {
        var info = new DirectoryInfo(dir);
        var result = new List<FileEntry>();
        foreach (var fsi in info.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            var isDir = fsi.Attributes.HasFlag(FileAttributes.Directory);
            result.Add(new FileEntry
            {
                Name = fsi.Name,
                FullPath = fsi.FullName,
                IsDir = isDir,
                IsHidden = fsi.Attributes.HasFlag(FileAttributes.Hidden) || fsi.Name.StartsWith('.'),
                Size = isDir ? 0 : ((FileInfo)fsi).Length,
                Modified = fsi.LastWriteTime,
            });
        }
        return result
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    List<FileEntry> SelectedEntries => FileList.SelectedItems.Cast<FileEntry>().Where(e => !e.IsParent).ToList();

    string ForDisplay(string winPath) => PathMapper.Display(winPath, Tool);

    // ───────────────────────── Actions ─────────────────────────

    void OpenEntry(FileEntry e)
    {
        if (e.IsDir) { Navigate(e.FullPath, true); return; }
        try { Process.Start(new ProcessStartInfo(e.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not open file"); }
    }

    void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(CurrentDir);
        if (parent != null) Navigate(parent.FullName, true);
    }

    void Home_Click(object sender, RoutedEventArgs e) => Navigate(ProjectRoot, true);

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var target = PathMapper.FromUserInput(PathBox.Text, Tool);
        if (Directory.Exists(target)) Navigate(target, true);
        else
        {
            CountText.Text = "Folder not found";
            CountText.Foreground = Brushes.Firebrick;
        }
    }

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileEntry entry) OpenEntry(entry);
    }

    void List_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when FileList.SelectedItem is FileEntry entry: OpenEntry(entry); break;
            case Key.Back: Up_Click(sender, e); break;
            case Key.Delete: Delete_Click(sender, e); break;
            case Key.F2: Rename_Click(sender, e); break;
            case Key.F5: Refresh(); break;
        }
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileEntry entry) OpenEntry(entry);
    }

    void Insert_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedEntries;
        InsertRequested?.Invoke(sel.Count > 0 ? sel.Select(x => x.FullPath).ToList() : new List<string> { CurrentDir });
    }

    void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedEntries;
        var paths = sel.Count > 0 ? sel.Select(x => ForDisplay(x.FullPath)) : new[] { ForDisplay(CurrentDir) };
        Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    void CopyWinPath_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedEntries;
        var paths = sel.Count > 0 ? sel.Select(x => x.FullPath) : new[] { CurrentDir };
        Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    void OpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileEntry { IsParent: false } entry)
            Process.Start("explorer.exe", $"/select,\"{entry.FullPath}\"");
        else
            Process.Start("explorer.exe", $"\"{CurrentDir}\"");
    }

    void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(Window.GetWindow(this), "New folder", "Folder name:", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        TryIo(() => Directory.CreateDirectory(Path.Combine(CurrentDir, name.Trim())));
    }

    void NewFile_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(Window.GetWindow(this), "New file", "File name:", "new.txt");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = Path.Combine(CurrentDir, name.Trim());
        if (File.Exists(path)) { MessageBox.Show("A file with that name already exists."); return; }
        TryIo(() => File.WriteAllBytes(path, Array.Empty<byte>()));
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileEntry { IsParent: false } entry) return;
        var name = PromptWindow.Ask(Window.GetWindow(this), "Rename", "New name:", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        var target = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, name.Trim());
        TryIo(() =>
        {
            if (entry.IsDir) Directory.Move(entry.FullPath, target);
            else File.Move(entry.FullPath, target);
        });
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedEntries;
        if (sel.Count == 0) return;
        var network = CurrentDir.StartsWith(@"\\", StringComparison.Ordinal);
        var what = sel.Count == 1 ? sel[0].Name : $"{sel.Count} items";
        var msg = network
            ? $"PERMANENTLY delete {what}? (network/WSL paths have no Recycle Bin)"
            : $"Move {what} to the Recycle Bin?";
        if (MessageBox.Show(msg, "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        TryIo(() =>
        {
            foreach (var entry in sel)
            {
                if (network)
                {
                    if (entry.IsDir) Directory.Delete(entry.FullPath, true);
                    else File.Delete(entry.FullPath);
                }
                else if (entry.IsDir)
                {
                    FileSystem.DeleteDirectory(entry.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                else
                {
                    FileSystem.DeleteFile(entry.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
            }
        });
    }

    void TryIo(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Refresh();
    }

    /// <summary>The Name column stretches with the panel width.</summary>
    void FileList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = FileList.ActualWidth - SizeColumn.Width - ModifiedColumn.Width - SystemParameters.VerticalScrollBarWidth - 8;
        NameColumn.Width = Math.Max(80, w);
    }

    void FollowBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppressFollowEvent) FollowChanged?.Invoke(Follow);
    }

    // ───────────────────────── Drag and drop ─────────────────────────

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileEntry) return;

        var sel = SelectedEntries;
        if (sel.Count == 0) return;
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, sel.Select(x => x.FullPath).ToArray());
        data.SetText(string.Join(" ", sel.Select(x => ForDisplay(x.FullPath))));
        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy);
    }

    void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Files dropped from Windows Explorer are copied into the current folder (like upload in MobaXterm).</summary>
    async void List_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] sources) return;
        var dest = CurrentDir;
        sources = sources.Where(s => !string.Equals(Path.GetDirectoryName(s), dest, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sources.Length == 0) return;
        CountText.Text = $"copying {sources.Length} items...";
        try
        {
            await Task.Run(() =>
            {
                foreach (var src in sources)
                {
                    var target = Path.Combine(dest, Path.GetFileName(src));
                    if (Directory.Exists(src)) FileSystem.CopyDirectory(src, target, UIOption.OnlyErrorDialogs);
                    else FileSystem.CopyFile(src, target, UIOption.OnlyErrorDialogs);
                }
            });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Copy failed"); }
        Refresh();
    }
}
