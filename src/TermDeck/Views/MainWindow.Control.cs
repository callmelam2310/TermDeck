using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TermDeck.Core;
using TermDeck.Core.Ai;

namespace TermDeck.Views;

/// <summary>
/// MainWindow as the AI agent's control surface (<see cref="IAppControl"/>). The MCP child process calls these
/// over the loopback <see cref="ControlServer"/>. Read calls return data; mutating calls (run/edit/autorun) are
/// gated behind a confirmation unless the agent panel's auto-approve is on. Everything that touches UI state runs
/// on the dispatcher.
/// </summary>
public partial class MainWindow : IAppControl
{
    ControlServer? _controlServer;

    /// <summary>When true, the agent's run/edit actions are not confirmed one by one (set from the agent panel).</summary>
    public bool AgentAutoApprove { get; set; }

    public ControlServer EnsureControlServer() => _controlServer ??= new ControlServer(this);

    /// <summary>History-store root of the open project (where agent chats are saved too), or null if none.</summary>
    public string? CurrentProjectRoot => _store?.Root;

    /// <summary>The open project folder, or null.</summary>
    public string? CurrentProjectDir => string.IsNullOrEmpty(_projectDir) ? null : _projectDir;

    /// <summary>Opens (or focuses) the AI agent tab for this project.</summary>
    void Agent_Click(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        var existing = _tabs.FirstOrDefault(t => t.View is AgentTab);
        if (existing != null) { TabStrip.SelectedItem = existing; return; }
        AddTab("AI agent", new AgentTab(this, _config));
    }

    /// <summary>Lets open AgentTabs pick up profile / auto-approve changes made in Settings.</summary>
    void RefreshAgentTabs()
    {
        foreach (var t in _tabs.Where(t => t.View is AgentTab)) ((AgentTab)t.View!).RefreshFromSettings();
    }

    /// <summary>Stops the control server on shutdown (agent tabs are disposed by normal tab teardown).</summary>
    void DisposeAgent() => _controlServer?.Dispose();

    async Task<object?> IAppControl.CallAsync(string method, JsonElement a, CancellationToken ct)
    {
        if (AiTools.IsMutating(method))
        {
            var allowed = await Dispatcher.InvokeAsync(() => ConfirmAgentAction(method, a));
            if (!allowed) throw new ControlException("The user declined this action in TermDeck.");
        }
        return await DispatchAsync(method, a, ct);
    }

