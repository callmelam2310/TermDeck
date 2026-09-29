using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using TermDeck.Core;

namespace TermDeck.Views;

/// <summary>
/// Light/dark appearance. Standard controls use WPF's built-in Fluent theme (ThemeMode Light/Dark);
/// TermDeck's own chrome colors are brush resources swapped here and consumed via DynamicResource.
/// The terminal itself stays dark in both modes.
/// </summary>
public static class ThemeManager
{
    static readonly Dictionary<string, string> Light = new()
    {
        ["ChromeBg"] = "#F3F3F3",
        ["ChromeBorder"] = "#D4D4D4",
        ["PanelBg"] = "#FFFFFF",
        ["Accent"] = "#2B6CB0",
        ["HoverBg"] = "#E5F1FB",
        ["HoverBorder"] = "#9CC7EE",
        ["PressedBg"] = "#CCE4F7",
        ["SelectedBg"] = "#DCEBFA",
        ["ToolbarBg"] = "#FBFBFB",
        ["SideStripBg"] = "#E6E6E6",
        ["TabBg"] = "#E4E4E4",
        ["TabHoverBg"] = "#EFEFEF",
        ["TabSelectedBg"] = "#FFFFFF",
        ["CommandBarBg"] = "#F7F7F7",
        ["PillBg"] = "#E6E6E6",
        ["TextPrimary"] = "#1B1B1B",
        ["TextSecondary"] = "#555555",
        ["TextMuted"] = "#7A7A7A",
        ["WinBadge"] = "#1E73D8",
        ["WslBadge"] = "#E0701E",
    };

    static readonly Dictionary<string, string> Dark = new()
    {
        ["ChromeBg"] = "#202020",
        ["ChromeBorder"] = "#3A3A3A",
        ["PanelBg"] = "#272727",
        ["Accent"] = "#4C9BE8",
        ["HoverBg"] = "#2D3B4A",
        ["HoverBorder"] = "#3F6D96",
        ["PressedBg"] = "#1F4A70",
        ["SelectedBg"] = "#1F3A57",
        ["ToolbarBg"] = "#262626",
        ["SideStripBg"] = "#1A1A1A",
        ["TabBg"] = "#2B2B2B",
        ["TabHoverBg"] = "#343434",
        ["TabSelectedBg"] = "#383838",
        ["CommandBarBg"] = "#242424",
        ["PillBg"] = "#3A3A3A",
        ["TextPrimary"] = "#F0F0F0",
        ["TextSecondary"] = "#C8C8C8",
        ["TextMuted"] = "#9A9A9A",
        ["WinBadge"] = "#1E73D8",
        ["WslBadge"] = "#E0701E",
    };

    static AppTheme _current = AppTheme.System;
    static bool _listening;

    public static bool IsDark { get; private set; }

    public static void Apply(AppTheme theme)
    {
        _current = theme;
        IsDark = theme == AppTheme.Dark || (theme == AppTheme.System && SystemUsesDarkTheme());

#pragma warning disable WPF0001 // ThemeMode is marked experimental
        Application.Current.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var resources = Application.Current.Resources;
        foreach (var (key, hex) in IsDark ? Dark : Light)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            resources[key] = brush;
        }

        if (!_listening)
        {
            // Follow Windows when the user flips the system theme while TermDeck is open.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _current == AppTheme.System)
                    Application.Current.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
            };
            _listening = true;
        }
    }

    /// <summary>Windows "Choose your app mode" setting (AppsUseLightTheme = 0 means dark).</summary>
    static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
