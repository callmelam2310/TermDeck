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

    static string? Run(string args) => Run(new ProcessStartInfo("wsl.exe", args), 10000);

    /// <summary>Runs a POSIX sh script in a distro (null = default) as its login user; returns stdout, or null on failure.</summary>
    public static string? RunScript(string? distro, string script, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo("wsl.exe");
        if (!string.IsNullOrWhiteSpace(distro))
        {
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro.Trim());
        }
        foreach (var a in new[] { "-e", "sh", "-c", script }) psi.ArgumentList.Add(a);
        return Run(psi, timeoutMs);
    }

    static string? Run(ProcessStartInfo psi, int timeoutMs)
    {
        try
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            // Force wsl.exe to print its own messages as UTF-8 instead of UTF-16.
            psi.Environment["WSL_UTF8"] = "1";
            using var p = Process.Start(psi)!;
            p.BeginErrorReadLine();
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
