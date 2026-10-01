using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace TermDeck.Core;

public enum ReportFormat { Html, Markdown }

public sealed class ReportOptions
{
    public ReportFormat Format { get; set; } = ReportFormat.Html;
    public string Title { get; set; } = "TermDeck report";
    public bool IncludeOutput { get; set; } = true;
    /// <summary>Output lines per run; 0 = everything. Long output keeps its head and tail.</summary>
    public int MaxLines { get; set; } = 200;
}

/// <summary>
/// Builds a self-contained HTML or Markdown report of runs (command, time, duration, exit code and output)
/// — e.g. as evidence for a pentest report.
/// </summary>
public static class ReportBuilder
{
    public static string Build(IReadOnlyList<RunRecord> runs, HistoryStore store, ReportOptions o, Action<int>? progress = null)
    {
        var groups = runs.GroupBy(r => r.ToolId)
            .Select(g => (Name: g.OrderBy(r => r.Id).Last().ToolName, Runs: g.OrderBy(r => r.StartedAt).ToList()))
            .OrderBy(g => g.Runs[0].StartedAt)
            .ToList();
        var done = 0;
        string Output(RunRecord r)
        {
            progress?.Invoke(++done);
            if (!o.IncludeOutput) return "";
            return Truncate(AnsiText.FromCast(store.LogPath(r), keepSgr: o.Format == ReportFormat.Html), o.MaxLines);
        }
        return o.Format == ReportFormat.Html
            ? Html(groups, store, o, Output)
            : Markdown(groups, store, o, Output);
    }

    static string Truncate(string text, int maxLines)
    {
        if (maxLines <= 0) return text;
        var lines = text.Split('\n');
        if (lines.Length <= maxLines) return text;
        var tail = Math.Max(1, maxLines / 4);
        var head = maxLines - tail;
        var skipped = lines.Length - head - tail;
        var reset = text.Contains('\x1b') ? "\x1b[0m" : "";
        return string.Join('\n', lines.Take(head))
               + $"\n{reset}… {skipped} more line(s) — full output in the .cast log …\n"
               + string.Join('\n', lines.Skip(lines.Length - tail));
    }

    static string Status(RunRecord r) =>
        r.EndedAt == null ? "interrupted" : r.ExitCode == 0 ? "ok" : "failed";

    static string Duration(RunRecord r) =>
        r.EndedAt is { } end ? RunSession.FormatDuration(end - r.StartedAt) : "—";

    static string Exit(RunRecord r) => r.EndedAt == null ? "—" : RunSession.DescribeExit(r.ExitCode);

    static (DateTime From, DateTime To) Span(IEnumerable<RunRecord> runs)
    {
        var list = runs.ToList();
        return (list.Min(r => r.StartedAt), list.Max(r => r.EndedAt ?? r.StartedAt));
    }

    // ───────────────────────── HTML ─────────────────────────

