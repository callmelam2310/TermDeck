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
    }

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
        var list = new List<RunRecord>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, tool_id, tool_name, args, command_line, cwd, started_at, ended_at, exit_code, log_file
            FROM runs WHERE tool_id=$t ORDER BY id DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$t", toolId);
        cmd.Parameters.AddWithValue("$n", limit);
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

    public void Delete(RunRecord r)
    {
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM runs WHERE id=$id";
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
