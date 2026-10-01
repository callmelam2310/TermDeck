using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TermDeck.Core;

/// <summary>
/// Each run's output is stored as asciicast v2 (asciinema): ANSI colors and timing are preserved,
/// and the file can be replayed outside TermDeck with asciinema play / asciinema-player.
/// </summary>
public sealed class CastWriter : IDisposable
{
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    readonly StreamWriter _w;

    public CastWriter(string path, int cols, int rows, string command)
    {
        _w = new StreamWriter(path, false, new UTF8Encoding(false));
        var header = new
        {
            version = 2,
            width = cols,
            height = rows,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            command,
            env = new { TERM = "xterm-256color" },
        };
        _w.Write(JsonSerializer.Serialize(header, Json));
        _w.Write('\n');
        _w.Flush();
    }

    public void Output(double seconds, string data)
    {
        _w.Write('[');
        _w.Write(seconds.ToString("0.000", CultureInfo.InvariantCulture));
        _w.Write(", \"o\", ");
        _w.Write(JsonSerializer.Serialize(data, Json));
        _w.Write("]\n");
        _w.Flush();
    }

    /// <summary>asciicast v2 resize event ("COLSxROWS"), so the output can be replayed at the right size.</summary>
    public void Resize(double seconds, int cols, int rows)
    {
        _w.Write('[');
        _w.Write(seconds.ToString("0.000", CultureInfo.InvariantCulture));
        _w.Write($", \"r\", \"{cols}x{rows}\"]\n");
        _w.Flush();
    }

    public void Dispose() => _w.Dispose();
}

public static class CastReader
{
    /// <summary>Concatenates all output ("o") events of a .cast file so it can be written straight into the terminal.</summary>
    public static string ReadOutput(string path)
    {
        if (!File.Exists(path)) return "";
        var sb = new StringBuilder();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8);
        r.ReadLine(); // header
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var arr = doc.RootElement;
                if (arr.GetArrayLength() >= 3 && arr[1].GetString() == "o")
                    sb.Append(arr[2].GetString());
            }
            catch (JsonException)
            {
                // The last line may be truncated if the app was killed.
            }
        }
        return sb.ToString();
    }

    /// <summary>Terminal size from the header plus every output ("o") and resize ("r") event, in order.</summary>
    public static (int Cols, int Rows, List<(char Type, string Data)> Events) ReadEvents(string path)
    {
        var events = new List<(char, string)>();
        int cols = 120, rows = 30;
        if (!File.Exists(path)) return (cols, rows, events);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8);
        try
        {
            using var header = JsonDocument.Parse(r.ReadLine() ?? "{}");
            if (header.RootElement.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv)) cols = wv;
            if (header.RootElement.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv)) rows = hv;
        }
        catch (JsonException) { }
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var arr = doc.RootElement;
                if (arr.GetArrayLength() < 3) continue;
                var type = arr[1].GetString();
                if (type is "o" or "r") events.Add((type[0], arr[2].GetString() ?? ""));
            }
            catch (JsonException) { }
        }
        return (cols, rows, events);
    }
}