    static string Html(List<(string Name, List<RunRecord> Runs)> groups, HistoryStore store, ReportOptions o, Func<RunRecord, string> output)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var all = groups.SelectMany(g => g.Runs).ToList();
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(o.Title)}}</title>
            <style>
            :root { --bg:#fff; --fg:#1b1b1b; --muted:#6b6b6b; --line:#e2e2e2; --card:#f7f7f7; --ok:#13a10e; --fail:#c50f1f; --warn:#b07800; --accent:#2b6cb0; }
            @media (prefers-color-scheme: dark) { :root { --bg:#1e1e1e; --fg:#eee; --muted:#9a9a9a; --line:#3a3a3a; --card:#262626; --accent:#4c9be8; } }
            * { box-sizing: border-box; }
            body { margin: 0; background: var(--bg); color: var(--fg); font: 14px/1.5 "Segoe UI", system-ui, sans-serif; }
            main { max-width: 1200px; margin: 0 auto; padding: 32px 20px 60px; }
            h1 { font-size: 26px; margin: 0 0 4px; }
            h2 { font-size: 19px; margin: 36px 0 12px; padding-bottom: 6px; border-bottom: 1px solid var(--line); }
            .meta { color: var(--muted); }
            table { border-collapse: collapse; width: 100%; margin: 12px 0; }
            th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--line); vertical-align: top; }
            th { font-weight: 600; color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: .03em; }
            td.n { text-align: right; font-variant-numeric: tabular-nums; }
            a { color: var(--accent); text-decoration: none; }
            a:hover { text-decoration: underline; }
            .run { background: var(--card); border: 1px solid var(--line); border-radius: 6px; margin: 14px 0; overflow: hidden; }
            .run header { padding: 10px 14px; }
            .cmd { font: 13px Cascadia Mono, Consolas, monospace; word-break: break-all; }
            .cmd::before { content: "$ "; color: var(--muted); }
            .badge { display: inline-block; min-width: 74px; text-align: center; border-radius: 3px; color: #fff; font-size: 11px; font-weight: 600; padding: 1px 6px; margin-right: 8px; text-transform: uppercase; }
            .ok { background: var(--ok); } .failed { background: var(--fail); } .interrupted { background: var(--warn); }
            .info { color: var(--muted); font-size: 12px; margin-top: 3px; }
            pre { margin: 0; padding: 12px 14px; background: #0c0c0c; color: #cccccc; font: 12.5px/1.35 Cascadia Mono, Consolas, monospace;
                  white-space: pre-wrap; word-break: break-all; max-height: 640px; overflow: auto; }
            @media print { pre { max-height: none; } .run { break-inside: avoid-page; } }
            </style>
            </head>
            <body>
            <main>
            <h1>{{E(o.Title)}}</h1>

            """);
        var (from, to) = all.Count > 0 ? Span(all) : (DateTime.Now, DateTime.Now);
        sb.Append($"<div class=\"meta\">Project: <code>{E(store.ProjectDir)}</code><br>")
          .Append($"Runs: {all.Count} · Tools: {groups.Count} · Period: {from:yyyy-MM-dd HH:mm} → {to:yyyy-MM-dd HH:mm}<br>")
          .Append($"Generated by TermDeck on {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>\n");

        sb.Append("<h2>Summary</h2>\n<table><tr><th>Tool</th><th>Runs</th><th>OK</th><th>Failed</th><th>First run</th><th>Last run</th></tr>\n");
        for (var i = 0; i < groups.Count; i++)
        {
            var (name, runs) = groups[i];
            sb.Append($"<tr><td><a href=\"#t{i}\">{E(name)}</a></td><td class=\"n\">{runs.Count}</td>")
              .Append($"<td class=\"n\">{runs.Count(r => Status(r) == "ok")}</td><td class=\"n\">{runs.Count(r => Status(r) != "ok")}</td>")
              .Append($"<td>{runs[0].StartedAt:yyyy-MM-dd HH:mm}</td><td>{runs[^1].StartedAt:yyyy-MM-dd HH:mm}</td></tr>\n");
        }
        sb.Append("</table>\n");

        for (var i = 0; i < groups.Count; i++)
        {
            var (name, runs) = groups[i];
            sb.Append($"<h2 id=\"t{i}\">{E(name)}</h2>\n");
            foreach (var r in runs)
            {
                var status = Status(r);
                sb.Append($"<section class=\"run\" id=\"run{r.Id}\"><header><span class=\"badge {status}\">{status}</span>")
                  .Append($"<span class=\"cmd\">{E(r.CommandLine)}</span>")
                  .Append($"<div class=\"info\">#{r.Id} · {r.StartedAt:yyyy-MM-dd HH:mm:ss} · duration {Duration(r)} · exit {E(Exit(r))} · cwd <code>{E(r.Cwd)}</code></div></header>\n");
                var text = output(r);
                if (text.Length > 0) sb.Append("<pre>").Append(AnsiText.ToHtml(text)).Append("</pre>\n");
                sb.Append("</section>\n");
            }
        }
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    // ───────────────────────── Markdown ─────────────────────────

    static string Markdown(List<(string Name, List<RunRecord> Runs)> groups, HistoryStore store, ReportOptions o, Func<RunRecord, string> output)
    {
        static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");
        var all = groups.SelectMany(g => g.Runs).ToList();
        var sb = new StringBuilder();
        sb.Append($"# {o.Title}\n\n");
        var (from, to) = all.Count > 0 ? Span(all) : (DateTime.Now, DateTime.Now);
        sb.Append($"- **Project:** `{store.ProjectDir}`\n")
          .Append($"- **Runs:** {all.Count} · **Tools:** {groups.Count}\n")
          .Append($"- **Period:** {from:yyyy-MM-dd HH:mm} → {to:yyyy-MM-dd HH:mm}\n")
          .Append($"- **Generated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss} (TermDeck)\n\n");

        sb.Append("## Summary\n\n| Tool | Runs | OK | Failed | First run | Last run |\n|---|---:|---:|---:|---|---|\n");
        foreach (var (name, runs) in groups)
            sb.Append($"| {Cell(name)} | {runs.Count} | {runs.Count(r => Status(r) == "ok")} | {runs.Count(r => Status(r) != "ok")} | ")
              .Append($"{runs[0].StartedAt:yyyy-MM-dd HH:mm} | {runs[^1].StartedAt:yyyy-MM-dd HH:mm} |\n");

        foreach (var (name, runs) in groups)
        {
            sb.Append($"\n## {name}\n");
            foreach (var r in runs)
            {
                sb.Append($"\n### #{r.Id} · {r.StartedAt:yyyy-MM-dd HH:mm:ss} · {Status(r)}\n\n")
                  .Append($"```\n$ {r.CommandLine}\n```\n\n")
                  .Append($"Duration {Duration(r)} · exit {Exit(r)} · cwd `{r.Cwd}`\n");
                var text = output(r);
                if (text.Length == 0) continue;
                // A fence longer than any backtick run inside the output.
                var fence = new string('`', Math.Max(3, LongestRun(text, '`') + 1));
                sb.Append($"\n{fence}text\n{text}\n{fence}\n");
            }
        }
        return sb.ToString();
    }

    static int LongestRun(string s, char c)
    {
        int best = 0, cur = 0;
        foreach (var ch in s)
        {
            cur = ch == c ? cur + 1 : 0;
            if (cur > best) best = cur;
        }
        return best;
    }

    public static string DefaultFileName(string projectDir, ReportFormat format) =>
        $"TermDeck-report-{SafeName(Path.GetFileName(projectDir.TrimEnd('\\', '/')))}-{DateTime.Now:yyyyMMdd-HHmm}" +
        (format == ReportFormat.Html ? ".html" : ".md");

    public static string SafeName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "project" : clean;
    }
}
