using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TermDeck.Core.Ai;

/// <summary>
/// A tiny loopback HTTP/1.1 server (raw TcpListener, so it needs no URL-ACL / admin) that the MCP child process
/// calls back into. One endpoint, <c>POST /rpc</c>, body <c>{"method","args"}</c>, guarded by a per-session token
/// in the <c>X-TD-Token</c> header. Only 127.0.0.1 is bound. Replies <c>{"ok":true,"result":…}</c> or
/// <c>{"ok":false,"error":"…"}</c>.
/// </summary>
public sealed class ControlServer : IDisposable
{
    readonly IAppControl _control;
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public string Token { get; } = Guid.NewGuid().ToString("N");
    public string Url => $"http://127.0.0.1:{Port}/rpc";

    public ControlServer(IAppControl control)
    {
        _control = control;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = HandleClient(client);
        }
    }

    async Task HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                var (headers, body) = await ReadRequest(stream, _cts.Token);
                if (headers == null)
                    return;

                if (!headers.TryGetValue("x-td-token", out var tok) || tok != Token)
                {
                    await Write(stream, 403, "{\"ok\":false,\"error\":\"forbidden\"}");
                    return;
                }

                string json;
                try
                {
                    using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
                    var root = doc.RootElement;
                    var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                    var args = root.TryGetProperty("args", out var a) ? a.Clone()
                        : JsonDocument.Parse("{}").RootElement;
                    var result = await _control.CallAsync(method, args, _cts.Token);
                    json = JsonSerializer.Serialize(new { ok = true, result }, JsonOpts);
                }
                catch (ControlException ex)
                {
                    json = JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOpts);
                }
                catch (Exception ex)
                {
                    json = JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOpts);
                }
                await Write(stream, 200, json);
            }
            catch { /* client went away */ }
        }
    }

    static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    static async Task<(Dictionary<string, string>? Headers, string Body)> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var ms = new MemoryStream();
        int headerEnd = -1;
        // Read until the blank line that ends the headers.
        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n <= 0) return (null, "");
            ms.Write(buf, 0, n);
            headerEnd = IndexOfDoubleCrlf(ms.GetBuffer(), (int)ms.Length);
            if (ms.Length > 1 << 20) return (null, ""); // 1 MB header cap
        }

        var all = ms.GetBuffer();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = headerText.Split("\r\n");
        foreach (var line in lines)
        {
            var c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim().ToLowerInvariant()] = line[(c + 1)..].Trim();
        }

        var bodyStart = headerEnd + 4;
        var have = (int)ms.Length - bodyStart;
        var len = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var v) ? v : 0;
        var bodyBytes = new byte[Math.Max(0, len)];
        if (len > 0)
        {
            Array.Copy(all, bodyStart, bodyBytes, 0, Math.Min(have, len));
            var read = Math.Min(have, len);
            while (read < len)
            {
                var n = await stream.ReadAsync(bodyBytes.AsMemory(read, len - read), ct);
                if (n <= 0) break;
                read += n;
            }
        }
        return (headers, Encoding.UTF8.GetString(bodyBytes));
    }

    static int IndexOfDoubleCrlf(byte[] b, int len)
    {
        for (var i = 0; i + 3 < len; i++)
            if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
        return -1;
    }

    static async Task Write(NetworkStream stream, int status, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Forbidden")}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
