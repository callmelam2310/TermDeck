using System;
using System.Windows;
using System.Windows.Controls;
using TermDeck.Core;

namespace TermDeck.Views;

/// <summary>General settings: appearance (light/dark), terminal font, and where data is stored.</summary>
public sealed class SettingsWindow : Window
{
    readonly AppConfig _config;
    readonly AppTheme _originalTheme;
    readonly ComboBox _theme;
    readonly TextBox _font;
    readonly ComboBox _size;

    public SettingsWindow(AppConfig config)
    {
        _config = config;
        _originalTheme = config.Theme;
        Title = "Settings";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ChromeBg");

        _theme = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 4) };
        _theme.Items.Add(new ComboBoxItem { Content = "Use system setting", Tag = AppTheme.System });
        _theme.Items.Add(new ComboBoxItem { Content = "Light", Tag = AppTheme.Light });
        _theme.Items.Add(new ComboBoxItem { Content = "Dark", Tag = AppTheme.Dark });
        _theme.SelectedIndex = (int)config.Theme;
        // Live preview; Cancel restores the original theme.
        _theme.SelectionChanged += (_, _) => ThemeManager.Apply(SelectedTheme);

        var appearance = new StackPanel();
        appearance.Children.Add(new TextBlock { Text = "Theme" });
        appearance.Children.Add(_theme);
        appearance.Children.Add(Hint("The terminal area stays dark in both themes."));

        _font = new TextBox { Text = config.TerminalFont, Margin = new Thickness(0, 3, 0, 10) };
        _size = new ComboBox { IsEditable = true, Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 4) };
        foreach (var s in new[] { 11, 12, 13, 14, 15, 16, 18, 20 }) _size.Items.Add(s.ToString());
        _size.Text = config.TerminalFontSize.ToString();

        var terminal = new StackPanel();
        terminal.Children.Add(new TextBlock { Text = "Font (CSS font-family)" });
        terminal.Children.Add(_font);
        terminal.Children.Add(new TextBlock { Text = "Font size" });
        terminal.Children.Add(_size);

        var data = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = (AppPaths.IsPortable ? "Mode: Portable\n" : "Mode: Installed\n") +
                   $"Config: {AppPaths.ConfigFile}\n" +
                   "Run history: <project>\\.termdeck\\ (history.db + runs\\*.cast)\n\n" +
                   "To run portable, put a portable.txt file (or a data\\ folder) next to TermDeck.exe.",
        };

        var ok = new Button { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(Group("Appearance", appearance, 0));
        root.Children.Add(Group("Terminal", terminal, 10));
        root.Children.Add(Group("Data", data, 10));
        root.Children.Add(buttons);
        Content = root;

        Closed += (_, _) =>
        {
            if (DialogResult != true && _config.Theme == _originalTheme) ThemeManager.Apply(_originalTheme);
        };
    }

    AppTheme SelectedTheme => (_theme.SelectedItem as ComboBoxItem)?.Tag is AppTheme t ? t : AppTheme.System;

    static GroupBox Group(string header, UIElement content, double top)
    {
        var g = new GroupBox { Header = header, Padding = new Thickness(10), Content = content, Margin = new Thickness(0, top, 0, 0) };
        g.SetResourceReference(BackgroundProperty, "PanelBg");
        return g;
    }

    static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        return t;
    }

    void Save()
    {
        if (!int.TryParse(_size.Text, out var size) || size < 6 || size > 48)
        {
            MessageBox.Show(this, "Invalid font size (6–48).");
            return;
        }
        _config.Theme = SelectedTheme;
        _config.TerminalFont = string.IsNullOrWhiteSpace(_font.Text) ? "Consolas, monospace" : _font.Text.Trim();
        _config.TerminalFontSize = size;
        DialogResult = true;
    }
}
