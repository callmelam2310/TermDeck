using System;
using System.Collections.Generic;

namespace TermDeck.Core;

public enum ToolKind { Windows, Wsl }

public sealed class ToolDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ToolKind Kind { get; set; } = ToolKind.Windows;

    /// <summary>Windows: path to the executable. WSL: Linux command/binary (e.g. nmap, /usr/bin/ffuf).</summary>
    public string Path { get; set; } = "";

    /// <summary>WSL distro; empty = default distro.</summary>
    public string Distro { get; set; } = "";

    /// <summary>
    /// Account the tool runs as.
    /// WSL: Linux user for <c>wsl -u</c> (empty = the distro's login user; e.g. "root").
    /// Windows: account for CreateProcessWithLogonW as <c>DOMAIN\user</c>, <c>user</c> or <c>user@domain</c>
    /// (empty = the current user; the password is asked for at run time and never stored).
    /// </summary>
    public string RunAsUser { get; set; } = "";

    /// <summary>Shell used to run WSL commands (bash/zsh/sh).</summary>
    public string WslShell { get; set; } = "bash";

    public string DefaultArgs { get; set; } = "";

    /// <summary>Collection this tool belongs to. Empty = ungrouped.</summary>
    public string CollectionId { get; set; } = "";

    public ToolDef Clone() => (ToolDef)MemberwiseClone();

    public override string ToString() => Name;

    public string KindLabel => Kind == ToolKind.Wsl
        ? (string.IsNullOrEmpty(Distro) ? "WSL" : $"WSL · {Distro}")
        : "Windows";

    /// <summary>Short label for the run-as account, or empty when running as the current/login user.</summary>
    public static string RunAsLabel(ToolKind kind, string? user)
    {
        user = user?.Trim() ?? "";
        if (user.Length == 0) return "";
        return user;
    }
}

/// <summary>A named group of tools (like session folders in MobaXterm).</summary>
public sealed class ToolCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool IsExpanded { get; set; } = true;
}

public enum AppTheme { System, Light, Dark }

public sealed class AppConfig
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public List<ToolCollection> Collections { get; set; } = new();
    public List<ToolDef> Tools { get; set; } = new();
    public List<string> RecentProjects { get; set; } = new();
    public string? LastProject { get; set; }
    public bool FollowTerminalFolder { get; set; } = true;
    public bool UngroupedExpanded { get; set; } = true;
    public string TerminalFont { get; set; } = "Cascadia Mono, Consolas, monospace";
    public int TerminalFontSize { get; set; } = 14;
    public double SidebarWidth { get; set; } = 270;
    public double HistoryWidth { get; set; } = 300;
    /// <summary>Key of the last shell opened (<see cref="ShellDef.Key"/>); Ctrl+T opens it again.</summary>
    public string LastShell { get; set; } = "";
    /// <summary>Master switch: when off, no rule fires on its own (rules can still be run by hand from a run's menu).</summary>
    public bool AutorunEnabled { get; set; } = true;
    public List<AutoRule> AutoRules { get; set; } = new();
    /// <summary>Model passed to the Claude CLI for the AI agent (empty = the CLI default).</summary>
    public string AgentModel { get; set; } = "";
    /// <summary>Remembered state of the agent window's "auto-approve actions" toggle.</summary>
    public bool AgentAutoApprove { get; set; }
}

public sealed class RunRecord
{
    public long Id { get; set; }
    public string ToolId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string Args { get; set; } = "";
    /// <summary>Command as displayed (in the tool's path format).</summary>
    public string CommandLine { get; set; } = "";
    /// <summary>Working directory (in the tool's path format).</summary>
    public string Cwd { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>Path of the .cast file, relative to the history folder.</summary>
    public string LogFile { get; set; } = "";
}
