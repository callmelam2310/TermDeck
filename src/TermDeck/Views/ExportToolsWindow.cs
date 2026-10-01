using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TermDeck.Views;

/// <summary>Picks which collections to export to a tools file.</summary>
public sealed class ExportToolsWindow : Window
{
    readonly List<CheckBox> _checks = new();

    ExportToolsWindow(IReadOnlyList<CollectionNode> nodes)
    {
        Title = "Export tools";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ChromeBg");

        var list = new StackPanel();
        foreach (var n in nodes)
        {
            var cb = new CheckBox { Content = $"{n.Name}  ({n.Tools.Count} tool{(n.Tools.Count == 1 ? "" : "s")})", Tag = n.Id, IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
            _checks.Add(cb);
            list.Children.Add(cb);
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var ok = new Button { Content = "Export...", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock
        {
            Text = "Collections to export. The file can be imported on another machine (Tools → Import tools...). Passwords are never stored in it.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        root.Children.Add(scroll);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>Returns the chosen collection ids ("" = ungrouped), or null if cancelled.</summary>
    public static HashSet<string>? Pick(Window owner, IReadOnlyList<CollectionNode> nodes)
    {
        var w = new ExportToolsWindow(nodes) { Owner = owner };
        if (w.ShowDialog() != true) return null;
        return w._checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToHashSet();
    }
}
