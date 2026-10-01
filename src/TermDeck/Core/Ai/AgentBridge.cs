using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TermDeck.Core.Ai;

/// <summary>One parsed event from the Claude CLI stream.</summary>
public abstract record AgentEvent;
public sealed record AgentSession(string SessionId) : AgentEvent;
public sealed record AgentText(string Text) : AgentEvent;
public sealed record AgentToolUse(string Name, string Input) : AgentEvent;
public sealed record AgentToolResult(string Text, bool IsError) : AgentEvent;
public sealed record AgentDone(string? Result, bool Ok, string? Error) : AgentEvent;
public sealed record AgentLog(string Line) : AgentEvent;

/// <summary>
/// Bridge to the Claude Code CLI, in the spirit of Pentest-Assistant's ai-bridge: it spawns <c>claude --print
/// --output-format stream-json</c> wired to TermDeck's own MCP server (an isolated <c>--mcp-config</c> that runs
/// <c>TermDeck.exe --mcp</c>, forwarding to the loopback control server), parses the stream, and raises
/// <see cref="AgentEvent"/>s. Provider/model env (ANTHROPIC_*) is injected only into the spawned process.
/// </summary>
public sealed class AgentBridge
{
    public const string SystemPrompt =
        "You are the AI agent built into TermDeck, a per-project runner for Windows and WSL command-line tools that " +
        "keeps every command and its output. Help the user run and manage their tools and work with the results, " +
        "whatever their task is. You control TermDeck through the `td_*` MCP tools — use them instead of guessing:\n" +
        "- Start with `td_project` and `td_list_tools` to see the project and the available tools.\n" +
        "- Run a tool with `td_run_tool` (set wait=true for short commands to get the output back; for long-running " +
        "ones run without wait and poll `td_get_run_output`). Runs appear in the GUI like any other.\n" +
        "- Read past work with `td_list_runs`, `td_get_run_output` and `td_search_history`.\n" +
        "- To chain tools automatically, design the filter with `td_filter_text` first, then `td_create_autorun_rule`.\n" +
        "Running tools and editing config ask the user for confirmation in the UI unless they enabled auto-approve; if " +
        "an action is declined, respect it. Do only what the user asks. Be concise, show the exact commands you run, " +
        "and never claim a result you did not get from a tool.";

    static string? _claudePath;

    readonly string _exePath;
    readonly string _controlUrl;
    readonly string _controlToken;

    public AgentBridge(string controlUrl, string controlToken)
    {
        _exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        _controlUrl = controlUrl;
        _controlToken = controlToken;
    }

