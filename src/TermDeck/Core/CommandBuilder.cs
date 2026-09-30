using System.Text;

namespace TermDeck.Core;

/// <param name="WinRunAsUser">
/// Non-null only for a Windows tool that must run as another account: the process is created with
/// CreateProcessWithLogonW (a password is required). WSL run-as users are baked into <see cref="CommandLine"/> via <c>wsl -u</c>.
/// </param>
public sealed record LaunchSpec(string CommandLine, string? WorkingDir, string Display, string DisplayCwd, string? WinRunAsUser = null);

public static class CommandBuilder
{
    /// <param name="args">Arguments as typed by the user, passed through verbatim.</param>
    /// <param name="cwd">Working directory (Windows path).</param>
    /// <param name="runAs">Run-as account override; null = use <see cref="ToolDef.RunAsUser"/>. Empty = current/login user.</param>
    public static LaunchSpec Build(ToolDef tool, string args, string cwd, string? runAs = null)
    {
        args = args.Trim();
        var user = (runAs ?? tool.RunAsUser).Trim();
        if (tool.Kind == ToolKind.Windows)
        {
            var exe = AppPaths.ResolveToolPath(tool.Path.Trim());
            var cl = QuoteWin(exe) + (args.Length > 0 ? " " + args : "");
            var display = (tool.Path.Trim() + " " + args).Trim();
            return new LaunchSpec(cl, cwd, display, cwd, user.Length > 0 ? user : null);
        }
        else
        {
            var linuxCwd = PathMapper.ToLinux(cwd);
            var inner = (tool.Path.Trim() + " " + args).Trim();
            var shell = string.IsNullOrWhiteSpace(tool.WslShell) ? "bash" : tool.WslShell.Trim();
            var sb = new StringBuilder("wsl.exe");
            if (!string.IsNullOrWhiteSpace(tool.Distro)) sb.Append(" -d ").Append(QuoteWin(tool.Distro.Trim()));
            // Run as a specific Linux user (e.g. root); no password needed — WSL runs it directly.
            if (user.Length > 0) sb.Append(" -u ").Append(QuoteWin(user));
            sb.Append(" --cd ").Append(QuoteWin(linuxCwd));
            // -i so the shell reads its rc file (PATH for go/bin, pipx...), -l for the profile, -c runs one command and exits.
            sb.Append(" -e ").Append(QuoteWin(shell)).Append(" -lic ").Append(QuoteWin(inner));
            return new LaunchSpec(sb.ToString(), null, inner, linuxCwd);
        }
    }

    /// <summary>Quotes an argument following CommandLineToArgvW rules.</summary>
    public static string QuoteWin(string s)
    {
        if (s.Length > 0 && s.IndexOfAny([' ', '\t', '"']) < 0) return s;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in s)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
