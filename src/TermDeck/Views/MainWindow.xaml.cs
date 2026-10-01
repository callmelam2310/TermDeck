using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TermDeck.Core;

namespace TermDeck.Views;

public partial class MainWindow : Window
{
    const string ToolDragFormat = "TermDeck.ToolId";

    readonly AppConfig _config;
    readonly ObservableCollection<ToolDef> _tools;
    readonly ObservableCollection<ToolCollection> _collections;
    readonly ObservableCollection<CollectionNode> _treeNodes = new();
    readonly ObservableCollection<CollectionNode> _homeNodes = new();
    readonly ObservableCollection<DocTab> _tabs = new();
    readonly ObservableCollection<string> _recent;
    readonly DocTab _homeTab = new("Home", null);

    HistoryStore? _store;
    string _projectDir = "";
    string _filter = "";
    RadioButton? _activeSide;
    bool _sidebarVisible = true;
    object? _contextTarget;
    Point _dragStart;
    ToolDef? _dragTool;
    List<ShellDef>? _shells;
    SearchWindow? _searchWindow;

    public MainWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _tools = new ObservableCollection<ToolDef>(config.Tools);
        _collections = new ObservableCollection<ToolCollection>(config.Collections);
        _recent = new ObservableCollection<string>(config.RecentProjects);

        ToolsTree.ItemsSource = _treeNodes;
        HomeGroups.ItemsSource = _homeNodes;
        HomeRecent.ItemsSource = _recent;
        RebuildTree();

        _tabs.Add(_homeTab);
        TabStrip.ItemsSource = _tabs;
        TabStrip.SelectedItem = _homeTab;

        SidebarColumn.Width = new GridLength(Math.Max(160, config.SidebarWidth));
        ToolsSideTab.IsChecked = true;
        _activeSide = ToolsSideTab;

        Files.Follow = config.FollowTerminalFolder;
        Files.DirectoryChanged += OnFilesDirectoryChanged;
        Files.InsertRequested += OnFilesInsertRequested;
        Files.FollowChanged += follow =>
        {
            _config.FollowTerminalFolder = follow;
            if (follow && ActiveToolTab is { } t) Files.Navigate(t.Cwd, false);
        };

        StatusMode.Text = AppPaths.IsPortable ? "Portable" : "Installed";
        UpdateAutorunStatus();
        StatusMode.ToolTip = "Data: " + AppPaths.DataDir;

        AddShortcut(Key.N, ModifierKeys.Control, () => NewTool());
        AddShortcut(Key.N, ModifierKeys.Control | ModifierKeys.Shift, () => NewCollection());
        AddShortcut(Key.O, ModifierKeys.Control, () => OpenProject_Click(this, new RoutedEventArgs()));
        AddShortcut(Key.B, ModifierKeys.Control, () => ToggleSidebar_Click(this, new RoutedEventArgs()));
        AddShortcut(Key.W, ModifierKeys.Control, () => CloseCurrentTab_Click(this, new RoutedEventArgs()));
        AddShortcut(Key.F, ModifierKeys.Control, () => ActiveView?.OpenFind());
        AddShortcut(Key.F, ModifierKeys.Control | ModifierKeys.Shift, () => OpenSearch());
        AddShortcut(Key.T, ModifierKeys.Control, () => OpenDefaultShell());
        AddShortcut(Key.Tab, ModifierKeys.Control, () => CycleTab(1));
        AddShortcut(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, () => CycleTab(-1));

        // Let the window paint first; on first run OpenInitialProject shows a folder picker.
        Loaded += (_, _) => Dispatcher.BeginInvoke(OpenInitialProject, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        LoadShells();
    }