    bool ConfirmAgentAction(string method, JsonElement a)
    {
        if (AgentAutoApprove) return true;
        var summary = DescribeAction(method, a);
        var r = MessageBox.Show(this, summary + "\n\nAllow the AI agent to do this?", "AI agent — confirm action",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    string DescribeAction(string method, JsonElement a) => method switch
    {
        "td_run_tool" => $"Run the tool \"{Str(a, "tool")}\" with arguments:\n{Str(a, "args")}",
        "td_stop_run" => $"Stop run #{Int(a, "run_id")}.",
        "td_create_tool" => $"Create a new {Str(a, "kind")} tool \"{Str(a, "name")}\" → {Str(a, "path")}",
        "td_update_tool" => $"Change the tool {Str(a, "id")}.",
        "td_create_autorun_rule" => $"Create an autorun rule \"{Str(a, "name")}\": {Str(a, "from")} → {Str(a, "to")}.",
        "td_run_autorun_rule" => $"Apply autorun rule {Str(a, "rule_id")} to run #{Int(a, "run_id")}.",
        "td_set_autorun_enabled" => $"Turn autorun {(Bool(a, "enabled") == true ? "ON" : "OFF")}.",
        _ => $"Perform: {method}",
    };

    async Task<object?> DispatchAsync(string method, JsonElement a, CancellationToken ct)
    {
        var store = _store ?? throw new ControlException("No project is open.");
        switch (method)
        {
            case "td_project":
                return await OnUi(() => (object?)new { dir = _projectDir, portable = AppPaths.IsPortable });

            case "td_list_tools":
                return await OnUi(() => (object?)_tools.Select(ToolDto).ToList());

            case "td_list_collections":
                return await OnUi(() => (object?)_collections.Select(c => new { id = c.Id, name = c.Name }).ToList());

            case "td_get_tool":
                return await OnUi(() => (object?)ToolDto(ResolveTool(Str(a, "tool")) ?? throw new ControlException("No such tool: " + Str(a, "tool"))));

            case "td_list_runs":
            {
                var toolRef = Str(a, "tool");
                var limit = Math.Clamp(Int(a, "limit") ?? 30, 1, 500);
                return await OnUi(() =>
                {
                    List<RunRecord> runs;
                    if (!string.IsNullOrEmpty(toolRef))
                    {
                        var t = ResolveTool(toolRef) ?? throw new ControlException("No such tool: " + toolRef);
                        runs = store.List(t.Id, limit);
                    }
                    else runs = store.ListAll().AsEnumerable().Reverse().Take(limit).ToList();
                    return (object?)runs.Select(r => RunDto(r)).ToList();
                });
            }

            case "td_get_run":
            {
                var id = Int(a, "run_id") ?? throw new ControlException("run_id is required");
                var r = store.Get(id) ?? throw new ControlException("No run #" + id);
                return RunDto(r);
            }

            case "td_get_run_output":
            {
                var id = Int(a, "run_id") ?? throw new ControlException("run_id is required");
                var max = Math.Clamp(Int(a, "max_chars") ?? 40000, 200, 400000);
                var r = store.Get(id) ?? throw new ControlException("No run #" + id);
                var text = await Task.Run(() => ClipMiddle(Autorun.ReadRunOutput(store, r), max), ct);
                return new { run_id = id, r.ExitCode, running = r.EndedAt == null, output = text };
            }

            case "td_search_history":
            {
                var q = Str(a, "query") ?? throw new ControlException("query is required");
                var toolRef = Str(a, "tool");
                var limit = Math.Clamp(Int(a, "limit") ?? 30, 1, 200);
                return await OnUi(() =>
                {
                    string? toolId = string.IsNullOrEmpty(toolRef) ? null : (ResolveTool(toolRef)?.Id ?? toolRef);
                    var hits = store.Search(q, toolId, limit);
                    var snips = store.Snippets(hits.Select(h => h.Id).ToList(), q);
                    return (object?)hits.Select(h => new
                    {
                        run_id = h.Id,
                        tool = h.ToolName,
                        args = h.Args,
                        h.ExitCode,
                        snippet = snips.TryGetValue(h.Id, out var s) ? s.Excerpt.Trim() : "",
                    }).ToList();
                });
            }

            case "td_filter_text":
            {
                var text = Str(a, "text") ?? "";
                var script = Str(a, "script") ?? "";
                var values = await Task.Run(() => FilterScript.Run(text, script, ct), ct);
                return new { count = values.Count, values = values.Take(1000).ToList() };
            }

            case "td_list_autorun_rules":
                return await OnUi(() => (object?)_config.AutoRules.Select(RuleDto).ToList());

            // ───────── mutating ─────────
            case "td_run_tool":
                return await RunToolAsync(a, ct);

            case "td_stop_run":
            {
                var id = Int(a, "run_id") ?? throw new ControlException("run_id is required");
                return await OnUi(() =>
                {
                    var stopped = Views.OfType<ToolTab>().Any(t => t.StopRun(id));
                    return (object?)new { run_id = id, stopped };
                });
            }

            case "td_create_tool":
                return await OnUi(() => CreateTool(a));

            case "td_update_tool":
                return await OnUi(() => UpdateToolCtl(a));

            case "td_create_autorun_rule":
                return await OnUi(() => CreateRule(a));

            case "td_run_autorun_rule":
                return await OnUi(() => RunRuleCtl(a));

            case "td_set_autorun_enabled":
                return await OnUi(() =>
                {
                    SetAutorunEnabled(Bool(a, "enabled") ?? true);
                    return (object?)new { enabled = _config.AutorunEnabled };
                });

            default:
                throw new ControlException("Unknown method: " + method);
        }
    }

    // ───────────────────────── run_tool (with optional wait) ─────────────────────────

    async Task<object?> RunToolAsync(JsonElement a, CancellationToken ct)
    {
        var store = _store ?? throw new ControlException("No project is open.");
        var toolRef = Str(a, "tool") ?? throw new ControlException("tool is required");
        var args = Str(a, "args") ?? "";
        var wait = Bool(a, "wait") ?? false;
        var timeout = Math.Clamp(Int(a, "timeout_sec") ?? 600, 1, 3600);
        var maxChars = Math.Clamp(Int(a, "max_chars") ?? 40000, 200, 400000);

        RunSession? session = null;
        var toolName = "";
        await Dispatcher.InvokeAsync(() =>
        {
            var tool = ResolveTool(toolRef) ?? throw new ControlException("No such tool: " + toolRef);
            toolName = tool.Name;
            var tab = OpenToolTab(tool) ?? throw new ControlException("Could not open the tool tab.");
            session = tab.Launch(args, "AI agent");
        });
        if (session == null) throw new ControlException("The tool did not start (no binary path, or a password prompt was cancelled).");

        var runId = session.Record.Id;
        if (!wait)
            return new { run_id = runId, tool = toolName, started = true, note = "Running; poll td_get_run / td_get_run_output." };

        var tcs = new TaskCompletionSource();
        void Done(RunSession _) => tcs.TrySetResult();
        session.Exited += Done;
        if (!session.IsRunning) tcs.TrySetResult();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
        try { await tcs.Task.WaitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            return new { run_id = runId, tool = toolName, timed_out = true, note = "Still running; poll td_get_run_output." };
        }
        finally { session.Exited -= Done; }

        var rec = session.Record;
        var text = await Task.Run(() => ClipMiddle(Autorun.ReadRunOutput(store, rec), maxChars), ct);
        return new { run_id = runId, tool = toolName, exit_code = rec.ExitCode, finished = true, output = text };
    }

    // ───────────────────────── mutating helpers (UI thread) ─────────────────────────

    object CreateTool(JsonElement a)
    {
        var name = Str(a, "name") ?? throw new ControlException("name is required");
        var kind = (Str(a, "kind") ?? "windows").ToLowerInvariant() == "wsl" ? ToolKind.Wsl : ToolKind.Windows;
        var tool = new ToolDef
        {
            Name = name,
            Kind = kind,
            Path = Str(a, "path") ?? "",
            Distro = Str(a, "distro") ?? "",
            DefaultArgs = Str(a, "default_args") ?? "",
            CollectionId = ResolveOrCreateCollection(Str(a, "collection")),
        };
        _tools.Add(tool);
        RebuildTree();
        SaveConfig();
        return ToolDto(tool);
    }

    object UpdateToolCtl(JsonElement a)
    {
        var id = Str(a, "id") ?? throw new ControlException("id is required");
        var tool = _tools.FirstOrDefault(t => t.Id == id) ?? throw new ControlException("No tool with id " + id);
        if (Str(a, "name") is { } n) tool.Name = n;
        if (Str(a, "kind") is { } k) tool.Kind = k.ToLowerInvariant() == "wsl" ? ToolKind.Wsl : ToolKind.Windows;
        if (Str(a, "path") is { } p) tool.Path = p;
        if (Str(a, "distro") is { } d) tool.Distro = d;
        if (Str(a, "default_args") is { } da) tool.DefaultArgs = da;
        RebuildTree();
        SaveConfig();
        foreach (var doc in _tabs.Where(t => t.View is ToolTab vt && vt.Tool.Id == id))
            ((ToolTab)doc.View!).UpdateTool(tool);
        return ToolDto(tool);
    }

    object CreateRule(JsonElement a)
    {
        var from = ResolveTool(Str(a, "from")) ?? throw new ControlException("No source tool: " + Str(a, "from"));
        var to = ResolveTool(Str(a, "to")) ?? throw new ControlException("No target tool: " + Str(a, "to"));
        var rule = new AutoRule
        {
            Name = Str(a, "name") ?? $"{from.Name} → {to.Name}",
            FromToolId = from.Id,
            ToToolId = to.Id,
            Filter = Str(a, "filter") ?? "",
            ArgsTemplate = Str(a, "args_template") ?? "{{line}}",
            Source = (Str(a, "source") ?? "output").ToLowerInvariant() == "file" ? AutoSource.File : AutoSource.Output,
            SourceFile = Str(a, "source_file") ?? "",
            Trigger = (Str(a, "trigger") ?? "success").ToLowerInvariant() switch
            {
                "always" => AutoTrigger.Always,
                "failure" => AutoTrigger.Failure,
                _ => AutoTrigger.Success,
            },
            Enabled = Bool(a, "enabled") ?? true,
            Confirm = Bool(a, "confirm") ?? false,
            MaxParallel = Math.Clamp(Int(a, "max_parallel") ?? 1, 1, 32),
            MaxItems = Math.Max(0, Int(a, "max_items") ?? 500),
        };
        // Validate the filter so the agent gets a clear error instead of a broken rule.
        try { FilterScript.Parse(rule.Filter); }
        catch (AutorunException ex) { throw new ControlException($"Filter error on line {ex.Line}: {ex.Message}"); }

        _config.AutoRules.Add(rule);
        SaveConfig();
        UpdateAutorunStatus();
        return RuleDto(rule);
    }

    object RunRuleCtl(JsonElement a)
    {
        var store = _store ?? throw new ControlException("No project is open.");
        var ruleId = Str(a, "rule_id") ?? throw new ControlException("rule_id is required");
        var runId = Int(a, "run_id") ?? throw new ControlException("run_id is required");
        var rule = _config.AutoRules.FirstOrDefault(r => r.Id == ruleId) ?? throw new ControlException("No rule " + ruleId);
        var run = store.Get(runId) ?? throw new ControlException("No run #" + runId);
        _ = FireAsync(rule, run, 1, manual: false);
        return new { queued = true, rule = rule.Name, run_id = runId };
    }

    string ResolveOrCreateCollection(string? nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return "";
        var existing = _collections.FirstOrDefault(c => c.Id == nameOrId || c.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing.Id;
        var col = new ToolCollection { Name = nameOrId.Trim() };
        _collections.Add(col);
        return col.Id;
    }

    // ───────────────────────── helpers ─────────────────────────

    Task<object?> OnUi(Func<object?> f) => Dispatcher.InvokeAsync(f).Task;

    ToolDef? ResolveTool(string? r)
    {
        if (string.IsNullOrWhiteSpace(r)) return null;
        return _tools.FirstOrDefault(t => t.Id == r)
            ?? _tools.FirstOrDefault(t => t.Name.Equals(r, StringComparison.OrdinalIgnoreCase));
    }

    object ToolDto(ToolDef t) => new
    {
        id = t.Id,
        name = t.Name,
        kind = t.Kind == ToolKind.Wsl ? "wsl" : "windows",
        path = t.Path,
        distro = t.Distro,
        run_as = t.RunAsUser,
        default_args = t.DefaultArgs,
        collection = _collections.FirstOrDefault(c => c.Id == t.CollectionId)?.Name ?? "",
    };

    object RunDto(RunRecord r, bool? running = null) => new
    {
        run_id = r.Id,
        tool = r.ToolName,
        args = r.Args,
        command = r.CommandLine,
        cwd = r.Cwd,
        started_at = r.StartedAt,
        ended_at = r.EndedAt,
        exit_code = r.ExitCode,
        running = running ?? (r.EndedAt == null),
    };

    object RuleDto(AutoRule r) => new
    {
        id = r.Id,
        name = r.Name,
        enabled = r.Enabled,
        from = _tools.FirstOrDefault(t => t.Id == r.FromToolId)?.Name ?? r.FromToolId,
        to = _tools.FirstOrDefault(t => t.Id == r.ToToolId)?.Name ?? r.ToToolId,
        source = r.Source == AutoSource.File ? "file" : "output",
        r.SourceFile,
        trigger = r.Trigger.ToString().ToLowerInvariant(),
        filter = r.Filter,
        args_template = r.ArgsTemplate,
        r.MaxParallel,
        r.MaxItems,
    };

    static string ClipMiddle(string s, int max)
    {
        if (s.Length <= max) return s;
        var head = max * 2 / 3;
        var tail = max - head;
        return s[..head] + $"\n\n…[{s.Length - max} chars omitted]…\n\n" + s[^tail..];
    }

    static string? Str(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool? Bool(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    static int? Int(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
