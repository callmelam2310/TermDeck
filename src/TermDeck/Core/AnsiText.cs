using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace TermDeck.Core;

/// <summary>
/// Turns raw terminal output (as recorded in .cast files) into readable text for the search index and reports.
/// ConPTY draws with cursor movement and repaints the screen on resize, so the output is replayed on a small
/// text screen (<see cref="TextScreen"/>) instead of being filtered line by line.
/// </summary>
public static class AnsiText
{
    /// <param name="keepSgr">Keep color/style sequences (ESC[...m) so the text can be converted with <see cref="ToHtml"/>.</param>
    public static string Clean(string raw, bool keepSgr = false, int rows = 30)
    {
        var screen = new TextScreen(rows, keepSgr);
        screen.Feed(raw);
        return screen.Result();
    }

    /// <summary>Replays a .cast file (output and resize events) and returns its text.</summary>
    public static string FromCast(string path, bool keepSgr = false)
    {
        var (_, rows, events) = CastReader.ReadEvents(path);
        var screen = new TextScreen(rows, keepSgr);
        foreach (var (type, data) in events)
        {
            if (type == 'o') screen.Feed(data);
            else if (type == 'r' && TryParseSize(data, out var r)) screen.Resize(r);
        }
        return screen.Result();
    }

    static bool TryParseSize(string s, out int rows)
    {
        rows = 0;
        var x = s.IndexOf('x');
        return x > 0 && int.TryParse(s.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out rows) && rows > 0;
    }

    /// <summary>Removes the ESC[...m sequences left by <c>Clean(raw, keepSgr: true)</c>.</summary>
    public static string StripSgr(string s)
    {
        if (s.IndexOf('\x1b') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\x1b')
            {
                var m = s.IndexOf('m', i);
                if (m < 0) break;
                i = m;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // ───────────────────────── SGR → HTML ─────────────────────────

    /// <summary>Same palette as the terminal (terminal.html).</summary>
    static readonly string[] Palette =
    [
        "#0c0c0c", "#c50f1f", "#13a10e", "#c19c00", "#0037da", "#881798", "#3a96dd", "#cccccc",
        "#767676", "#e74856", "#16c60c", "#f9f1a5", "#3b78ff", "#b4009e", "#61d6d6", "#f2f2f2",
    ];

    static string Color256(int n)
    {
        if (n < 16) return Palette[Math.Max(0, n)];
        if (n < 232)
        {
            n -= 16;
            static int Level(int v) => v == 0 ? 0 : 55 + v * 40;
            return $"#{Level(n / 36):x2}{Level(n / 6 % 6):x2}{Level(n % 6):x2}";
        }
        var g = 8 + (Math.Min(n, 255) - 232) * 10;
        return $"#{g:x2}{g:x2}{g:x2}";
    }

    sealed class Style
    {
        public string? Fg, Bg;
        public bool Bold, Dim, Italic, Underline, Inverse;

        public void Reset() { Fg = Bg = null; Bold = Dim = Italic = Underline = Inverse = false; }

        public string Css()
        {
            var fg = Inverse ? Bg ?? "#0c0c0c" : Fg;
            var bg = Inverse ? Fg ?? "#cccccc" : Bg;
            var sb = new StringBuilder();
            if (fg != null) sb.Append("color:").Append(fg).Append(';');
            if (bg != null) sb.Append("background:").Append(bg).Append(';');
            if (Bold) sb.Append("font-weight:bold;");
            if (Dim) sb.Append("opacity:.7;");
            if (Italic) sb.Append("font-style:italic;");
            if (Underline) sb.Append("text-decoration:underline;");
            return sb.ToString();
        }
    }

    /// <summary>Converts text from <c>Clean(raw, keepSgr: true)</c> into HTML-escaped text with colored &lt;span&gt;s.</summary>
    public static string ToHtml(string s)
    {
        var sb = new StringBuilder(s.Length + s.Length / 4);
        var style = new Style();
        var open = false;
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length == 0) return;
            sb.Append(WebUtility.HtmlEncode(text.ToString()));
            text.Clear();
        }

        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                var m = s.IndexOf('m', i);
                if (m < 0) break;
                FlushText();
                Apply(style, s.AsSpan(i + 2, m - i - 2));
                if (open) sb.Append("</span>");
                var css = style.Css();
                open = css.Length > 0;
                if (open) sb.Append("<span style=\"").Append(css).Append("\">");
                i = m + 1;
                continue;
            }
            text.Append(s[i]);
            i++;
        }
        FlushText();
        if (open) sb.Append("</span>");
        return sb.ToString();
    }

    static void Apply(Style st, ReadOnlySpan<char> p)
    {
        var codes = new List<int>();
        foreach (var part in p.ToString().Split(';', ':'))
            codes.Add(int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0);
        if (codes.Count == 0) codes.Add(0);

        for (var k = 0; k < codes.Count; k++)
        {
            var c = codes[k];
            switch (c)
            {
                case 0: st.Reset(); break;
                case 1: st.Bold = true; break;
                case 2: st.Dim = true; break;
                case 3: st.Italic = true; break;
                case 4: st.Underline = true; break;
                case 7: st.Inverse = true; break;
                case 22: st.Bold = st.Dim = false; break;
                case 23: st.Italic = false; break;
                case 24: st.Underline = false; break;
                case 27: st.Inverse = false; break;
                case >= 30 and <= 37: st.Fg = Palette[c - 30]; break;
                case 39: st.Fg = null; break;
                case >= 40 and <= 47: st.Bg = Palette[c - 40]; break;
                case 49: st.Bg = null; break;
                case >= 90 and <= 97: st.Fg = Palette[c - 90 + 8]; break;
                case >= 100 and <= 107: st.Bg = Palette[c - 100 + 8]; break;
                case 38 or 48:
                    string? color = null;
                    if (k + 2 < codes.Count && codes[k + 1] == 5)
                    {
                        color = Color256(codes[k + 2]);
                        k += 2;
                    }
                    else if (k + 4 < codes.Count && codes[k + 1] == 2)
                    {
                        color = $"#{Math.Clamp(codes[k + 2], 0, 255):x2}{Math.Clamp(codes[k + 3], 0, 255):x2}{Math.Clamp(codes[k + 4], 0, 255):x2}";
                        k += 4;
                    }
                    if (c == 38) st.Fg = color; else st.Bg = color;
                    break;
            }
        }
    }
}
