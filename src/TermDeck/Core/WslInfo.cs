using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace TermDeck.Core;

public static class WslInfo
{
    static string? _defaultDistro;
    static bool _defaultResolved;

    public static List<string> ListDistros()
    {
        var output = Run("-l -q");
        return output == null
            ? new List<string>()
            : output.Split('\n').Select(l => l.Trim().Trim('\0')).Where(l => l.Length > 0).ToList();
    }

    /// <summary>Default distro name (asked from inside WSL, so it does not depend on the language of wsl -l).</summary>
    public static string? DefaultDistro()
    {
        if (_defaultResolved) return _defaultDistro;
        _defaultResolved = true;
        var name = Run("-e sh -c \"echo $WSL_DISTRO_NAME\"")?.Trim();
        _defaultDistro = string.IsNullOrEmpty(name) ? null : name;
        return _defaultDistro;
    }

    static string? Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            // Force wsl.exe to print its own messages as UTF-8 instead of UTF-16.
            psi.Environment["WSL_UTF8"] = "1";
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
