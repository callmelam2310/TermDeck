using System.Collections.Generic;

namespace TermDeck.Core.Ai;

/// <summary>The MCP tool catalog TermDeck exposes to its AI agent. One entry per method the control layer handles.
/// <see cref="Mutating"/> tools run or change things and are gated behind a UI confirmation.</summary>
public sealed record AiTool(string Name, string Description, string InputSchema, bool Mutating);

public static class AiTools
{
    const string Obj = "\"type\":\"object\"";

    public static readonly IReadOnlyList<AiTool> All = new[]
    {
        new AiTool("td_project",
            "Get the current TermDeck project: its folder and whether runs are stored there. Call this first to know where you are.",
            $"{{{Obj},\"properties\":{{}}}}", false),

        new AiTool("td_list_tools",
            "List the tools configured in TermDeck (name, id, kind Windows/WSL, binary path, collection). These are the binaries the user can run.",
            $"{{{Obj},\"properties\":{{}}}}", false),

        new AiTool("td_list_collections",
            "List the tool collections (named groups of tools).",
            $"{{{Obj},\"properties\":{{}}}}", false),

        new AiTool("td_get_tool",
            "Get one tool's full definition by id or by name.",
            $"{{{Obj},\"properties\":{{\"tool\":{{\"type\":\"string\",\"description\":\"tool id or name\"}}}},\"required\":[\"tool\"]}}", false),

        new AiTool("td_list_runs",
            "List recent runs (command history), newest first, optionally for one tool. Each run has id, tool, args, exit code and timing.",
            $"{{{Obj},\"properties\":{{\"tool\":{{\"type\":\"string\",\"description\":\"optional tool id or name\"}},\"limit\":{{\"type\":\"integer\"}}}}}}", false),

        new AiTool("td_get_run",
            "Get one run's status by id (tool, args, cwd, exit code, running/finished).",
            $"{{{Obj},\"properties\":{{\"run_id\":{{\"type\":\"integer\"}}}},\"required\":[\"run_id\"]}}", false),

        new AiTool("td_get_run_output",
            "Get the terminal output (plain text, ANSI stripped) of a run by id. Use max_chars to cap large output (keeps the start and end).",
            $"{{{Obj},\"properties\":{{\"run_id\":{{\"type\":\"integer\"}},\"max_chars\":{{\"type\":\"integer\"}}}},\"required\":[\"run_id\"]}}", false),

        new AiTool("td_search_history",
            "Full-text search across every run's output, arguments and command line in this project. Returns matching runs with a snippet.",
            $"{{{Obj},\"properties\":{{\"query\":{{\"type\":\"string\"}},\"tool\":{{\"type\":\"string\"}},\"limit\":{{\"type\":\"integer\"}}}},\"required\":[\"query\"]}}", false),

        new AiTool("td_filter_text",
            "Dry-run TermDeck's output-filter language on text WITHOUT running anything. One step per line: grep [-v -i -F -o] RE, awk '{print $1}', sed s/a/b/g, trim/lower/upper/uniq/sort/head N/tail N, wsl CMD, ps CMD. Returns the resulting values. Use this to design an autorun filter.",
            $"{{{Obj},\"properties\":{{\"text\":{{\"type\":\"string\"}},\"script\":{{\"type\":\"string\"}}}},\"required\":[\"text\",\"script\"]}}", false),

        new AiTool("td_list_autorun_rules",
            "List the autorun rules (when a tool finishes, filter its output and run the next tool).",
            $"{{{Obj},\"properties\":{{}}}}", false),

        // ── mutating ──
        new AiTool("td_run_tool",
            "Run a tool in TermDeck: opens its tab and executes it with the given arguments. If wait=true, blocks until it finishes (up to timeout_sec) and returns the exit code and output. The run appears in the GUI like any other.",
            $"{{{Obj},\"properties\":{{\"tool\":{{\"type\":\"string\",\"description\":\"tool id or name\"}},\"args\":{{\"type\":\"string\"}},\"wait\":{{\"type\":\"boolean\"}},\"timeout_sec\":{{\"type\":\"integer\"}},\"max_chars\":{{\"type\":\"integer\"}}}},\"required\":[\"tool\"]}}", true),

        new AiTool("td_stop_run",
            "Stop a running run by id (Ctrl+C, then kill).",
            $"{{{Obj},\"properties\":{{\"run_id\":{{\"type\":\"integer\"}}}},\"required\":[\"run_id\"]}}", true),

        new AiTool("td_create_tool",
            "Create a new tool. kind is 'windows' (path to .exe) or 'wsl' (linux binary name/path). distro and run_as are optional (WSL).",
            $"{{{Obj},\"properties\":{{\"name\":{{\"type\":\"string\"}},\"kind\":{{\"type\":\"string\",\"enum\":[\"windows\",\"wsl\"]}},\"path\":{{\"type\":\"string\"}},\"distro\":{{\"type\":\"string\"}},\"default_args\":{{\"type\":\"string\"}},\"collection\":{{\"type\":\"string\",\"description\":\"collection id or name (created if new)\"}}}},\"required\":[\"name\",\"kind\",\"path\"]}}", true),

        new AiTool("td_update_tool",
            "Update fields of an existing tool by id. Only the fields you pass are changed.",
            $"{{{Obj},\"properties\":{{\"id\":{{\"type\":\"string\"}},\"name\":{{\"type\":\"string\"}},\"kind\":{{\"type\":\"string\",\"enum\":[\"windows\",\"wsl\"]}},\"path\":{{\"type\":\"string\"}},\"distro\":{{\"type\":\"string\"}},\"default_args\":{{\"type\":\"string\"}}}},\"required\":[\"id\"]}}", true),

        new AiTool("td_create_autorun_rule",
            "Create an autorun rule: when the source tool finishes, read its output (or a file it wrote), filter it, and run the target tool with args_template. Placeholders: {{line}} (one run per value), {{file}} (list file), {{lines}}, {{csv}}, {{first}}, {{cwd}}. source is 'output' or 'file'. Use td_filter_text first to validate the filter.",
            $"{{{Obj},\"properties\":{{\"name\":{{\"type\":\"string\"}},\"from\":{{\"type\":\"string\",\"description\":\"source tool id or name\"}},\"to\":{{\"type\":\"string\",\"description\":\"target tool id or name\"}},\"filter\":{{\"type\":\"string\"}},\"args_template\":{{\"type\":\"string\"}},\"source\":{{\"type\":\"string\",\"enum\":[\"output\",\"file\"]}},\"source_file\":{{\"type\":\"string\"}},\"trigger\":{{\"type\":\"string\",\"enum\":[\"success\",\"always\",\"failure\"]}},\"enabled\":{{\"type\":\"boolean\"}},\"confirm\":{{\"type\":\"boolean\"}},\"max_parallel\":{{\"type\":\"integer\"}},\"max_items\":{{\"type\":\"integer\"}}}},\"required\":[\"name\",\"from\",\"to\",\"args_template\"]}}", true),

        new AiTool("td_run_autorun_rule",
            "Apply an existing autorun rule to a finished run by id, right now (same as right-click → Autorun in the history).",
            $"{{{Obj},\"properties\":{{\"rule_id\":{{\"type\":\"string\"}},\"run_id\":{{\"type\":\"integer\"}}}},\"required\":[\"rule_id\",\"run_id\"]}}", true),

        new AiTool("td_set_autorun_enabled",
            "Turn the global autorun master switch on or off.",
            $"{{{Obj},\"properties\":{{\"enabled\":{{\"type\":\"boolean\"}}}},\"required\":[\"enabled\"]}}", true),
    };

    public static bool IsMutating(string name)
    {
        foreach (var t in All) if (t.Name == name) return t.Mutating;
        return true; // unknown → treat as mutating (safer)
    }
}
