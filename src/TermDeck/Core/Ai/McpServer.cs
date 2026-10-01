using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TermDeck.Core.Ai;

/// <summary>
/// The headless MCP server run by <c>TermDeck.exe --mcp</c>. Speaks newline-delimited JSON-RPC (the MCP stdio
/// transport) on stdin/stdout, advertises <see cref="AiTools"/>, and forwards every <c>tools/call</c> to the
/// running app's loopback control server (URL + token in env). No WPF — just a pipe between the Claude CLI and
/// the app. It trusts only the parent app: the token proves the call came from the TermDeck that spawned it.
/// </summary>
public static class McpServer
{
    const string ProtocolVersion = "2024-11-05";

    public static int Run()
    {
        var url = Environment.GetEnvironmentVariable("TD_CONTROL_URL");
        var token = Environment.GetEnvironmentVariable("TD_CONTROL_TOKEN");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine("TermDeck MCP: TD_CONTROL_URL / TD_CONTROL_TOKEN not set (run from the TermDeck AI agent).");
            return 2;
        }

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.Add("X-TD-Token", token);

        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
                var hasId = root.TryGetProperty("id", out var idEl);
                var id = hasId ? idEl.Clone() : default;

                if (method == null) continue;
                // Notifications (no id) get no response.
                try
                {
                    switch (method)
                    {
                        case "initialize":
                            Respond(stdout, id, new
                            {
                                protocolVersion = ProtocolVersion,
                                capabilities = new { tools = new { } },
                                serverInfo = new { name = "termdeck", version = "0.5.0" },
                            });
                            break;
                        case "notifications/initialized":
                        case "notifications/cancelled":
                            break; // notification, ignore
                        case "ping":
                            Respond(stdout, id, new { });
                            break;
                        case "tools/list":
                            Respond(stdout, id, ToolsList());
                            break;
                        case "tools/call":
                            HandleCall(stdout, http, url!, id, root);
                            break;
                        default:
                            if (hasId) Error(stdout, id, -32601, "method not found: " + method);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    if (hasId) Error(stdout, id, -32603, ex.Message);
                }
            }
        }
        return 0;
    }

    static object ToolsList()
    {
        var tools = new object[AiTools.All.Count];
        for (var i = 0; i < AiTools.All.Count; i++)
        {
            var t = AiTools.All[i];
            tools[i] = new
            {
                name = t.Name,
                description = t.Description,
                inputSchema = JsonDocument.Parse(t.InputSchema).RootElement.Clone(),
            };
        }
        return new { tools };
    }

    static void HandleCall(StreamWriter stdout, HttpClient http, string url, JsonElement id, JsonElement root)
    {
        var p = root.TryGetProperty("params", out var pe) ? pe : default;
        var name = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "";
        var args = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("arguments", out var ae) ? ae : default;
        var argsJson = args.ValueKind == JsonValueKind.Object || args.ValueKind == JsonValueKind.Array
            ? args.GetRawText() : "{}";

        var reqBody = $"{{\"method\":{JsonSerializer.Serialize(name)},\"args\":{argsJson}}}";
        string respText;
        try
        {
            using var content = new StringContent(reqBody, Encoding.UTF8, "application/json");
            var resp = http.PostAsync(url, content).GetAwaiter().GetResult();
            respText = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ToolResult(stdout, id, "TermDeck is not reachable: " + ex.Message, isError: true);
            return;
        }

        try
        {
            using var rd = JsonDocument.Parse(respText);
            var r = rd.RootElement;
            if (r.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                var result = r.TryGetProperty("result", out var res) ? res : default;
                var text = result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? "ok"
                    : (result.ValueKind == JsonValueKind.String ? result.GetString()! : JsonSerializer.Serialize(result, Indented));
                ToolResult(stdout, id, text, isError: false);
            }
            else
            {
                var err = r.TryGetProperty("error", out var e) ? e.GetString() : "error";
                ToolResult(stdout, id, err ?? "error", isError: true);
            }
        }
        catch
        {
            ToolResult(stdout, id, respText, isError: false);
        }
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static void ToolResult(StreamWriter stdout, JsonElement id, string text, bool isError) =>
        Respond(stdout, id, new { content = new[] { new { type = "text", text } }, isError });

    static void Respond(StreamWriter stdout, JsonElement id, object result)
    {
        var msg = new JsonObject(id, result, null);
        stdout.WriteLine(msg.Serialize());
    }

    static void Error(StreamWriter stdout, JsonElement id, int code, string message)
    {
        var msg = new JsonObject(id, null, new { code, message });
        stdout.WriteLine(msg.Serialize());
    }

    readonly record struct JsonObject(JsonElement Id, object? Result, object? Error)
    {
        public string Serialize()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("jsonrpc", "2.0");
                w.WritePropertyName("id");
                if (Id.ValueKind == JsonValueKind.Undefined) w.WriteNullValue(); else Id.WriteTo(w);
                if (Error != null)
                {
                    w.WritePropertyName("error");
                    JsonSerializer.Serialize(w, Error);
                }
                else
                {
                    w.WritePropertyName("result");
                    JsonSerializer.Serialize(w, Result);
                }
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}
