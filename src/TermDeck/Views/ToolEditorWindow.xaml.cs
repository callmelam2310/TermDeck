using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TermDeck.Core;

namespace TermDeck.Views;

public partial class ToolEditorWindow : Window
{
    static readonly ToolCollection Ungrouped = new() { Id = "", Name = "(Ungrouped)" };

    readonly ToolDef _tool;
    readonly string _projectDir;
    readonly IList<ToolCollection> _collections;
    bool _loading = true;

    public ToolDef? Result { get; private set; }

    /// <param name="collections">Live collection list; "New..." adds to it directly.</param>
    /// <param name="defaultCollectionId">Pre-selected collection for a new tool.</param>
    public ToolEditorWindow(ToolDef? existing, string projectDir, IList<ToolCollection> collections, string defaultCollectionId = "")
    {
        InitializeComponent();
        _tool = existing?.Clone() ?? new ToolDef { CollectionId = defaultCollectionId };
        _projectDir = projectDir;
        _collections = collections;
        Title = existing == null ? "New tool" : $"Tool settings — {existing.Name}";

        NameBox.Text = _tool.Name;
        ArgsBox.Text = _tool.DefaultArgs;
        ShellBox.Text = string.IsNullOrWhiteSpace(_tool.WslShell) ? "bash" : _tool.WslShell;
        DistroBox.Text = _tool.Distro;
        if (_tool.Kind == ToolKind.Wsl) { WslCmdBox.Text = _tool.Path; KindWsl.IsChecked = true; }
        else { WinPathBox.Text = _tool.Path; KindWindows.IsChecked = true; }
        FillCollections(_tool.CollectionId);

        DistroBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(Any_Changed));
        ShellBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(Any_Changed));

        Loaded += async (_, _) =>
        {
            NameBox.Focus();
            var current = DistroBox.Text;
            var distros = await Task.Run(WslInfo.ListDistros);
            DistroBox.Items.Add(new ComboBoxItem { Content = "", ToolTip = "Default distro" });
            foreach (var d in distros) DistroBox.Items.Add(new ComboBoxItem { Content = d });
            DistroBox.Text = current;
        };

        _loading = false;
        UpdateKindUi();
        UpdatePreview();
    }

    void FillCollections(string selectedId)
    {
        var items = new List<ToolCollection> { Ungrouped };
        items.AddRange(_collections);
        CollectionBox.ItemsSource = items;
        CollectionBox.SelectedItem = items.FirstOrDefault(c => c.Id == selectedId) ?? Ungrouped;
    }

    void NewCollection_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(this, "New collection", "Collection name:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var collection = new ToolCollection { Name = name.Trim() };
        _collections.Add(collection);
        FillCollections(collection.Id);
    }

    bool IsWsl => KindWsl.IsChecked == true;

    void Kind_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateKindUi();
        UpdatePreview();
    }

    void UpdateKindUi()
    {
        var win = IsWsl ? Visibility.Collapsed : Visibility.Visible;
        var wsl = IsWsl ? Visibility.Visible : Visibility.Collapsed;
        WinPathLabel.Visibility = WinPathPanel.Visibility = WinBrowse.Visibility = win;
        DistroLabel.Visibility = DistroBox.Visibility = WslCmdLabel.Visibility = WslCmdPanel.Visibility = wsl;
    }

    void Any_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading) UpdatePreview();
    }

    ToolDef Collect()
    {
        var t = _tool.Clone();
        t.Name = NameBox.Text.Trim();
        t.Kind = IsWsl ? ToolKind.Wsl : ToolKind.Windows;
        t.Path = (IsWsl ? WslCmdBox.Text : WinPathBox.Text).Trim().Trim('"');
        t.Distro = IsWsl ? DistroBox.Text.Trim() : "";
        t.WslShell = string.IsNullOrWhiteSpace(ShellBox.Text) ? "bash" : ShellBox.Text.Trim();
        t.DefaultArgs = ArgsBox.Text.Trim();
        t.CollectionId = (CollectionBox.SelectedItem as ToolCollection)?.Id ?? "";
        return t;
    }

    void UpdatePreview()
    {
        if (PreviewText == null) return;
        var t = Collect();
        if (string.IsNullOrEmpty(t.Path)) { PreviewText.Text = "(no executable yet)"; return; }
        // Every tool starts in the project folder.
        PreviewText.Text = CommandBuilder.Build(t, t.DefaultArgs, _projectDir).CommandLine;
    }

    void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Programs (*.exe;*.bat;*.cmd;*.com)|*.exe;*.bat;*.cmd;*.com|All files (*.*)|*.*",
            Title = "Choose executable",
        };
        if (dlg.ShowDialog(this) != true) return;
        WinPathBox.Text = MakePortableRelative(dlg.FileName);
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    /// <summary>Portable build: executables inside the TermDeck folder are stored relative, so the kit still works on another machine.</summary>
    static string MakePortableRelative(string path)
    {
        if (!AppPaths.IsPortable) return path;
        var root = AppPaths.ExeDir + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? @".\" + path[root.Length..] : path;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var t = Collect();
        if (string.IsNullOrEmpty(t.Path))
        {
            MessageBox.Show(this, IsWsl ? "Enter a Linux command or binary." : "Choose an executable.", "Missing information");
            return;
        }
        if (string.IsNullOrEmpty(t.Name))
            t.Name = IsWsl ? t.Path.Split('/', ' ')[^1] : Path.GetFileNameWithoutExtension(t.Path);
        if (t.Kind == ToolKind.Windows)
        {
            var resolved = AppPaths.ResolveToolPath(t.Path);
            var isBareName = !t.Path.Contains('\\') && !t.Path.Contains('/');
            if (!isBareName && !File.Exists(resolved) &&
                MessageBox.Show(this, $"File not found:\n{resolved}\n\nSave anyway?", "Warning",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        Result = t;
        DialogResult = true;
    }
}
