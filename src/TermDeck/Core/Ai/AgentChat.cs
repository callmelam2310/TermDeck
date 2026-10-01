using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TermDeck.Core.Ai;

/// <summary>One line of a saved agent conversation.</summary>
public sealed class AgentChatEntry
{
    /// <summary>"user", "assistant", "tool_use", "tool_result" or "error".</summary>
    public string Role { get; set; } = "";
    /// <summary>Tool name for tool_use / tool_result rows.</summary>
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Error { get; set; }
}

/// <summary>A persisted agent conversation, saved per project so it survives restarts and can be reopened.</summary>
public sealed class AgentChat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    /// <summary>Claude CLI session id, for <c>--resume</c> on follow-ups.</summary>
    public string SessionId { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Model { get; set; } = "";
    public List<AgentChatEntry> Entries { get; set; } = new();
}

/// <summary>
/// Reads/writes agent conversations under the project's history root (<c>&lt;root&gt;\agent\chats\*.json</c>), the
/// same place TermDeck keeps that project's run history — so chats live with the project, not globally.
/// </summary>
public static class AgentChatStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    static string Dir(string root) => Path.Combine(root, "agent", "chats");

    public static void Save(string root, AgentChat chat)
    {
        var dir = Dir(root);
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, chat.Id + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(chat, Opts));
        File.Move(tmp, Path.Combine(dir, chat.Id + ".json"), true);
    }

    public static AgentChat? Load(string root, string id)
    {
        try
        {
            var path = Path.Combine(Dir(root), id + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentChat>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    /// <summary>All saved chats of a project, newest first.</summary>
    public static List<AgentChat> List(string root)
    {
        var list = new List<AgentChat>();
        try
        {
            var dir = Dir(root);
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                try { if (JsonSerializer.Deserialize<AgentChat>(File.ReadAllText(f)) is { } c) list.Add(c); }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(c => c.Updated).ToList();
    }

    public static void Delete(string root, string id)
    {
        try { File.Delete(Path.Combine(Dir(root), id + ".json")); } catch { }
    }
}
