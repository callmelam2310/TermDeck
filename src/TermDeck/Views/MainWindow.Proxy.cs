using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TermDeck.Core;

namespace TermDeck.Views;

/// <summary>
/// Proxy: the status bar picks the active proxy profile for new runs (tools and shells). Runs already going keep
/// what they started with. When a run is seen connecting somewhere directly, the status bar turns amber and the
/// menu lists those connections until it is opened.
/// </summary>
public partial class MainWindow
{
    /// <summary>Direct connections reported since the proxy menu was last opened: "tool #id → endpoint".</summary>
    readonly List<string> _proxyLeaks = new();

    void InitProxy()
    {
        ApplyActiveProxy();
        Proxy.LeakDetected += (record, endpoint) => Dispatcher.BeginInvoke(() =>
        {
            _proxyLeaks.Add($"{record.ToolName} #{record.Id} → {endpoint}");
            UpdateProxyStatus();
            ShowStatus($"⚠ {record.ToolName} #{record.Id} is not going through the proxy: {endpoint}");
        });
    }

    /// <summary>Points <see cref="Proxy.Current"/> at the configured profile (or off if it is missing/invalid).</summary>
    void ApplyActiveProxy()
    {
        var p = _config.Proxies.FirstOrDefault(x => x.Name == _config.ActiveProxy);
        Proxy.Current = p != null && Proxy.TryParse(p.Url, out _) ? p : null;
        UpdateProxyStatus();
    }

    void SetActiveProxy(string name)
    {
        _config.ActiveProxy = name;
        SaveConfig();
        ApplyActiveProxy();
        ShowStatus(Proxy.Current == null ? "Proxy off — new runs go direct" : $"New runs go through proxy {Proxy.Current.Name} ({Proxy.Masked(Proxy.Current)})");
    }

    void UpdateProxyStatus()
    {
        var p = Proxy.Current;
        if (p == null)
        {
            StatusProxy.Text = "🌐 Proxy off";
            StatusProxy.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            StatusProxy.ToolTip = "Proxy — click to send new runs through a proxy";
            return;
        }
        StatusProxy.Text = _proxyLeaks.Count > 0 ? $"🌐 {p.Name} · ⚠ {_proxyLeaks.Count} direct" : $"🌐 {p.Name}";
        if (_proxyLeaks.Count > 0) StatusProxy.Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x93, 0x0C));
        else StatusProxy.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        StatusProxy.ToolTip = $"New runs get HTTP_PROXY / HTTPS_PROXY / ALL_PROXY = {Proxy.Masked(p)}.\n" +
                              "Each run's connections are watched; anything not going to the proxy is reported.\n" +
                              "Raw-socket traffic (SYN scans, ping) and UDP cannot be seen.";
    }

    void StatusProxy_Click(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = StatusProxy, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        menu.Items.Add(Item("Off (direct)", () => SetActiveProxy(""), isChecked: Proxy.Current == null));
        foreach (var p in _config.Proxies)
        {
            var valid = Proxy.TryParse(p.Url, out _);
            var name = p.Name;
            menu.Items.Add(Item($"{p.Name}   {Proxy.Masked(p)}", () => SetActiveProxy(name), isChecked: Proxy.Current?.Name == p.Name, enabled: valid));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Manage proxies...", OpenProxySettings, color: "#0F6CBD"));
        if (_proxyLeaks.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Not through the proxy:", IsEnabled = false });
            foreach (var l in _proxyLeaks.TakeLast(12)) menu.Items.Add(new MenuItem { Header = "  " + l, IsEnabled = false });
            if (_proxyLeaks.Count > 12) menu.Items.Add(new MenuItem { Header = $"  … {_proxyLeaks.Count - 12} more", IsEnabled = false });
            menu.Items.Add(Item("Clear", () => { _proxyLeaks.Clear(); UpdateProxyStatus(); }));
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    void OpenProxySettings()
    {
        var w = new ProxySettingsWindow(_config.Proxies, _config.ActiveProxy) { Owner = this };
        if (w.ShowDialog() != true) return;
        _config.Proxies = w.Profiles;
        _config.ActiveProxy = w.Active;
        SaveConfig();
        ApplyActiveProxy();
    }
}
