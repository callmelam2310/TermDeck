using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TermDeck.Core;

namespace TermDeck.Terminal;

/// <summary>
/// Terminal pane: xterm.js inside WebView2. Kept isolated so it can be swapped for another terminal control later.
/// </summary>
public sealed class TerminalHost : Border, IDisposable
{
    static Task<CoreWebView2Environment>? _env;
    static string? _pageTemplate;

    readonly WebView2 _web = new();
    readonly List<string> _pending = new();
    bool _initStarted;
    bool _ready;

    public int Cols { get; private set; } = 120;
    public int Rows { get; private set; } = 30;
    public bool IsReady => _ready;

    public event Action<string>? Input;
    public event Action<int, int>? Resized;
    public event Action? Ready;

    public string FontFamily { get; set; } = "Cascadia Mono, Consolas, monospace";
    public int FontSize { get; set; } = 14;

    public TerminalHost()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x0c, 0x0c, 0x0c));
        Child = _web;
        Loaded += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            _env ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDir);
            await _web.EnsureCoreWebView2Async(await _env);
            var s = _web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
#if !DEBUG
            s.AreDevToolsEnabled = false;
#endif
            _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0c, 0x0c, 0x0c);
            _web.CoreWebView2.WebMessageReceived += OnMessage;
            _web.NavigateToString(BuildPage());
        }
        catch (Exception ex)
        {
            Child = new TextBlock
            {
                Text = "Could not initialize WebView2 to display the terminal.\n" +
                       "Install the WebView2 Runtime: https://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n" + ex.Message,
                Foreground = Brushes.OrangeRed,
                Margin = new Thickness(12),
                TextWrapping = TextWrapping.Wrap,
            };
        }
    }

    string BuildPage()
    {
        _pageTemplate ??= ReadResource("terminal.html")
            .Replace("{{CSS}}", ReadResource("xterm.css"))
            .Replace("{{XTERM}}", ReadResource("xterm.js"))
            .Replace("{{FIT}}", ReadResource("addon-fit.js"))
            .Replace("{{SEARCH}}", ReadResource("addon-search.js"));
        return _pageTemplate
            .Replace("{{FONT}}", JsonSerializer.Serialize(FontFamily))
            .Replace("{{SIZE}}", FontSize.ToString());
    }

    static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException("Missing resource " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                Cols = m.GetProperty("cols").GetInt32();
                Rows = m.GetProperty("rows").GetInt32();
                _ready = true;
                foreach (var p in _pending) PostRaw(p);
                _pending.Clear();
                Ready?.Invoke();
                break;
            case "resize":
                Cols = m.GetProperty("cols").GetInt32();
                Rows = m.GetProperty("rows").GetInt32();
                Resized?.Invoke(Cols, Rows);
                break;
            case "input":
                Input?.Invoke(m.GetProperty("d").GetString() ?? "");
                break;
            case "select":
            case "copy":
                var text = m.GetProperty("d").GetString();
                if (!string.IsNullOrEmpty(text)) TrySetClipboard(text);
                break;
            case "paste":
                if (Clipboard.ContainsText())
                    Input?.Invoke(Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r'));
                break;
        }
    }

    static void TrySetClipboard(string text)
    {
        // The clipboard may be locked by another app.
        for (var i = 0; i < 3; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(20); }
        }
    }

    void Post(object msg)
    {
        var json = JsonSerializer.Serialize(msg);
        if (_ready) PostRaw(json);
        else _pending.Add(json);
    }

    void PostRaw(string json) => _web.CoreWebView2?.PostWebMessageAsJson(json);

    public void Write(string data)
    {
        if (data.Length > 0) Post(new { t = "w", d = data });
    }

    public void Reset()
    {
        _pending.Clear();
        Post(new { t = "reset" });
    }

    /// <summary>Opens the terminal's find bar (Ctrl+F).</summary>
    public void OpenFind()
    {
        _web.Focus();
        Post(new { t = "find" });
    }

    public void FocusTerminal()
    {
        _web.Focus();
        Post(new { t = "focus" });
    }

    /// <summary>Plain text of the whole rendered buffer — more accurate than stripping ANSI codes, since ConPTY also emits cursor movement.</summary>
    public async Task<string> GetBufferTextAsync()
    {
        if (!_ready || _web.CoreWebView2 == null) return "";
        const string script = """
            (() => {
              const b = term.buffer.active, out = [];
              for (let i = 0; i < b.length; i++) {
                const line = b.getLine(i);
                const text = line.translateToString(true);
                if (line.isWrapped && out.length) out[out.length - 1] += text; else out.push(text);
              }
              while (out.length && out[out.length - 1] === '') out.pop();
              return out.join('\r\n');
            })()
            """;
        var json = await _web.CoreWebView2.ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    public void Dispose()
    {
        _ready = false;
        _web.Dispose();
    }

    public void SetFont(string family, int size)
    {
        FontFamily = family;
        FontSize = size;
        if (_ready) Post(new { t = "font", family, size });
    }
}