    void AddShortcut(Key key, ModifierKeys mods, Action action)
    {
        var cmd = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(cmd, (_, _) => action()));
        InputBindings.Add(new KeyBinding(cmd, key, mods));
    }

    IDocView? ActiveView => (TabStrip.SelectedItem as DocTab)?.View;
    ToolTab? ActiveToolTab => ActiveView as ToolTab;
    IEnumerable<IDocView> Views => _tabs.Where(t => !t.IsHome).Select(t => t.View!);
    ToolDef? SelectedTool => ToolsTree.SelectedItem as ToolDef;
    CollectionNode? SelectedNode => ToolsTree.SelectedItem as CollectionNode;

    // ───────────────────────── Project ─────────────────────────

    void OpenInitialProject()
    {
        // Reopen the last project, or fall back to the default folder — never pop a folder picker on startup.
        // Use File → Open project… (Ctrl+O) to switch.
        if (!string.IsNullOrEmpty(_config.LastProject) && Directory.Exists(_config.LastProject) && OpenProject(_config.LastProject))
            return;

        Directory.CreateDirectory(AppPaths.DefaultProjectDir);
        OpenProject(AppPaths.DefaultProjectDir);
    }

    bool OpenProject(string dir)
    {
        if (string.Equals(dir, _projectDir, StringComparison.OrdinalIgnoreCase)) return true;
        if (!ConfirmStopRunning("Switching projects stops all running tools. Continue?")) return false;

        HistoryStore store;
        try { store = new HistoryStore(dir); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open project:\n{dir}\n\n{ex.Message}", "TermDeck", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        // Tabs and the search window are bound to the project folder they were opened in, so close them all.
        _searchWindow?.Close();
        foreach (var tab in _tabs.Where(t => !t.IsHome).ToList()) RemoveTab(tab);
        ResetAutorun();
        _store = store;
        _projectDir = dir;

        _config.LastProject = dir;
        var existing = _recent.FirstOrDefault(r => string.Equals(r, dir, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _recent.Remove(existing);
        _recent.Insert(0, dir);
        while (_recent.Count > 10) _recent.RemoveAt(_recent.Count - 1);
        RebuildRecentMenu();
        SaveConfig();

        Title = $"TermDeck — {Path.GetFileName(dir.TrimEnd('\\'))}";
        StatusProject.Text = dir;
        HomeProject.Text = dir;
        Files.ProjectRoot = dir;
        Files.SetTool(null);
        Files.Navigate(dir, false);
        TabStrip.SelectedItem = _homeTab;
        return true;
    }

    void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var p in _recent)
        {
            var item = new MenuItem { Header = p.Replace("_", "__") };
            item.Click += (_, _) => OpenProjectIfExists(p);
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = _recent.Count > 0;
    }

    void OpenProjectIfExists(string dir)
    {
        if (Directory.Exists(dir)) OpenProject(dir);
        else if (MessageBox.Show(this, $"This folder no longer exists:\n{dir}\n\nRemove it from the list?", "TermDeck",
                     MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            _recent.Remove(dir);
            RebuildRecentMenu();
            SaveConfig();
        }
    }

    void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a project folder", InitialDirectory = _projectDir };
        if (dlg.ShowDialog(this) == true) OpenProject(dlg.FolderName);
    }

    void RecentProject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.Tag is string dir) OpenProjectIfExists(dir);
    }

    // ───────────────────────── Tabs ─────────────────────────

    /// <summary>Opens the tool's tab. <paramref name="forceNew"/> always opens an extra tab, so one tool can run in
    /// several terminals at once (titled "name (2)", "name (3)"…); otherwise the existing tab is reused.</summary>
    ToolTab? OpenToolTab(ToolDef tool, bool forceNew = false)
    {
        if (_store == null) return null;
        if (!forceNew)
        {
            var existing = TabOf(tool.Id);
            if (existing != null)
            {
                TabStrip.SelectedItem = existing;
                return (ToolTab)existing.View!;
            }
        }
        var tab = new ToolTab(tool, _store, _config);
        tab.RunFinished += OnToolRunFinished;
        tab.AutorunRequested += OnAutorunRequested;
        var n = _tabs.Count(t => t.View is ToolTab v && v.Tool.Id == tool.Id);
        AddTab(n == 0 ? tool.Name : $"{tool.Name} ({n + 1})", tab);
        return tab;
    }

    DocTab? TabOf(string toolId) => _tabs.FirstOrDefault(t => t.View is ToolTab v && v.Tool.Id == toolId);

    DocTab AddTab(string title, IDocView view)
    {
        var doc = new DocTab(title, view);
        view.Element.Visibility = Visibility.Collapsed;
        view.CwdChanged += OnTabCwdChanged;
        view.RunningChanged += _ => { doc.IsRunning = view.HasRunning; UpdateRunningStatus(); };
        ContentHost.Children.Add(view.Element);
        _tabs.Add(doc);
        TabStrip.SelectedItem = doc;
        TabStrip.ScrollIntoView(doc);
        Dispatcher.BeginInvoke(UpdateTabScrollButtons, System.Windows.Threading.DispatcherPriority.Background);
        return doc;
    }

    // ───────────────────────── Shell tabs ─────────────────────────

    async void LoadShells()
    {
        _shells = await Task.Run(ShellCatalog.List);
        RebuildShellMenu();
    }

    IEnumerable<MenuItem> ShellItems()
    {
        if (_shells == null)
        {
            yield return new MenuItem { Header = "Looking for shells…", IsEnabled = false };
            yield break;
        }
        foreach (var sh in _shells)
        {
            var item = Item(sh.Title, () => OpenShell(sh), glyph: "\uE756", color: sh.Kind == ToolKind.Wsl ? "#E0701E" : "#1E73D8",
                bold: sh.Key == _config.LastShell);
            if (sh.Key == _config.LastShell) item.InputGestureText = "Ctrl+T";
            yield return item;
        }
    }

    void RebuildShellMenu()
    {
        ShellMenu.Items.Clear();
        foreach (var item in ShellItems()) ShellMenu.Items.Add(item);
    }

    /// <summary>Ribbon button: drops down the list of shells.</summary>
    void Shell_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var item in ShellItems()) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    void OpenDefaultShell()
    {
        if (_shells is not { Count: > 0 }) return;
        OpenShell(_shells.FirstOrDefault(s => s.Key == _config.LastShell) ?? _shells[0]);
    }

    void OpenShell(ShellDef shell)
    {
        if (_store == null) return;
        if (_config.LastShell != shell.Key)
        {
            _config.LastShell = shell.Key;
            SaveConfig();
            RebuildShellMenu();
        }
        // Start where the Files panel is when it follows the terminal, otherwise in the project folder.
        var dir = Files.Follow && ActiveView is { } v ? v.Cwd : _projectDir;
        var baseTitle = shell.Kind == ToolKind.Wsl ? shell.Distro : shell.Key;
        var n = _tabs.Count(t => t.View is ShellTab s && s.Shell.Key == shell.Key);
        AddTab(n == 0 ? baseTitle : $"{baseTitle} ({n + 1})", new ShellTab(shell, _store, _config, dir));
    }

    void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabStrip.SelectedItem is not DocTab doc)
        {
            TabStrip.SelectedItem = _homeTab;
            return;
        }

        HomeView.Visibility = doc.IsHome ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in _tabs.Where(t => !t.IsHome))
            t.View!.Element.Visibility = t == doc ? Visibility.Visible : Visibility.Collapsed;

        if (doc.View is { } view)
        {
            Files.SetTool(view.Tool);
            if (Files.Follow) Files.Navigate(view.Cwd, false);
            Dispatcher.BeginInvoke(view.FocusInput, System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            Files.SetTool(null);
        }
    }

    void CloseTab(DocTab doc)
    {
        if (doc.IsHome) return;
        if (doc.View!.HasRunning &&
            MessageBox.Show(this, $"{doc.Title} has {doc.View.RunningCount} running command(s). Stop them and close the tab?", "Close tab",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var index = _tabs.IndexOf(doc);
        RemoveTab(doc);
        if (TabStrip.SelectedItem == null || !_tabs.Contains((DocTab)TabStrip.SelectedItem))
            TabStrip.SelectedItem = _tabs[Math.Clamp(index - 1, 0, _tabs.Count - 1)];
        UpdateRunningStatus();
    }

    void RemoveTab(DocTab doc)
    {
        var view = doc.View!;
        view.KillAll();
        view.Dispose();
        ContentHost.Children.Remove(view.Element);
        _tabs.Remove(doc);
        Dispatcher.BeginInvoke(UpdateTabScrollButtons, System.Windows.Threading.DispatcherPriority.Background);
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DocTab doc) CloseTab(doc);
        e.Handled = true;
    }

    void Find_Click(object sender, RoutedEventArgs e) => ActiveView?.OpenFind();

    /// <summary>Right-click on a tab header: export what its terminal shows, close tabs.</summary>
    void TabHeader_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var header = (FrameworkElement)sender;
        e.Handled = true;
        if (header.DataContext is not DocTab doc || doc.IsHome || doc.View is not { } view) return;
        var menu = new ContextMenu { PlacementTarget = header, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        FillTabMenu(menu, doc, view);
        menu.IsOpen = true;
    }

    void FillTabMenu(ContextMenu menu, DocTab doc, IDocView view)
    {
        if (view.Terminal is { } term)
        {
            menu.Items.Add(Item("Export text...", async () => await TerminalExport.SaveAsync(this, term, view.ExportName, _projectDir),
                bold: true, glyph: "\uE74E", color: "#1E73D8"));
            menu.Items.Add(Item("Export visible screen...", async () => await TerminalExport.SaveAsync(this, term, view.ExportName, _projectDir, visibleOnly: true),
                glyph: "\uE7F4"));
            menu.Items.Add(Item("Copy all text", async () => await TerminalExport.CopyAsync(term), glyph: "\uE8C8"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Find in terminal", () => { TabStrip.SelectedItem = doc; view.OpenFind(); }, glyph: "\uE721"));
        }
        if (view is ToolTab tt)
            menu.Items.Add(Item("Edit tool...", () => { if (_tools.FirstOrDefault(t => t.Id == tt.Tool.Id) is { } tool) EditTool(tool); },
                glyph: "\uE70F", color: "#1E73D8"));
        if (view is ShellTab st)
            menu.Items.Add(Item("New tab with this shell", () => OpenShell(st.Shell), glyph: "\uE756"));
        if (view is AgentTab at)
            menu.Items.Add(Item("New chat", () => at.NewChatPublic(), glyph: "\uE710", color: "#13A10E"));
        if (menu.Items.Count > 0 && menu.Items[menu.Items.Count - 1] is not Separator) menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close", () => CloseTab(doc), glyph: "\uE8BB"));
        var others = _tabs.Where(t => !t.IsHome && t != doc).ToList();
        menu.Items.Add(Item("Close other tabs", () => CloseTabs(others), enabled: others.Count > 0));
        var right = _tabs.Skip(_tabs.IndexOf(doc) + 1).ToList();
        menu.Items.Add(Item("Close tabs to the right", () => CloseTabs(right), enabled: right.Count > 0));
    }

    void CloseTabs(IEnumerable<DocTab> tabs)
    {
        foreach (var t in tabs.ToList()) CloseTab(t);
    }

    void CloseCurrentTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabStrip.SelectedItem is DocTab doc) CloseTab(doc);
    }

    void TabHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle-click closes a tab, like browsers and MobaXterm.
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is DocTab doc)
        {
            CloseTab(doc);
            e.Handled = true;
        }
    }

    void TabStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Wheel scrolls the strip when tabs overflow; otherwise it cycles the selection.
        var sv = TabScrollViewer;
        if (sv != null && sv.ScrollableWidth > 0)
            sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset - e.Delta, 0, sv.ScrollableWidth));
        else
            CycleTab(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    void TabScrollLeft_Click(object sender, RoutedEventArgs e) => ScrollTabs(-140);
    void TabScrollRight_Click(object sender, RoutedEventArgs e) => ScrollTabs(140);

    void ScrollTabs(double delta)
    {
        var sv = TabScrollViewer;
        if (sv != null) sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset + delta, 0, sv.ScrollableWidth));
    }

    void TabStrip_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabScrollButtons();

    ScrollViewer? TabScrollViewer => FindDescendant<ScrollViewer>(TabStrip);

    /// <summary>Shows the ◂ ▸ tab-scroll buttons only when the tabs overflow the strip.</summary>
    void UpdateTabScrollButtons()
    {
        var sv = TabScrollViewer;
        var show = sv != null && sv.ScrollableWidth > 0.5;
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        TabScrollLeft.Visibility = vis;
        TabScrollRight.Visibility = vis;
    }

    static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return null;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindDescendant<T>(c) is { } d) return d;
        }
        return null;
    }

    void CycleTab(int step)
    {
        if (_tabs.Count < 2) return;
        TabStrip.SelectedIndex = (TabStrip.SelectedIndex + step + _tabs.Count) % _tabs.Count;
        TabStrip.ScrollIntoView(TabStrip.SelectedItem);
    }

    /// <summary>Shows a short message in the status bar, then goes back to the running count.</summary>
    void ShowStatus(string message)
    {
        StatusRunning.Text = message;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => { timer.Stop(); UpdateRunningStatus(); };
        timer.Start();
    }

    void UpdateRunningStatus()
    {
        var n = Views.Sum(v => v.RunningCount);
        StatusRunning.Text = n == 0 ? "" : $"● {n} running";
    }

    // ───────────────────────── Files ↔ tab ─────────────────────────

    void OnFilesDirectoryChanged(string dir)
    {
        if (Files.Follow) ActiveView?.SetCwd(dir, false);
    }

    void OnTabCwdChanged(IDocView tab)
    {
        if (Files.Follow && tab == ActiveView) Files.Navigate(tab.Cwd, false);
    }

    void OnFilesInsertRequested(IReadOnlyList<string> paths)
    {
        if (ActiveView is not { } tab)
        {
            MessageBox.Show(this, "Open a tool or shell tab first to insert paths into it.", "TermDeck");
            return;
        }
        tab.InsertText(string.Join(" ", paths.Select(p => PathMapper.ForCommand(p, tab.Tool))));
    }

    // ───────────────────────── Sidebar ─────────────────────────

    void SideTab_Click(object sender, RoutedEventArgs e)
    {
        var tab = (RadioButton)sender;
        if (tab == _activeSide && _sidebarVisible) SetSidebar(false);
        else ShowSide(tab);
    }

    void ShowSide(RadioButton tab)
    {
        _activeSide = tab;
        tab.IsChecked = true;
        ToolsPanel.Visibility = tab == ToolsSideTab ? Visibility.Visible : Visibility.Collapsed;
        Files.Visibility = tab == FilesSideTab ? Visibility.Visible : Visibility.Collapsed;
        SetSidebar(true);
    }

    void SetSidebar(bool visible)
    {
        if (visible == _sidebarVisible) return;
        if (!visible)
        {
            _config.SidebarWidth = SidebarColumn.ActualWidth;
            SidebarColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            ToolsSideTab.IsChecked = FilesSideTab.IsChecked = false;
        }
        else
        {
            SidebarColumn.Width = new GridLength(Math.Max(160, _config.SidebarWidth));
            SplitterColumn.Width = new GridLength(5);
            _activeSide ??= ToolsSideTab;
            _activeSide.IsChecked = true;
        }
        _sidebarVisible = visible;
    }

    void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarVisible) SetSidebar(false);
        else ShowSide(_activeSide ?? ToolsSideTab);
    }

    void ShowTools_Click(object sender, RoutedEventArgs e) => ShowSide(ToolsSideTab);
    void ShowFiles_Click(object sender, RoutedEventArgs e) => ShowSide(FilesSideTab);

    // ───────────────────────── Tool tree ─────────────────────────

    /// <summary>Rebuilds the sidebar tree (filtered) and the Home groups (unfiltered) from tools + collections.</summary>
    void RebuildTree()
    {
        _treeNodes.Clear();
        foreach (var n in BuildNodes(_filter, trackExpansion: true)) _treeNodes.Add(n);
        _homeNodes.Clear();
        foreach (var n in BuildNodes("", trackExpansion: false).Where(n => n.Tools.Count > 0)) _homeNodes.Add(n);
        HomeNoTools.Visibility = _tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    List<CollectionNode> BuildNodes(string filter, bool trackExpansion)
    {
        var filtering = filter.Length > 0;
        Action<CollectionNode, bool>? onExpanded = trackExpansion && !filtering ? OnNodeExpanded : null;

        var nodes = _collections.Select(c => new CollectionNode(c, filtering || c.IsExpanded, onExpanded)).ToList();
        var ungrouped = new CollectionNode(null, filtering || _config.UngroupedExpanded, onExpanded);
        foreach (var tool in _tools)
        {
            if (filtering && !tool.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !tool.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var target = nodes.FirstOrDefault(n => n.Id == tool.CollectionId) ?? ungrouped;
            target.Tools.Add(tool);
        }

        // Empty collections stay visible (so tools can be dropped in) unless a filter is active.
        var result = filtering ? nodes.Where(n => n.Tools.Count > 0).ToList() : nodes;
        if (ungrouped.Tools.Count > 0) result.Add(ungrouped);
        return result;
    }

    void OnNodeExpanded(CollectionNode node, bool expanded)
    {
        if (node.Model != null) node.Model.IsExpanded = expanded;
        else _config.UngroupedExpanded = expanded;
    }

    void ToolSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = ToolSearch.Text.Trim();
        RebuildTree();
    }

    static TreeViewItem? ContainerFrom(DependencyObject? d)
    {
        while (d != null && d is not TreeViewItem)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    void ToolsTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ContainerFrom(e.OriginalSource as DependencyObject)?.DataContext is ToolDef tool)
        {
            OpenToolTab(tool);
            e.Handled = true;
        }
    }

    void ToolsTree_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when SelectedTool is { } t: OpenToolTab(t); break;
            case Key.Enter when SelectedNode is { IsUngrouped: false } n: OpenAll(n); break;
            case Key.Delete: Delete_Click(sender, e); break;
            case Key.F2: Edit_Click(sender, e); break;
        }
    }

    void ToolsTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click selects the item under the cursor so the context menu acts on it.
        var item = ContainerFrom(e.OriginalSource as DependencyObject);
        _contextTarget = item?.DataContext;
        if (item != null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    void ToolsTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = ToolsTree.ContextMenu!;
        menu.Items.Clear();
        switch (_contextTarget)
        {
            case ToolDef tool:
                menu.Items.Add(Item("Open", () => OpenToolTab(tool), bold: true, glyph: "", color: "#13A10E"));
                menu.Items.Add(Item("Open in new tab", () => OpenToolTab(tool, forceNew: true), glyph: "", color: "#13A10E"));
                menu.Items.Add(Item("Edit...", () => EditTool(tool), glyph: "", color: "#1E73D8"));
                menu.Items.Add(Item("Duplicate", () => DuplicateTool(tool), glyph: ""));
                var move = new MenuItem { Header = "Move to", Icon = Glyph("", "#C8930C") };
                foreach (var c in _collections)
                    move.Items.Add(Item(c.Name, () => MoveTool(tool, c.Id), isChecked: tool.CollectionId == c.Id));
                if (_collections.Count > 0) move.Items.Add(new Separator());
                move.Items.Add(Item("(Ungrouped)", () => MoveTool(tool, ""), isChecked: tool.CollectionId.Length == 0 || _collections.All(c => c.Id != tool.CollectionId)));
                move.Items.Add(new Separator());
                move.Items.Add(Item("New collection...", () => { var c = NewCollection(); if (c != null) MoveTool(tool, c.Id); }));
                menu.Items.Add(move);
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete", () => DeleteTool(tool), glyph: "", color: "#C50F1F"));
                break;
            case CollectionNode { IsUngrouped: false } node:
                menu.Items.Add(Item($"Open all ({node.Tools.Count})", () => OpenAll(node), bold: true, glyph: "", color: "#13A10E", enabled: node.Tools.Count > 0));
                menu.Items.Add(Item("New tool in this collection...", () => NewTool(node.Id), glyph: "", color: "#13A10E"));
                menu.Items.Add(Item("Rename...", () => RenameCollection(node), glyph: ""));
                menu.Items.Add(Item("Scan for tools into this collection...", () => ScanTools(node.Name), glyph: "\uE721"));
                menu.Items.Add(Item("Export collection...", () => ExportCollection(node), glyph: "\uEDE1", enabled: node.Tools.Count > 0));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete collection", () => DeleteCollection(node), glyph: "", color: "#C50F1F"));
                break;
            case CollectionNode:
                menu.Items.Add(Item("New tool...", () => NewTool(), glyph: "", color: "#13A10E"));
                break;
            default:
                menu.Items.Add(Item("New tool...", () => NewTool(), glyph: "", color: "#13A10E"));
                menu.Items.Add(Item("New collection...", () => NewCollection(), glyph: "", color: "#C8930C"));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Scan for tools...", () => ScanTools(""), glyph: "\uE721"));
                menu.Items.Add(Item("Import tools...", () => ImportTools_Click(this, new RoutedEventArgs()), glyph: "\uE8B5"));
                menu.Items.Add(Item("Export tools...", () => ExportTools_Click(this, new RoutedEventArgs()), glyph: "\uEDE1"));
                break;
        }
    }

    static MenuItem Item(string header, Action action, bool bold = false, string? glyph = null, string? color = null,
        bool isChecked = false, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        if (glyph != null) item.Icon = Glyph(glyph, color);
        item.Click += (_, _) => action();
        return item;
    }

    static TextBlock Glyph(string glyph, string? color) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
        Foreground = color == null ? (Brush)Application.Current.FindResource("TextSecondary") : (Brush)new BrushConverter().ConvertFromString(color)!,
    };

    // Drag a tool onto a collection (or onto another tool) to move it there.
    void ToolsTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragTool = ContainerFrom(e.OriginalSource as DependencyObject)?.DataContext as ToolDef;
    }

    void ToolsTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragTool == null) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var tool = _dragTool;
        _dragTool = null;
        DragDrop.DoDragDrop(ToolsTree, new DataObject(ToolDragFormat, tool.Id), DragDropEffects.Move);
    }

    string? DropTargetCollection(DragEventArgs e) =>
        ContainerFrom(e.OriginalSource as DependencyObject)?.DataContext switch
        {
            CollectionNode node => node.Id,
            ToolDef t => t.CollectionId,
            _ => null,
        };

    void ToolsTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ToolDragFormat) && DropTargetCollection(e) != null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void ToolsTree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ToolDragFormat) is not string id || DropTargetCollection(e) is not { } target) return;
        var tool = _tools.FirstOrDefault(t => t.Id == id);
        if (tool != null) MoveTool(tool, target);
    }

    // ───────────────────────── Tools & collections ─────────────────────────

    void HomeTool_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ToolDef t) OpenToolTab(t);
    }

    void NewTool_Click(object sender, RoutedEventArgs e) =>
        NewTool(SelectedNode?.Id ?? SelectedTool?.CollectionId ?? "");

    void NewTool(string collectionId = "")
    {
        var dlg = new ToolEditorWindow(null, _projectDir, _collections, collectionId) { Owner = this };
        var ok = dlg.ShowDialog() == true && dlg.Result != null;
        if (ok) _tools.Add(dlg.Result!);
        SaveConfig();
        RebuildTree();
        if (ok) OpenToolTab(dlg.Result!);
    }

    void NewCollection_Click(object sender, RoutedEventArgs e) => NewCollection();

    ToolCollection? NewCollection()
    {
        var name = PromptWindow.Ask(this, "New collection", "Collection name:", "");
        if (string.IsNullOrWhiteSpace(name)) return null;
        var collection = new ToolCollection { Name = name.Trim() };
        _collections.Add(collection);
        SaveConfig();
        RebuildTree();
        ShowSide(ToolsSideTab);
        return collection;
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is { IsUngrouped: false } node) RenameCollection(node);
        else if ((SelectedTool ?? ActiveToolTab?.Tool) is { } tool) EditTool(tool);
    }

    void EditTool(ToolDef tool)
    {
        var dlg = new ToolEditorWindow(tool, _projectDir, _collections) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } updated)
        {
            var i = _tools.IndexOf(tool);
            if (i >= 0) _tools[i] = updated;
            var doc = TabOf(updated.Id);
            if (doc != null)
            {
                ((ToolTab)doc.View!).UpdateTool(updated);
                doc.Title = updated.Name;
                if (doc == TabStrip.SelectedItem) Files.SetTool(updated);
            }
        }
        SaveConfig();
        RebuildTree();
    }

    void DuplicateTool_Click(object sender, RoutedEventArgs e)
    {
        if ((SelectedTool ?? ActiveToolTab?.Tool) is { } tool) DuplicateTool(tool);
    }

    void DuplicateTool(ToolDef tool)
    {
        var copy = tool.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = tool.Name + " (copy)";
        _tools.Insert(_tools.IndexOf(tool) + 1, copy);
        SaveConfig();
        RebuildTree();
    }

    void MoveTool(ToolDef tool, string collectionId)
    {
        if (tool.CollectionId == collectionId) return;
        tool.CollectionId = collectionId;
        SaveConfig();
        RebuildTree();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is { IsUngrouped: false } node) DeleteCollection(node);
        else if (SelectedTool is { } tool) DeleteTool(tool);
    }

    void DeleteTool(ToolDef tool)
    {
        var doc = TabOf(tool.Id);
        if (doc?.View?.HasRunning == true)
        {
            MessageBox.Show(this, "This tool is running. Stop it before deleting.", "TermDeck");
            return;
        }
        if (MessageBox.Show(this, $"Delete tool “{tool.Name}”?\nIts run history stays in the project folder.", "Delete tool",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (doc != null) CloseTab(doc);
        _tools.Remove(tool);
        SaveConfig();
        RebuildTree();
    }

    void RenameCollection(CollectionNode node)
    {
        var name = PromptWindow.Ask(this, "Rename collection", "Collection name:", node.Name);
        if (string.IsNullOrWhiteSpace(name) || node.Model == null) return;
        node.Model.Name = name.Trim();
        SaveConfig();
        RebuildTree();
    }

    void DeleteCollection(CollectionNode node)
    {
        if (node.Model == null) return;
        var msg = node.Tools.Count == 0
            ? $"Delete collection “{node.Name}”?"
            : $"Delete collection “{node.Name}”?\nIts {node.Tools.Count} tool(s) will move to Ungrouped (tools are not deleted).";
        if (MessageBox.Show(this, msg, "Delete collection", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var t in _tools.Where(t => t.CollectionId == node.Id)) t.CollectionId = "";
        _collections.Remove(node.Model);
        SaveConfig();
        RebuildTree();
    }

    void OpenAll(CollectionNode node)
    {
        foreach (var t in node.Tools.ToList()) OpenToolTab(t);
    }

    // ───────────────────────── Search, report ─────────────────────────

    void Search_Click(object sender, RoutedEventArgs e) => OpenSearch();

    void OpenSearch(string? query = null)
    {
        if (_store == null) return;
        if (_searchWindow == null)
        {
            // Not owned: after "Open in tab" the main window must be able to come in front of it.
            _searchWindow = new SearchWindow(_store, OpenRunInTab);
            _searchWindow.Closed += (_, _) => _searchWindow = null;
            _searchWindow.Show();
        }
        else
        {
            if (_searchWindow.WindowState == WindowState.Minimized) _searchWindow.WindowState = WindowState.Normal;
            _searchWindow.Activate();
        }
        _searchWindow.FocusQuery(query);
    }

    /// <summary>Opens the tool tab of a run from the Search window and shows that run. False if the tool no longer exists.</summary>
    bool OpenRunInTab(RunRecord run, string? find)
    {
        var tool = _tools.FirstOrDefault(t => t.Id == run.ToolId);
        if (tool == null) return false;
        OpenToolTab(tool);
        Activate();
        return TabOf(tool.Id)?.View is ToolTab tab && tab.SelectRun(run.Id, find);
    }

    void Report_Click(object sender, RoutedEventArgs e) => ExportReport(null);

    /// <param name="runs">Runs to include (e.g. search results); null = choose in the dialog from the whole project.</param>
    public void ExportReport(IReadOnlyList<RunRecord>? runs, Window? owner = null)
    {
        if (_store == null) return;
        new ReportWindow(_store, runs) { Owner = owner ?? this }.ShowDialog();
    }

    // ───────────────────────── Import / export tools ─────────────────────────

    void ScanTools_Click(object sender, RoutedEventArgs e) => ScanTools(SelectedNode is { IsUngrouped: false } n ? n.Name : "");

    void ScanTools(string collection)
    {
        var dlg = new ImportToolsWindow(_tools, _collections, collection) { Owner = this };
        if (dlg.ShowDialog() == true) AfterImport(dlg.ImportedCount);
    }

    void ImportTools_Click(object sender, RoutedEventArgs e)
    {
        var open = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import tools",
            Filter = "TermDeck tools (*.json)|*.json|All files (*.*)|*.*",
        };
        if (open.ShowDialog(this) != true) return;
        ToolPack pack;
        try { pack = ToolTransfer.Load(open.FileName); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not import this file:\n" + ex.Message, "Import tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (pack.Collections.Sum(c => c.Tools.Count) == 0)
        {
            MessageBox.Show(this, "The file contains no tools.", "Import tools");
            return;
        }
        var dlg = new ImportToolsWindow(_tools, _collections, pack, Path.GetFileName(open.FileName)) { Owner = this };
        if (dlg.ShowDialog() == true) AfterImport(dlg.ImportedCount);
    }

    void AfterImport(int count)
    {
        SaveConfig();
        RebuildTree();
        ShowSide(ToolsSideTab);
        // Tabs of replaced tools show the new definition.
        foreach (var doc in _tabs.Where(t => t.View is ToolTab).ToList())
        {
            var tab = (ToolTab)doc.View!;
            if (_tools.FirstOrDefault(t => t.Id == tab.Tool.Id) is { } tool && !ReferenceEquals(tool, tab.Tool))
            {
                tab.UpdateTool(tool);
                doc.Title = tool.Name;
            }
        }
        ShowStatus($"Imported {count} tool(s)");
    }

    void ExportTools_Click(object sender, RoutedEventArgs e)
    {
        if (_tools.Count == 0)
        {
            MessageBox.Show(this, "There are no tools to export.", "Export tools");
            return;
        }
        // Choose which collections to export.
        var ids = ExportToolsWindow.Pick(this, BuildNodes("", trackExpansion: false).Where(n => n.Tools.Count > 0).ToList());
        if (ids != null) SaveToolPack(ids, "all");
    }

    void ExportCollection(CollectionNode node) => SaveToolPack(new HashSet<string> { node.Id }, node.Name);

    void SaveToolPack(ICollection<string> collectionIds, string name)
    {
        var pack = ToolTransfer.Create(_tools, _collections, collectionIds);
        var count = pack.Collections.Sum(c => c.Tools.Count);
        if (count == 0)
        {
            MessageBox.Show(this, "No tools selected.", "Export tools");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export tools",
            FileName = ToolTransfer.DefaultFileName(name),
            Filter = "TermDeck tools (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ToolTransfer.Save(pack, dlg.FileName);
            ShowStatus($"Exported {count} tool(s)");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the file:\n" + ex.Message, "Export tools", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────────────────────── Misc ─────────────────────────

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (new SettingsWindow(_config) { Owner = this }.ShowDialog() != true) return;
        foreach (var v in Views) v.ApplyFont();
        RefreshAgentTabs();
        SaveConfig();
    }

    void OpenDataDir_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{AppPaths.DataDir}\"");

    void OpenHistoryDir_Click(object sender, RoutedEventArgs e)
    {
        if (_store != null) Process.Start("explorer.exe", $"\"{_store.Root}\"");
    }

    void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            $"TermDeck {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n\nRun Windows/WSL tools per project and keep every command and its output.\n\n" +
            $"Data: {AppPaths.DataDir}\nMode: {(AppPaths.IsPortable ? "Portable" : "Installed")}",
            "About TermDeck", MessageBoxButton.OK, MessageBoxImage.Information);

    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    bool ConfirmStopRunning(string message)
    {
        var running = Views.Sum(v => v.RunningCount);
        return running == 0 ||
               MessageBox.Show(this, $"{running} command(s) are running.\n{message}", "TermDeck",
                   MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmStopRunning("Exiting stops all of them. Continue?"))
        {
            e.Cancel = true;
            return;
        }
        foreach (var v in Views) v.KillAll();
        _searchWindow?.Close();
        DisposeAgent();
        SaveConfig();
    }

    void SaveConfig()
    {
        _config.Tools = _tools.ToList();
        _config.Collections = _collections.ToList();
        _config.RecentProjects = _recent.ToList();
        if (_sidebarVisible && SidebarColumn.ActualWidth > 0) _config.SidebarWidth = SidebarColumn.ActualWidth;
        try { ConfigStore.Save(_config); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save settings: " + ex.Message, "TermDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
