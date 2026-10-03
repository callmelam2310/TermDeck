using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TermDeck.Core;

/// <summary>
/// An interactive shell that can be opened in a free shell tab.
/// Key: "wsl" (default distro), "wsl:&lt;distro&gt;", "powershell", "pwsh" or "cmd".
/// </summary>
public sealed record ShellDef(string Key, string Title, ToolKind Kind, string Distro, string Exe)
{
    /// <summary>History rows of shell sessions use this tool id, so search and reports can group them.</summary>
    public string ToolId => "shell:" + Key;

    /// <param name="proxy">Proxy exported into the shell's environment; null = direct.</param>
    public LaunchSpec Build(string cwd, ProxyProfile? proxy = null)
    {
        if (Kind == ToolKind.Wsl)
        {
            var linuxCwd = PathMapper.ToLinux(cwd);
            var cl = "wsl.exe" + (Distro.Length > 0 ? " -d " + CommandBuilder.QuoteWin(Distro) : "")
                     + " --cd " + CommandBuilder.QuoteWin(linuxCwd);
            if (proxy == null) return new LaunchSpec(cl, null, cl, linuxCwd);
            // Export the proxy, then replace sh with the user's login shell.
            var pl = new ProxyLaunch(proxy, Proxy.NewToken(), true, Distro);
            var script = Proxy.WslPrefix(proxy, pl.Token)
                         + "s=$(getent passwd \"$(id -un)\" | cut -d: -f7); exec \"${s:-bash}\" -l";
            return new LaunchSpec(cl + " -e sh -c " + CommandBuilder.QuoteWin(script), null, cl, linuxCwd, Proxy: pl);
        }
        var exe = CommandBuilder.QuoteWin(Exe) + (Key == "cmd" ? "" : " -NoLogo");
        if (proxy == null) return new LaunchSpec(exe, cwd, exe, cwd);
        return new LaunchSpec(exe, cwd, exe, cwd, null, Proxy.WindowsEnv(proxy), new ProxyLaunch(proxy, Proxy.NewToken(), false, ""));
    }

    /// <summary>A stand-in tool so the Files panel maps and quotes paths for this shell.</summary>
    public ToolDef AsTool() => new() { Id = ToolId, Name = Title, Kind = Kind, Distro = Distro, Path = Exe };
}

public static class ShellCatalog
{
    /// <summary>WSL distros first (default distro on top), then the Windows shells that exist on this machine.</summary>
    public static List<ShellDef> List()
    {
        var list = new List<ShellDef>();
        // docker-desktop(-data) are Docker's internal distros, not shells.
        var distros = WslInfo.ListDistros().Where(d => !d.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)).ToList();
        var def = distros.Count > 0 ? WslInfo.DefaultDistro() : null;
        foreach (var d in distros.OrderBy(d => string.Equals(d, def, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            var isDefault = string.Equals(d, def, StringComparison.OrdinalIgnoreCase);
            list.Add(new ShellDef("wsl:" + d, isDefault ? $"WSL · {d} (default)" : $"WSL · {d}", ToolKind.Wsl, d, "wsl.exe"));
        }

        list.Add(new ShellDef("powershell", "Windows PowerShell", ToolKind.Windows, "", "powershell.exe"));
        if (FindPwsh() is { } pwsh) list.Add(new ShellDef("pwsh", "PowerShell 7", ToolKind.Windows, "", pwsh));
        list.Add(new ShellDef("cmd", "Command Prompt", ToolKind.Windows, "", "cmd.exe"));
        return list;
    }

    static string? FindPwsh()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "pwsh.exe");
                if (dir.Length > 0 && File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(pf) ? pf : null;
    }
}