    /// <summary>Path of the Claude CLI, or null if not found. Cached.</summary>
    public static string? FindClaude()
    {
        if (_claudePath != null) return _claudePath.Length == 0 ? null : _claudePath;
        foreach (var name in new[] { "claude.cmd", "claude.exe", "claude" })
        {
            var p = Which(name);
            if (p != null) { _claudePath = p; return p; }
        }
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var cand in new[]
                 {
                     Path.Combine(user, @"AppData\Local\AnthropicClaude\claude.exe"),
                     Path.Combine(user, @"AppData\Roaming\npm\claude.cmd"),
                     Path.Combine(user, @".local\bin\claude.exe"),
                     Path.Combine(user, @".local\bin\claude"),
                 })
            if (File.Exists(cand)) { _claudePath = cand; return cand; }
        _claudePath = "";
        return null;
    }

    static string? Which(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", name)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            foreach (var line in o.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0 && File.Exists(t)) return t;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Writes the isolated MCP config that points Claude at TermDeck's own MCP server.</summary>
    string WriteMcpConfig()
    {
        var cfg = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["termdeck"] = new
                {
                    command = _exePath,
                    args = new[] { "--mcp" },
                    env = new Dictionary<string, string>
                    {
                        ["TD_CONTROL_URL"] = _controlUrl,
                        ["TD_CONTROL_TOKEN"] = _controlToken,
                    },
                },
            },
        };
        var dir = Path.Combine(AppPaths.DataDir, "agent");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "mcp.json");
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>
    /// Runs one turn. <paramref name="sessionId"/> null = new conversation; otherwise resumes it. Events are raised
    /// on a background thread (the caller marshals to the UI). Returns when the turn ends.
    /// </summary>
    public async Task RunTurnAsync(string prompt, string? model, string? sessionId, IReadOnlyDictionary<string, string>? providerEnv,
        string? projectDir, string? scratchDir, Action<AgentEvent> sink, CancellationToken ct)
    {
        var claude = FindClaude();
        if (claude == null)
        {
            sink(new AgentDone(null, false, "Claude CLI not found. Install it and sign in (claude /login), or set a provider in Settings."));
            return;
        }

        var mcpConfig = WriteMcpConfig();
        var args = new List<string>
        {
            "--print",
            "--output-format", "stream-json",
            "--verbose",
            "--mcp-config", mcpConfig,
            "--strict-mcp-config",
            "--dangerously-skip-permissions",
            "--append-system-prompt", SystemPrompt + FileNote(projectDir, scratchDir),
        };
        if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model.Trim()); }
        if (!string.IsNullOrWhiteSpace(sessionId)) { args.Add("--resume"); args.Add(sessionId); }

        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // A .cmd/.bat shim (npm global) must be launched through cmd.exe.
        if (claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || claude.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(claude);
        }
        else psi.FileName = claude;
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Contain runtime junk: run in a per-project scratch folder and point TEMP/TMP there too, so any file the
        // agent or a tool writes (scripts, screenshots, downloads, temp files) lands with the project, not scattered
        // across the disk. Falls back to the project folder, then the process default.
        var cwd = FirstDir(scratchDir, projectDir);
        if (cwd != null)
        {
            psi.WorkingDirectory = cwd;
            psi.Environment["TEMP"] = cwd;
            psi.Environment["TMP"] = cwd;
            psi.Environment["TMPDIR"] = cwd;
        }

        // Provider overrides go ONLY into this child (never the machine env).
        if (providerEnv != null)
            foreach (var (k, v) in providerEnv)
                if (!string.IsNullOrWhiteSpace(k)) psi.Environment[k] = v;

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex) { sink(new AgentDone(null, false, "Could not start Claude: " + ex.Message)); return; }

        using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });

        var stderr = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            string? l;
            while ((l = await proc.StandardError.ReadLineAsync()) != null)
                if (l.Trim().Length > 0) stderr.AppendLine(l.Trim());
        });

        try
        {
            await proc.StandardInput.WriteAsync(prompt);
            proc.StandardInput.Close();
        }
        catch { }

        var sawResult = false;
        string? l2;
        while ((l2 = await proc.StandardOutput.ReadLineAsync()) != null)
        {
            l2 = l2.Trim();
            if (l2.Length == 0 || l2[0] != '{') continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(l2); } catch { continue; }
            using (doc)
            {
                foreach (var ev in Parse(doc.RootElement))
                {
                    if (ev is AgentDone) sawResult = true;
                    sink(ev);
                }
            }
        }

        await errTask;
        try { proc.WaitForExit(3000); } catch { }
        if (!sawResult)
        {
            var err = stderr.ToString().Trim();
            sink(new AgentDone(null, false, err.Length > 0 ? err : (ct.IsCancellationRequested ? "Stopped." : "Claude exited without a result.")));
        }
    }

    static IEnumerable<AgentEvent> Parse(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "system":
                if (root.TryGetProperty("session_id", out var sid) && sid.GetString() is { Length: > 0 } s)
                    yield return new AgentSession(s);
                break;

            case "assistant":
                if (root.TryGetProperty("message", out var am) && am.TryGetProperty("content", out var ac) && ac.ValueKind == JsonValueKind.Array)
                    foreach (var block in ac.EnumerateArray())
                    {
                        var bt = block.TryGetProperty("type", out var bte) ? bte.GetString() : null;
                        if (bt == "text" && block.TryGetProperty("text", out var txt) && txt.GetString() is { Length: > 0 } body)
                            yield return new AgentText(body);
                        else if (bt == "tool_use")
                        {
                            var name = block.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "";
                            var input = block.TryGetProperty("input", out var ie) ? ie.GetRawText() : "";
                            yield return new AgentToolUse(StripMcpPrefix(name), input);
                        }
                    }
                break;

            case "user":
                if (root.TryGetProperty("message", out var um) && um.TryGetProperty("content", out var uc) && uc.ValueKind == JsonValueKind.Array)
                    foreach (var block in uc.EnumerateArray())
                        if (block.TryGetProperty("type", out var bte2) && bte2.GetString() == "tool_result")
                        {
                            var isErr = block.TryGetProperty("is_error", out var ee) && ee.ValueKind == JsonValueKind.True;
                            yield return new AgentToolResult(ToolResultText(block), isErr);
                        }
                break;

            case "result":
                var ok = (root.TryGetProperty("subtype", out var st) ? st.GetString() : null) == "success"
                         && !(root.TryGetProperty("is_error", out var re) && re.ValueKind == JsonValueKind.True);
                var text = root.TryGetProperty("result", out var rr) ? rr.GetString() : null;
                yield return new AgentDone(text, ok, ok ? null : (text ?? "error"));
                break;
        }
    }

    static string ToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in c.EnumerateArray())
                if (part.TryGetProperty("text", out var txt)) sb.Append(txt.GetString());
            return sb.ToString();
        }
        return "";
    }

    /// <summary>Creates the first existing/creatable directory from the candidates and returns it, or null.</summary>
    static string? FirstDir(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try { Directory.CreateDirectory(c); return c; } catch { }
        }
        return null;
    }

    /// <summary>System-prompt addendum telling the agent to keep all file output inside the project scratch folder.</summary>
    static string FileNote(string? projectDir, string? scratchDir)
    {
        var scratch = scratchDir ?? projectDir;
        if (string.IsNullOrWhiteSpace(scratch)) return "";
        var sb = new StringBuilder("\n\nFILE OUTPUT (STRICT): your working directory is a scratch folder for this project:\n  ");
        sb.Append(scratch).Append('\n');
        sb.Append("Write ANY file you create — scripts you run, screenshots, downloads, scratch notes, temp files — inside this "
            + "folder (use relative paths, or this folder explicitly). Never write elsewhere on disk, never to the system temp, "
            + "and do not litter the project with throwaway files. TEMP/TMP already point here. ");
        if (!string.IsNullOrWhiteSpace(projectDir))
            sb.Append("The project folder itself is ").Append(projectDir).Append(" — run tools against it with the td_* tools rather than cd-ing around. ");
        sb.Append("You usually do not need files at all; prefer answering in text.");
        return sb.ToString();
    }

    static string StripMcpPrefix(string name)
    {
        // The CLI exposes MCP tools as "mcp__termdeck__td_run_tool"; show the bare name.
        var i = name.LastIndexOf("__", StringComparison.Ordinal);
        return i >= 0 ? name[(i + 2)..] : name;
    }
}
