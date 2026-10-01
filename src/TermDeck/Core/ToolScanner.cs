using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TermDeck.Core;

/// <summary>An executable found by a scan, not yet added as a tool.</summary>
public sealed record FoundTool(string Name, string Path, string Folder);

/// <summary>Finds executables to add as tools in bulk: WSL bin folders (~/go/bin, pipx, cargo…) or a Windows folder.</summary>
public static class ToolScanner
{
    /// <summary>Folders offered by default; (path, checked by default). /usr/bin is huge, so it is off by default.</summary>
    public static readonly (string Path, bool On)[] WslDefaults =
    [
        ("~/go/bin", true),
        ("~/.local/bin", true),
        ("~/.cargo/bin", true),
        ("/usr/local/bin", true),
        ("/usr/local/go/bin", false),
        ("/usr/bin", false),
        ("/usr/sbin", false),
    ];

    /// <summary>Lists executable files (symlinks followed) directly inside each folder. Returns null if WSL could not be run.</summary>
    public static List<FoundTool>? ScanWsl(string? distro, IEnumerable<string> folders)
    {
        var dirs = folders.Select(f => f.Trim()).Where(f => f.Length > 0).Distinct().ToList();
        if (dirs.Count == 0) return new List<FoundTool>();

        // Each folder is echoed as a "#" line so results can be grouped by the folder as the user typed it.
        var sb = new StringBuilder();
        foreach (var d in dirs)
        {
            sb.Append("printf '#%s\\n' ").Append(ShQuote(d)).Append("; ");
            sb.Append("d=").Append(ShPath(d)).Append("; ");
            sb.Append("if [ -d \"$d\" ]; then for f in \"$d\"/*; do [ -f \"$f\" ] && [ -x \"$f\" ] && printf '%s\\n' \"$f\"; done; fi; ");
        }
        var output = WslInfo.RunScript(string.IsNullOrWhiteSpace(distro) ? null : distro, sb.ToString());
        if (output == null) return null;

        var found = new List<FoundTool>();
        var folder = "";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] == '#') { folder = line[1..]; continue; }
            var name = line[(line.LastIndexOf('/') + 1)..];
            if (name.Length > 0) found.Add(new FoundTool(name, line, folder));
        }
        return found;
    }

    /// <summary>Lists *.exe files in a Windows folder (optionally up to 3 levels of subfolders).</summary>
    public static List<FoundTool> ScanWindows(string folder, bool recursive)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = recursive,
            MaxRecursionDepth = 3,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        var list = new List<FoundTool>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.exe", options))
        {
            list.Add(new FoundTool(Path.GetFileNameWithoutExtension(file), PortablePath(file),
                Path.GetDirectoryName(file) ?? folder));
        }
        return list.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Paths inside the TermDeck folder are stored relative (.\tools\x.exe) so a portable copy keeps working when moved.</summary>
    static string PortablePath(string file)
    {
        var root = AppPaths.ExeDir + Path.DirectorySeparatorChar;
        return file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? @".\" + file[root.Length..] : file;
    }

    static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Quotes a folder for sh while still expanding a leading ~ to $HOME.</summary>
    static string ShPath(string d)
    {
        if (d == "~") return "\"$HOME\"";
        if (d.StartsWith("~/", StringComparison.Ordinal)) return "\"$HOME\"" + ShQuote(d[1..]);
        return ShQuote(d);
    }
}
