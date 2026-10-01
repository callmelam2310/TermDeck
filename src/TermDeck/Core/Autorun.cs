using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TermDeck.Core;

public enum AutoTrigger { Success, Always, Failure }

public enum AutoSource { Output, File }

/// <summary>
/// "When tool A finishes, take its output (or a file it wrote), filter it, and run tool B with the result."
/// Rules chain naturally: B finishing fires the rules whose source is B.
/// </summary>
public sealed class AutoRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string FromToolId { get; set; } = "";
    public AutoTrigger Trigger { get; set; } = AutoTrigger.Success;
    public AutoSource Source { get; set; } = AutoSource.Output;
    /// <summary>File to read when <see cref="Source"/> is File. Placeholders: {{cwd}}, {{project}}, {{arg:-o}}; relative = the run's directory.</summary>
    public string SourceFile { get; set; } = "";
    /// <summary>Filter script, one step per line (see <see cref="FilterScript"/>).</summary>
    public string Filter { get; set; } = "";
    public string ToToolId { get; set; } = "";
    /// <summary>Arguments for the target tool, with placeholders ({{line}}, {{file}}, {{lines}}…).</summary>
    public string ArgsTemplate { get; set; } = "";
    public bool Confirm { get; set; }
    /// <summary>Per-line mode ({{line}}): how many runs of the target tool may execute at once.</summary>
    public int MaxParallel { get; set; } = 1;
    /// <summary>Values beyond this are dropped (protects against launching thousands of runs).</summary>
    public int MaxItems { get; set; } = 500;

    public AutoRule Clone() => (AutoRule)MemberwiseClone();
}

public sealed class AutorunException(string message, int line = 0) : Exception(message)
{
    /// <summary>1-based script line the error belongs to; 0 = not tied to a line.</summary>
    public int Line { get; } = line;
}

public sealed record FilterStep(int Line, string Op, string Args);

/// <summary>
/// The filter language: one step per line, applied top to bottom to the list of lines.
/// <code>
/// grep [-v] [-i] [-F] [-o] PATTERN   keep matching lines (-o: keep only the match, or group 1 if the regex has groups)
/// awk [-F SEP] '/re/ {print $1 ":" $3}'   fields ($N, $NF, $(NF-1), $0), quoted text; one optional /regex/ condition
/// sed s/PAT/REPL/[gi]  |  sed /PAT/d     regex replace (\1 or $1, &amp;) / delete matching lines
/// trim | lower | upper | uniq | sort [-r] [-n] | head N | tail N
/// wsl COMMAND        pipe the lines through bash in WSL (real grep/awk/jq…)
/// ps COMMAND         pipe the lines through PowerShell ($input)
/// # comment
/// </code>
/// Blank lines are dropped and every value is trimmed at the end.
/// </summary>
public static class FilterScript
{
    public static readonly string[] Ops = ["grep", "awk", "sed", "trim", "lower", "upper", "uniq", "sort", "head", "tail", "wsl", "ps"];

