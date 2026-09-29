using System.Windows;
using System.Windows.Controls;

namespace TermDeck.Views;

/// <summary>Single-line text input dialog.</summary>
public sealed class PromptWindow : Window
{
    readonly TextBox _box;

    PromptWindow(string title, string label, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _box = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(3) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = label });
        root.Children.Add(_box);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _box.Focus();
            var dot = _box.Text.LastIndexOf('.');
            if (dot > 0) _box.Select(0, dot); else _box.SelectAll();
        };
    }

    public static string? Ask(Window? owner, string title, string label, string initial = "")
    {
        var w = new PromptWindow(title, label, initial) { Owner = owner };
        return w.ShowDialog() == true ? w._box.Text : null;
    }
}
