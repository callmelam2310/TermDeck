using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TermDeck.Core;

/// <summary>
/// Run history of a project: &lt;project&gt;\.termdeck\history.db + runs\&lt;id&gt;.cast.
/// Projects on network/WSL paths (\\...) are stored in the data dir instead: SQLite locking is unreliable over 9P/SMB.
/// </summary>
public sealed class HistoryStore
{
    public string ProjectDir { get; }
    public string Root { get; }
    readonly string _cs;

    public HistoryStore(string projectDir)
    {
        ProjectDir = projectDir;
        if (projectDir.StartsWith(@"\\", StringComparison.Ordinal))
        {
            Root = Path.Combine(AppPaths.DataDir, "projects", Hash(projectDir));
        }
        else
        {
            Root = Path.Combine(projectDir, ".termdeck");
        }
        var created = !Directory.Exists(Root);
        Directory.CreateDirectory(Path.Combine(Root, "runs"));
        if (created && !projectDir.StartsWith(@"\\", StringComparison.Ordinal))
        {
            try { File.SetAttributes(Root, File.GetAttributes(Root) | FileAttributes.Hidden); } catch { }
        }

        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "history.db") }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS runs(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tool_id TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                args TEXT NOT NULL,
                command_line TEXT NOT NULL,
                cwd TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                exit_code INTEGER,
                log_file TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_runs_tool ON runs(tool_id, id DESC);
            """);

        // Output search index (rowid = run id). The trigram tokenizer gives case-insensitive substring search
        // (IPs, hostnames, paths) with LIKE; plain FTS5 is the fallback if the SQLite build lacks it.
        try { Exec(c, "CREATE VIRTUAL TABLE IF NOT EXISTS output_fts USING fts5(text, tokenize='trigram')"); }
        catch (SqliteException) { Exec(c, "CREATE VIRTUAL TABLE IF NOT EXISTS output_fts USING fts5(text)"); }
        using var probe = c.CreateCommand();
        probe.CommandText = "SELECT sql FROM sqlite_master WHERE name='output_fts'";
        _trigram = (probe.ExecuteScalar() as string)?.Contains("trigram", StringComparison.OrdinalIgnoreCase) == true;
    }

    readonly bool _trigram;

    const string Columns = "id, tool_id, tool_name, args, command_line, cwd, started_at, ended_at, exit_code, log_file";

    /// <summary>Output beyond this many characters is not indexed (keeps the index small for huge scans).</summary>
    const int MaxIndexedChars = 8 * 1024 * 1024;

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public string LogPath(RunRecord r) => Path.Combine(Root, r.LogFile.Replace('/', '\\'));

    public void Insert(RunRecord r)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runs(tool_id, tool_name, args, command_line, cwd, started_at)
            VALUES($tool_id, $tool_name, $args, $cl, $cwd, $started);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$tool_id", r.ToolId);
        cmd.Parameters.AddWithValue("$tool_name", r.ToolName);
        cmd.Parameters.AddWithValue("$args", r.Args);
        cmd.Parameters.AddWithValue("$cl", r.CommandLine);
        cmd.Parameters.AddWithValue("$cwd", r.Cwd);
        cmd.Parameters.AddWithValue("$started", r.StartedAt.ToString("o"));
        r.Id = (long)cmd.ExecuteScalar()!;
        r.LogFile = $"runs/{r.Id}.cast";

        using var up = c.CreateCommand();
        up.CommandText = "UPDATE runs SET log_file=$f WHERE id=$id";
        up.Parameters.AddWithValue("$f", r.LogFile);
        up.Parameters.AddWithValue("$id", r.Id);
        up.ExecuteNonQuery();
    }

    public void Finish(RunRecord r)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE runs SET ended_at=$e, exit_code=$x WHERE id=$id";
        cmd.Parameters.AddWithValue("$e", (object?)r.EndedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$x", (object?)r.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.ExecuteNonQuery();
    }

    public List<RunRecord> List(string toolId, int limit = 1000)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM runs WHERE tool_id=$t ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$t", toolId);
        cmd.Parameters.AddWithValue("$n", limit);
        return ReadAll(cmd);
    }

    /// <summary>One run by id, or null if it is not in this project.</summary>
    public RunRecord? Get(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM runs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        var list = ReadAll(cmd);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>Every run of the project, oldest first (for reports).</summary>
    public List<RunRecord> ListAll()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM runs ORDER BY id";
        return ReadAll(cmd);
    }

    /// <summary>Tools that have runs in this project (including deleted tools and shell sessions), with their run count.</summary>
    public List<(string ToolId, string ToolName, int Count)> ToolsWithRuns()
    {
        var list = new List<(string, string, int)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT tool_id, tool_name, COUNT(*) FROM runs GROUP BY tool_id ORDER BY MAX(id) DESC";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetString(1), rd.GetInt32(2)));
        return list;
    }

    static List<RunRecord> ReadAll(SqliteCommand cmd)
    {
        var list = new List<RunRecord>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new RunRecord
            {
                Id = rd.GetInt64(0),
                ToolId = rd.GetString(1),
                ToolName = rd.GetString(2),
                Args = rd.GetString(3),
                CommandLine = rd.GetString(4),
                Cwd = rd.GetString(5),
                StartedAt = ParseDate(rd.GetString(6)),
                EndedAt = rd.IsDBNull(7) ? null : ParseDate(rd.GetString(7)),
                ExitCode = rd.IsDBNull(8) ? null : rd.GetInt32(8),
                LogFile = rd.GetString(9),
            });
        }
        return list;
    }

    // ───────────────────────── Output search ─────────────────────────

    /// <summary>(Re)indexes a run's output as plain text. Safe to call again when the run finishes.</summary>
    public void IndexOutput(RunRecord r)
    {
        var text = AnsiText.FromCast(LogPath(r));
        if (text.Length > MaxIndexedChars) text = text[..MaxIndexedChars];
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM output_fts WHERE rowid=$id";
            del.Parameters.AddWithValue("$id", r.Id);
            del.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO output_fts(rowid, text) VALUES($id, $text)";
            ins.Parameters.AddWithValue("$id", r.Id);
            ins.Parameters.AddWithValue("$text", text);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Runs whose output is not in the search index yet (older runs, or runs killed with the app).</summary>
    public List<RunRecord> Unindexed()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM runs WHERE id NOT IN (SELECT rowid FROM output_fts) ORDER BY id DESC";
        return ReadAll(cmd);
    }

    /// <summary>
    /// Case-insensitive substring search in the output, arguments, command line and tool name of every run.
    /// Newest first. <paramref name="toolId"/> = null for all tools.
    /// </summary>
    public List<RunRecord> Search(string query, string? toolId, int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // A quoted phrase on a trigram index is an indexed, case-insensitive substring match (needs ≥ 3 chars).
        var outputMatch = _trigram && query.Length >= 3
            ? "SELECT rowid FROM output_fts WHERE output_fts MATCH $m"
            : "SELECT rowid FROM output_fts WHERE instr(lower(text), lower($q)) > 0";
        cmd.CommandText = $"""
            SELECT {Columns} FROM runs
            WHERE ($tool IS NULL OR tool_id=$tool)
              AND (id IN ({outputMatch})
                   OR instr(lower(args), lower($q)) > 0 OR instr(lower(command_line), lower($q)) > 0
                   OR instr(lower(tool_name), lower($q)) > 0)
            ORDER BY id DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$tool", (object?)toolId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$m", "\"" + query.Replace("\"", "\"\"") + "\"");
        cmd.Parameters.AddWithValue("$n", limit);
        return ReadAll(cmd);
    }

    /// <summary>
    /// For each run whose output contains <paramref name="query"/>: the text around the first match and the number of matches,
    /// computed inside SQLite so large outputs are not loaded.
    /// </summary>
    public Dictionary<long, (string Excerpt, int Offset, int Count)> Snippets(IReadOnlyCollection<long> runIds, string query, int before = 60)
    {
        var result = new Dictionary<long, (string, int, int)>();
        if (runIds.Count == 0 || query.Length == 0) return result;
        using var c = Open();
        using var cmd = c.CreateCommand();
        // instr() is 1-based; the excerpt starts up to `before` characters ahead of the match.
        cmd.CommandText = $"""
            SELECT rowid, pos, substr(text, max(1, pos - $b), $b + length($q) + 160),
                   (length(text) - length(replace(lower(text), lower($q), ''))) / length($q)
            FROM (SELECT rowid, text, instr(lower(text), lower($q)) AS pos FROM output_fts
                  WHERE rowid IN ({string.Join(",", runIds)}))
            WHERE pos > 0
            """;
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$b", before);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var pos = rd.GetInt32(1);
            result[rd.GetInt64(0)] = (rd.GetString(2), pos - Math.Max(1, pos - before), rd.GetInt32(3));
        }
        return result;
    }

    /// <summary>The indexed plain text of a run's output (empty if not indexed).</summary>
    public string IndexedText(long runId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT text FROM output_fts WHERE rowid=$id";
        cmd.Parameters.AddWithValue("$id", runId);
        return cmd.ExecuteScalar() as string ?? "";
    }

    public void Delete(RunRecord r)
    {
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM runs WHERE id=$id; DELETE FROM output_fts WHERE rowid=$id;";
            cmd.Parameters.AddWithValue("$id", r.Id);
            cmd.ExecuteNonQuery();
        }
        try { File.Delete(LogPath(r)); } catch { }
    }

    static DateTime ParseDate(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..16];
}