    public static readonly Dictionary<string, string> Help = new()
    {
        ["grep"] = "grep [-v] [-i] [-F] [-o] PATTERN — keep lines matching the regex. -v invert, -i ignore case, -F literal text, -o keep only the match (group 1 if the regex has a group)",
        ["awk"] = "awk [-F SEP] '{print $1}' — print fields: $1…, $NF, $(NF-1), $0, \"text\"; optional /regex/ before { }. Fields split on whitespace unless -F",
        ["sed"] = "sed s/PATTERN/REPLACEMENT/[g][i] — regex replace (\\1 or $1 for groups, & for the match). sed /PATTERN/d deletes matching lines",
        ["trim"] = "trim — remove spaces around each line",
        ["lower"] = "lower — lowercase every line",
        ["upper"] = "upper — uppercase every line",
        ["uniq"] = "uniq — drop duplicate lines (anywhere, keeps the first)",
        ["sort"] = "sort [-r] [-n] — sort lines (-r reverse, -n numeric)",
        ["head"] = "head N — keep the first N lines",
        ["tail"] = "tail N — keep the last N lines",
        ["wsl"] = "wsl COMMAND — pipe the lines through bash in the default WSL distro, e.g. wsl grep -oE '[a-z0-9.-]+\\.com' | sort -u",
        ["ps"] = "ps COMMAND — pipe the lines through PowerShell, e.g. ps $input | Where-Object { $_ -like '*200*' }",
    };

    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public static List<FilterStep> Parse(string script)
    {
        var steps = new List<FilterStep>();
        var lines = script.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var sp = line.IndexOfAny([' ', '\t']);
            var op = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
            var args = sp < 0 ? "" : line[(sp + 1)..].Trim();
            if (op == "extract") { op = "grep"; args = "-o " + args; }
            if (op is "dedupe") op = "uniq";
            if (op is "powershell") op = "ps";
            if (!Ops.Contains(op)) throw new AutorunException($"unknown step '{op}'", i + 1);
            steps.Add(new FilterStep(i + 1, op, args));
        }
        return steps;
    }

    public static string ToScript(IEnumerable<(string Op, string Args)> steps) =>
        string.Join("\n", steps.Select(s => s.Args.Length > 0 ? $"{s.Op} {s.Args}" : s.Op));

    /// <summary>Splits text into lines and runs the script over them.</summary>
    public static List<string> Run(string text, string script, CancellationToken ct = default)
    {
        IEnumerable<string> lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var step in Parse(script))
        {
            ct.ThrowIfCancellationRequested();
            try { lines = Apply(step, lines.ToList(), ct); }
            catch (AutorunException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (RegexMatchTimeoutException) { throw new AutorunException("regex took too long (over 2s on a line)", step.Line); }
            catch (Exception ex) { throw new AutorunException(ex.Message, step.Line); }
        }
        return lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    static IEnumerable<string> Apply(FilterStep step, List<string> lines, CancellationToken ct)
    {
        switch (step.Op)
        {
            case "grep": return Grep(step, lines);
            case "awk": return Awk(step, lines);
            case "sed": return Sed(step, lines);
            case "trim": return lines.Select(l => l.Trim());
            case "lower": return lines.Select(l => l.ToLowerInvariant());
            case "upper": return lines.Select(l => l.ToUpperInvariant());
            case "uniq": return lines.Distinct(StringComparer.Ordinal);
            case "sort":
            {
                var flags = step.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var numeric = flags.Any(f => f.Contains('n'));
                var reverse = flags.Any(f => f.Contains('r'));
                IEnumerable<string> sorted = numeric
                    ? lines.OrderBy(l => double.TryParse(LeadingNumber(l), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.MinValue)
                    : lines.OrderBy(l => l, StringComparer.Ordinal);
                return reverse ? sorted.Reverse() : sorted;
            }
            case "head": return lines.Take(Count(step));
            case "tail": return lines.Skip(Math.Max(0, lines.Count - Count(step)));
            case "wsl": return External.Wsl(step.Args, lines, ct);
            case "ps": return External.PowerShell(step.Args, lines, ct);
            default: throw new AutorunException($"unknown step '{step.Op}'", step.Line);
        }
    }

    static int Count(FilterStep step)
    {
        var a = step.Args.TrimStart('-').Trim();
        if (a.StartsWith('n')) a = a[1..].Trim();
        return int.TryParse(a, out var n) && n >= 0 ? n : throw new AutorunException($"{step.Op} needs a number", step.Line);
    }

    static string LeadingNumber(string s)
    {
        var m = Regex.Match(s, @"^\s*-?\d+(\.\d+)?");
        return m.Success ? m.Value : "";
    }

    // ───────────────────────── grep ─────────────────────────

    static IEnumerable<string> Grep(FilterStep step, List<string> lines)
    {
        var (flags, pattern) = SplitFlags(step.Args, "vioFE");
        if (pattern.Length == 0) throw new AutorunException("grep needs a pattern", step.Line);
        var opts = flags.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None;
        var re = new Regex(flags.Contains('F') ? Regex.Escape(pattern) : pattern, opts | RegexOptions.CultureInvariant, RegexTimeout);
        if (flags.Contains('o'))
        {
            var group = re.GetGroupNumbers().Length > 1 ? 1 : 0;
            return lines.SelectMany(l => re.Matches(l).Select(m => m.Groups[group].Value));
        }
        var invert = flags.Contains('v');
        return lines.Where(l => re.IsMatch(l) != invert);
    }

    /// <summary>Leading -x flags (combined like -vi or separate), then the rest of the line with surrounding quotes removed.</summary>
    static (string Flags, string Pattern) SplitFlags(string args, string known)
    {
        var flags = new StringBuilder();
        var rest = args.TrimStart();
        while (rest.StartsWith('-') && rest.Length > 1)
        {
            var sp = rest.IndexOf(' ');
            var token = sp < 0 ? rest : rest[..sp];
            if (token == "--") { rest = sp < 0 ? "" : rest[(sp + 1)..].TrimStart(); break; }
            if (!token[1..].All(known.Contains)) break;
            flags.Append(token[1..]);
            rest = sp < 0 ? "" : rest[(sp + 1)..].TrimStart();
        }
        return (flags.ToString(), Unquote(rest.Trim()));
    }

    public static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[^1] == s[0] ? s[1..^1] : s;

    // ───────────────────────── awk ─────────────────────────

    static IEnumerable<string> Awk(FilterStep step, List<string> lines)
    {
        var rest = step.Args.Trim();
        string? sep = null;
        if (rest.StartsWith("-F"))
        {
            rest = rest[2..];
            if (rest.StartsWith(' ')) rest = rest.TrimStart();
            var token = ReadToken(ref rest);
            sep = Unquote(token);
            if (sep == "\\t") sep = "\t";
        }
        var program = Unquote(rest.Trim());
        Regex? cond = null;
        if (program.StartsWith('/'))
        {
            var end = program.IndexOf('/', 1);
            while (end > 0 && program[end - 1] == '\\') end = program.IndexOf('/', end + 1);
            if (end < 0) throw new AutorunException("awk: unterminated /regex/", step.Line);
            cond = new Regex(program[1..end], RegexOptions.CultureInvariant, RegexTimeout);
            program = program[(end + 1)..].Trim();
        }
        if (program.Length == 0) program = "{print $0}";
        if (!program.StartsWith('{') || !program.EndsWith('}'))
            throw new AutorunException("awk: expected { print ... }", step.Line);
        var body = program[1..^1].Trim();
        if (!body.StartsWith("print")) throw new AutorunException("awk: only 'print' is supported", step.Line);
        var parts = ParsePrint(body[5..], step.Line);

        return lines.Where(l => cond == null || cond.IsMatch(l)).Select(l =>
        {
            var fields = sep == null
                ? l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                : l.Split(sep);
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                switch (p.Kind)
                {
                    case 'c': sb.Append(' '); break;
                    case 's': sb.Append(p.Text); break;
                    case 'f':
                        var idx = p.FromEnd ? fields.Length - p.Index : p.Index;
                        if (p.Index == 0 && !p.FromEnd) sb.Append(l);
                        else if (idx >= 1 && idx <= fields.Length) sb.Append(fields[idx - 1]);
                        break;
                }
            }
            return sb.ToString();
        });
    }

    /// <summary>A print item: field ($N / $NF / $(NF-k)), literal text, or a comma (output separator).</summary>
    readonly record struct PrintPart(char Kind, int Index = 0, bool FromEnd = false, string Text = "");

    static List<PrintPart> ParsePrint(string s, int line)
    {
        var parts = new List<PrintPart>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == ';') { i++; continue; }
            if (c == ',') { parts.Add(new PrintPart('c')); i++; continue; }
            if (c == '"')
            {
                var end = s.IndexOf('"', i + 1);
                if (end < 0) throw new AutorunException("awk: unterminated string", line);
                parts.Add(new PrintPart('s', Text: s[(i + 1)..end].Replace("\\t", "\t")));
                i = end + 1;
                continue;
            }
            if (c == '$')
            {
                var m = Regex.Match(s[(i + 1)..], @"^(\d+|NF|\(\s*NF\s*-\s*(\d+)\s*\))");
                if (!m.Success) throw new AutorunException("awk: expected $N, $NF or $(NF-N)", line);
                if (m.Value == "NF") parts.Add(new PrintPart('f', 0, true));
                else if (m.Groups[2].Success) parts.Add(new PrintPart('f', int.Parse(m.Groups[2].Value), true));
                else parts.Add(new PrintPart('f', int.Parse(m.Value)));
                i += 1 + m.Length;
                continue;
            }
            throw new AutorunException($"awk: unexpected '{c}' in print", line);
        }
        if (parts.Count == 0) parts.Add(new PrintPart('f', 0));
        return parts;
    }

    static string ReadToken(ref string s)
    {
        if (s.Length > 0 && (s[0] == '\'' || s[0] == '"'))
        {
            var end = s.IndexOf(s[0], 1);
            if (end > 0)
            {
                var t = s[..(end + 1)];
                s = s[(end + 1)..].TrimStart();
                return t;
            }
        }
        var sp = s.IndexOf(' ');
        var token = sp < 0 ? s : s[..sp];
        s = sp < 0 ? "" : s[(sp + 1)..].TrimStart();
        return token;
    }

    // ───────────────────────── sed ─────────────────────────

    static IEnumerable<string> Sed(FilterStep step, List<string> lines)
    {
        var script = Unquote(step.Args.Trim());
        if (script.StartsWith('/') && script.EndsWith("/d"))
        {
            var re = new Regex(script[1..^2], RegexOptions.CultureInvariant, RegexTimeout);
            return lines.Where(l => !re.IsMatch(l));
        }
        if (script.Length < 4 || script[0] != 's')
            throw new AutorunException("sed: expected s/PATTERN/REPLACEMENT/flags or /PATTERN/d", step.Line);
        var delim = script[1];
        var parts = SplitUnescaped(script[2..], delim);
        if (parts.Count < 2) throw new AutorunException("sed: expected s/PATTERN/REPLACEMENT/", step.Line);
        var flags = parts.Count > 2 ? parts[2] : "";
        var opts = RegexOptions.CultureInvariant | (flags.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None);
        var regex = new Regex(parts[0], opts, RegexTimeout);
        var repl = SedReplacement(parts[1]);
        var all = flags.Contains('g');
        return lines.Select(l => all ? regex.Replace(l, repl) : regex.Replace(l, repl, 1));
    }

    static List<string> SplitUnescaped(string s, char delim)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == delim) { sb.Append(delim); i++; continue; }
            if (s[i] == delim) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(s[i]);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>sed replacement → .NET: \1 → ${1}, &amp; → $0, \&amp; → &amp;, literal $ escaped unless it is already $1/${name}.</summary>
    static string SedReplacement(string r)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < r.Length; i++)
        {
            var c = r[i];
            if (c == '\\' && i + 1 < r.Length)
            {
                var n = r[i + 1];
                if (char.IsDigit(n)) sb.Append("${").Append(n).Append('}');
                else if (n == 'n') sb.Append('\n');
                else if (n == 't') sb.Append('\t');
                else if (n == '$') sb.Append("$$");
                else sb.Append(n);
                i++;
            }
            else if (c == '&') sb.Append("$0");
            else if (c == '$' && (i + 1 >= r.Length || !(char.IsDigit(r[i + 1]) || r[i + 1] == '{'))) sb.Append("$$");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // ───────────────────────── external commands ─────────────────────────

    static class External
    {
        public static List<string> Wsl(string command, List<string> lines, CancellationToken ct)
        {
            if (command.Length == 0) throw new AutorunException("wsl needs a command");
            var psi = new ProcessStartInfo("wsl.exe");
            foreach (var a in new[] { "-e", "bash", "-c", command }) psi.ArgumentList.Add(a);
            psi.Environment["WSL_UTF8"] = "1";
            return Pipe(psi, string.Join("\n", lines) + "\n", ct);
        }

        public static List<string> PowerShell(string command, List<string> lines, CancellationToken ct)
        {
            if (command.Length == 0) throw new AutorunException("ps needs a command");
            // $input is the piped lines; -Command - would read the script itself from stdin, so the lines go through a temp file.
            var tmp = Path.Combine(Path.GetTempPath(), $"termdeck-ps-{Guid.NewGuid():N}.txt");
            File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
            try
            {
                var script = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
                             $"Get-Content -LiteralPath '{tmp.Replace("'", "''")}' -Encoding UTF8 | & {{ {command} }} | ForEach-Object {{ \"$_\" }}";
                var psi = new ProcessStartInfo("powershell.exe");
                foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(a);
                return Pipe(psi, null, ct);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        static List<string> Pipe(ProcessStartInfo psi, string? input, CancellationToken ct)
        {
            psi.RedirectStandardInput = input != null;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            if (input != null) psi.StandardInputEncoding = new UTF8Encoding(false);

            using var p = Process.Start(psi) ?? throw new AutorunException("could not start " + psi.FileName);
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            if (input != null)
            {
                try
                {
                    p.StandardInput.Write(input);
                    p.StandardInput.Close();
                }
                catch (IOException) { } // the command stopped reading early (e.g. head)
            }
            using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            {
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(true); } catch { }
                    throw new AutorunException("command took longer than 60s");
                }
            }
            ct.ThrowIfCancellationRequested();
            var output = stdout.Result;
            // grep exits 1 when nothing matches: that is an empty result, not an error.
            if (p.ExitCode > 1 && output.Length == 0)
            {
                var err = stderr.Result.Trim();
                throw new AutorunException($"exit {p.ExitCode}" + (err.Length > 0 ? ": " + err.Split('\n')[0] : ""));
            }
            return output.Replace("\r\n", "\n").Split('\n').ToList();
        }
    }
}

/// <summary>What a rule will do for one source run: the values it extracted and the argument line of each run of the target tool.</summary>
public sealed record AutoPlan(List<string> Values, List<string> ArgsList, string? ListFile, int Dropped, string? Skipped);

public static partial class Autorun
{
    public const int MaxDepth = 8;

    /// <summary>Placeholders that consume the extracted values; a template without any of them runs once, as a plain trigger.</summary>
    static readonly string[] ValuePlaceholders = ["{{line}}", "{{lines}}", "{{csv}}", "{{first}}", "{{count}}", "{{file}}"];

    public static readonly (string Token, string Help)[] Placeholders =
    [
        ("{{line}}", "one value — runs the tool once per value"),
        ("{{file}}", "path of a file with all values, one per line (for -l / -iL / -list)"),
        ("{{lines}}", "all values separated by spaces"),
        ("{{csv}}", "all values separated by commas"),
        ("{{first}}", "the first value"),
        ("{{count}}", "number of values"),
        ("{{cwd}}", "working directory of the source run"),
        ("{{project}}", "project folder"),
        ("{{src.args}}", "arguments of the source run"),
        ("{{arg:-o}}", "value after -o (any option) in the source run's arguments"),
        ("{{date}}", "date-time, yyyyMMdd-HHmmss"),
    ];

    public static bool Matches(AutoRule rule, RunRecord run) => rule.Trigger switch
    {
        AutoTrigger.Success => run.ExitCode == 0,
        AutoTrigger.Failure => run.ExitCode != 0,
        _ => true,
    };

    public static bool IsPerLine(AutoRule rule) => rule.ArgsTemplate.Contains("{{line}}", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plain text of a run's output, without the "── finished" line TermDeck appends.</summary>
    public static string ReadRunOutput(HistoryStore store, RunRecord run)
    {
        var text = AnsiText.FromCast(store.LogPath(run));
        var cut = text.LastIndexOf("── finished", StringComparison.Ordinal);
        if (cut >= 0 && text.IndexOf('\n', cut) < 0) text = text[..cut];
        return text;
    }

    /// <summary>Windows path of the directory the run used.</summary>
    public static string RunDir(RunRecord run, ToolDef? tool, string projectDir)
    {
        if (string.IsNullOrWhiteSpace(run.Cwd)) return projectDir;
        try { return PathMapper.FromUserInput(run.Cwd, tool); } catch { return projectDir; }
    }

    /// <summary>The source file of a File rule, resolved for a run (Windows path).</summary>
    public static string ResolveSourceFile(AutoRule rule, RunRecord run, ToolDef? srcTool, string projectDir)
    {
        var dir = RunDir(run, srcTool, projectDir);
        var path = PlaceholderRegex().Replace(rule.SourceFile.Trim(), m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "cwd" => dir,
            "project" => projectDir,
            "date" => run.StartedAt.ToString("yyyyMMdd-HHmmss"),
            var k when k.StartsWith("arg:") => ArgValue(run.Args, m.Groups[1].Value[4..]) ?? "",
            _ => m.Value,
        });
        path = FilterScript.Unquote(path.Trim());
        if (path.Length == 0) throw new AutorunException("no source file set");
        // The tool may have written a Linux path (WSL) or a ~ path.
        if (path.StartsWith("~/") && srcTool?.Kind == ToolKind.Wsl)
            throw new AutorunException("use a full path instead of ~ for the source file");
        path = PathMapper.FromUserInput(path, srcTool);
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(dir, path));
    }

    /// <summary>Text the rule works on: the run's terminal output or the file it wrote.</summary>
    public static string ReadSource(AutoRule rule, RunRecord run, HistoryStore store, ToolDef? srcTool)
    {
        if (rule.Source == AutoSource.Output) return ReadRunOutput(store, run);
        var file = ResolveSourceFile(rule, run, srcTool, store.ProjectDir);
        if (!File.Exists(file)) throw new AutorunException("source file not found: " + file);
        return File.ReadAllText(file);
    }

    /// <summary>Reads the source, runs the filter and builds the target's argument lines. Blocking (may call WSL).</summary>
    public static AutoPlan Plan(AutoRule rule, RunRecord run, HistoryStore store, ToolDef? srcTool, ToolDef dstTool,
        bool writeListFile, CancellationToken ct = default)
    {
        var text = ReadSource(rule, run, store, srcTool);
        var values = FilterScript.Run(text, rule.Filter, ct);
        return Build(rule, run, store, srcTool, dstTool, values, writeListFile);
    }

    public static AutoPlan Build(AutoRule rule, RunRecord run, HistoryStore store, ToolDef? srcTool, ToolDef dstTool,
        List<string> values, bool writeListFile)
    {
        var max = rule.MaxItems > 0 ? rule.MaxItems : int.MaxValue;
        var dropped = Math.Max(0, values.Count - max);
        if (dropped > 0) values = values.Take(max).ToList();

        var template = rule.ArgsTemplate.Trim();
        var needsValues = ValuePlaceholders.Any(p => template.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (needsValues && values.Count == 0)
            return new AutoPlan(values, new List<string>(), null, dropped, "the filter found nothing");

        string? listFile = null;
        if (template.Contains("{{file}}", StringComparison.OrdinalIgnoreCase))
        {
            listFile = Path.Combine(store.Root, "autorun", $"{run.Id}-{Slug(rule.Name)}.txt");
            if (writeListFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(listFile)!);
                // LF endings: most consumers are Linux tools.
                File.WriteAllText(listFile, string.Join("\n", values) + "\n", new UTF8Encoding(false));
            }
        }

        var ctx = new ExpandContext(run, store.ProjectDir, RunDir(run, srcTool, store.ProjectDir), dstTool, values, listFile);
        var args = IsPerLine(rule)
            ? values.Select(v => Expand(template, ctx, v)).ToList()
            : new List<string> { Expand(template, ctx, null) };
        return new AutoPlan(values, args, listFile, dropped, null);
    }

    sealed record ExpandContext(RunRecord Run, string ProjectDir, string RunDir, ToolDef Tool, List<string> Values, string? ListFile);

    static string Expand(string template, ExpandContext c, string? line) =>
        PlaceholderRegex().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            switch (key.ToLowerInvariant())
            {
                case "line": return line == null ? "" : Quote(line, c.Tool);
                case "lines": return string.Join(" ", c.Values.Select(v => Quote(v, c.Tool)));
                case "csv": return Quote(string.Join(",", c.Values), c.Tool);
                case "first": return c.Values.Count > 0 ? Quote(c.Values[0], c.Tool) : "";
                case "count": return c.Values.Count.ToString(CultureInfo.InvariantCulture);
                case "file": return c.ListFile == null ? "" : PathMapper.ForCommand(c.ListFile, c.Tool);
                case "cwd": return PathMapper.ForCommand(c.RunDir, c.Tool);
                case "project": return PathMapper.ForCommand(c.ProjectDir, c.Tool);
                case "src.args": return c.Run.Args;
                case "date": return DateTime.Now.ToString("yyyyMMdd-HHmmss");
            }
            if (key.StartsWith("arg:", StringComparison.OrdinalIgnoreCase))
                return ArgValue(c.Run.Args, key[4..]) ?? "";
            return m.Value;
        });

    /// <summary>
    /// Quotes a value taken from tool output so it stays one argument and cannot inject shell syntax:
    /// WSL commands run through bash, so anything outside a safe character set is single-quoted.
    /// </summary>
    public static string Quote(string value, ToolDef tool)
    {
        if (tool.Kind == ToolKind.Wsl)
            return value.Length > 0 && SafeArgRegex().IsMatch(value) ? value : "'" + value.Replace("'", "'\\''") + "'";
        return value.Length > 0 && value.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '%']) < 0 ? value : CommandBuilder.QuoteWin(value);
    }

    /// <summary>The value after an option in an argument line: "-o out.txt", "-o=out.txt", "--output out.txt".</summary>
    public static string? ArgValue(string args, string option)
    {
        var tokens = SplitArgs(args);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == option) return i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (tokens[i].StartsWith(option + "=", StringComparison.Ordinal)) return tokens[i][(option.Length + 1)..];
        }
        return null;
    }

    static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        var has = false;
        foreach (var c in s)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else sb.Append(c);
            }
            else if (c is '"' or '\'') { quote = c; has = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 || has) list.Add(sb.ToString());
                sb.Clear();
                has = false;
            }
            else sb.Append(c);
        }
        if (sb.Length > 0 || has) list.Add(sb.ToString());
        return list;
    }

    static string Slug(string s)
    {
        var slug = SlugRegex().Replace(s.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "rule" : slug.Length > 40 ? slug[..40] : slug;
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z.]+(?::[^}]+)?)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._:/@%+=,\-]+$")]
    private static partial Regex SafeArgRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
