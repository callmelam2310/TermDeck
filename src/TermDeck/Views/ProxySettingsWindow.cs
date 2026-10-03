using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TermDeck.Core;

namespace TermDeck.Views;

/// <summary>
/// Edits the proxy profiles (name, URL, NO_PROXY). Works on copies; OK returns the edited list and the profile to
/// use (empty = off).
/// </summary>
public sealed class ProxySettingsWindow : Window
{
    readonly ListBox _list = new() { Width = 170, Margin = new Thickness(0, 0, 10, 0) };
    readonly TextBox _name = Tb();
    readonly TextBox _url = Tb(mono: true);
    readonly TextBox _noProxy = Tb(mono: true);
    readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    ProxyProfile? _current;
    bool _loading;

    public List<ProxyProfile> Profiles { get; }
    public string Active { get; private set; }

    public ProxySettingsWindow(IEnumerable<ProxyProfile> profiles, string active)
    {
        Profiles = profiles.Select(p => new ProxyProfile { Name = p.Name, Url = p.Url, NoProxy = p.NoProxy }).ToList();
        Active = active;
        Title = "Proxies";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ChromeBg");

        _list.Height = 230;
        _list.SelectionChanged += (_, _) => { if (!_loading) SwitchTo(_list.SelectedItem as ProxyProfile); };
        _list.DisplayMemberPath = nameof(ProxyProfile.Name);

        var left = new DockPanel();
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        btns.Children.Add(Small("New", NewProfile));
        btns.Children.Add(Small("Delete", DeleteProfile));
        DockPanel.SetDock(btns, Dock.Bottom);
        left.Children.Add(btns);
        left.Children.Add(_list);

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 8),
            Text = "e.g. http://127.0.0.1:8080 (Burp), socks5h://127.0.0.1:9050 (Tor), http://user:pass@proxy:3128.\n" +
                   "Runs get HTTP_PROXY / HTTPS_PROXY / ALL_PROXY (WSL also the lower-case names). With WSL in NAT mode a " +
                   "proxy on 127.0.0.1 is reached through the Windows host IP, so it must listen on all interfaces " +
                   "(Burp: bind to all interfaces) and be allowed by the firewall. Mirrored mode shares localhost.",
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var form = new StackPanel();
        form.Children.Add(Label("Name"));
        form.Children.Add(_name);
        form.Children.Add(Label("Proxy URL"));
        form.Children.Add(_url);
        form.Children.Add(Label("No proxy (NO_PROXY) — direct connections to these IPs/CIDRs are not reported"));
        form.Children.Add(_noProxy);
        form.Children.Add(hint);
        form.Children.Add(_error);

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(form);

        var use = new Button { Content = "Use selected", Width = 110, Margin = new Thickness(0, 0, 6, 0) };
        use.Click += (_, _) => { if (Commit()) { Active = _current?.Name ?? ""; DialogResult = true; } };
        var off = new Button { Content = "Use none (off)", Width = 110, Margin = new Thickness(0, 0, 6, 0) };
        off.Click += (_, _) => { if (Commit()) { Active = ""; DialogResult = true; } };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) =>
        {
            if (!Commit()) return;
            if (Profiles.All(p => p.Name != Active)) Active = "";
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in new[] { use, off, ok, cancel }) buttons.Children.Add(b);

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(body);
        root.Children.Add(buttons);
        Content = root;

        Reload(Profiles.FirstOrDefault(p => p.Name == active) ?? Profiles.FirstOrDefault());
    }

    void Reload(ProxyProfile? select)
    {
        _loading = true;
        _list.ItemsSource = null;
        _list.ItemsSource = Profiles;
        _list.SelectedItem = select;
        _loading = false;
        SwitchTo(select, commit: false);
    }

    void SwitchTo(ProxyProfile? p, bool commit = true)
    {
        if (commit && _current != null && !Commit())
        {
            // Stay on the invalid profile so the user can fix it.
            _loading = true;
            _list.SelectedItem = _current;
            _loading = false;
            return;
        }
        _current = p;
        _name.Text = p?.Name ?? "";
        _url.Text = p?.Url ?? "";
        _noProxy.Text = p?.NoProxy ?? "";
        foreach (var c in new Control[] { _name, _url, _noProxy }) c.IsEnabled = p != null;
    }

    /// <summary>Validates and writes the form into the current profile.</summary>
    bool Commit()
    {
        _error.Text = "";
        if (_current == null) return true;
        var name = _name.Text.Trim();
        if (name.Length == 0) { _error.Text = "Name is required."; return false; }
        if (Profiles.Any(p => p != _current && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _error.Text = $"Another proxy is already called \"{name}\".";
            return false;
        }
        if (!Proxy.TryParse(_url.Text, out _))
        {
            _error.Text = "Proxy URL must look like http://host:port or socks5://host:port (http, https, socks4, socks4a, socks5, socks5h).";
            return false;
        }
        if (Active == _current.Name) Active = name;
        _current.Name = name;
        _current.Url = _url.Text.Trim();
        _current.NoProxy = _noProxy.Text.Trim();
        _loading = true;
        _list.Items.Refresh();
        _loading = false;
        return true;
    }

    void NewProfile()
    {
        if (!Commit()) return;
        var n = "Burp";
        for (var i = 2; Profiles.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); i++) n = $"Proxy {i}";
        var p = new ProxyProfile { Name = n, Url = "http://127.0.0.1:8080" };
        Profiles.Add(p);
        _current = null;
        Reload(p);
        _name.Focus();
        _name.SelectAll();
    }

    void DeleteProfile()
    {
        if (_current == null) return;
        if (Active == _current.Name) Active = "";
        Profiles.Remove(_current);
        _current = null;
        Reload(Profiles.FirstOrDefault());
    }

    static TextBox Tb(bool mono = false)
    {
        var b = new TextBox { Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 8), VerticalContentAlignment = VerticalAlignment.Center };
        if (mono) b.SetResourceReference(FontFamilyProperty, "MonoFont");
        return b;
    }

    static TextBlock Label(string t) => new() { Text = t, Margin = new Thickness(0, 2, 0, 0) };

    static Button Small(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }
}
