using System;
using System.Text.RegularExpressions;

namespace TermDeck.Core;

/// <summary>
/// Windows ↔ Linux (WSL) path conversion. Internally the app always keeps Windows paths
/// (C:\x or \\wsl.localhost\distro\x) and converts to Linux form only to display/run WSL tools.
/// </summary>
public static partial class PathMapper
{
    static readonly string[] WslPrefixes = [@"\\wsl.localhost\", @"\\wsl$\"];

    public static bool TryParseWslUnc(string path, out string distro, out string rest)
    {
        foreach (var prefix in WslPrefixes)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var tail = path[prefix.Length..];
            var i = tail.IndexOf('\\');
            distro = i < 0 ? tail : tail[..i];
            rest = i < 0 ? "" : tail[(i + 1)..];
            return distro.Length > 0;
        }
        distro = rest = "";
        return false;
    }

    public static string ToLinux(string win)
    {
        if (TryParseWslUnc(win, out _, out var rest))
        {
            rest = rest.Replace('\\', '/').Trim('/');
            return "/" + rest;
        }
        if (win.Length >= 2 && win[1] == ':')
        {
            var drive = char.ToLowerInvariant(win[0]);
            var tail = win[2..].Replace('\\', '/').Trim('/');
            return tail.Length == 0 ? $"/mnt/{drive}" : $"/mnt/{drive}/{tail}";
        }
        return win.Replace('\\', '/');
    }

    public static string ToWindows(string linux, string distro)
    {
        var m = MntRegex().Match(linux);
        if (m.Success)
        {
            var rest = m.Groups[2].Value.Trim('/').Replace('/', '\\');
            return $"{char.ToUpperInvariant(m.Groups[1].Value[0])}:\\{rest}";
        }
        var p = linux.Trim().TrimEnd('/');
        return $@"\\wsl.localhost\{distro}" + p.Replace('/', '\\');
    }

    /// <summary>Path as displayed for the given tool.</summary>
    public static string Display(string win, ToolDef? tool) =>
        tool?.Kind == ToolKind.Wsl ? ToLinux(win) : win;

    /// <summary>Path typed by the user: accepts both Windows and Linux forms.</summary>
    public static string FromUserInput(string input, ToolDef? tool)
    {
        input = input.Trim().Trim('"', '\'');
        if (input.StartsWith('/'))
        {
            var distro = tool?.Kind == ToolKind.Wsl && !string.IsNullOrEmpty(tool.Distro)
                ? tool.Distro
                : WslInfo.DefaultDistro() ?? "";
            return ToWindows(input, distro);
        }
        return input;
    }

    /// <summary>Path to insert into the tool's command line (quoted when needed).</summary>
    public static string ForCommand(string win, ToolDef tool)
    {
        if (tool.Kind == ToolKind.Wsl)
        {
            var p = ToLinux(win);
            return NeedsQuote(p) ? "'" + p.Replace("'", "'\\''") + "'" : p;
        }
        return NeedsQuote(win) ? "\"" + win + "\"" : win;
    }

    static bool NeedsQuote(string s) => s.IndexOfAny([' ', '\t', '&', '(', ')', ';', '\'', '"', '$', '`']) >= 0;

    [GeneratedRegex(@"^/mnt/([a-zA-Z])(/.*)?$")]
    private static partial Regex MntRegex();
}
