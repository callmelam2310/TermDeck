using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermDeck.Core.Ai;

/// <summary>
/// A saved AI provider/model configuration the user can switch between (modelled on Pentest-Assistant's
/// ai_profiles.json). <see cref="Extra"/> is free-form KEY=VALUE env, one per line. Everything is injected only
/// into the spawned Claude CLI, never the machine. An all-blank profile uses the logged-in Claude subscription.
/// </summary>
public sealed class AgentProfile
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public string Model { get; set; } = "";
    public string Extra { get; set; } = "";

    public AgentProfile Clone() => (AgentProfile)MemberwiseClone();

    /// <summary>The env to inject into the Claude CLI for this profile (base URL + token + Extra lines).</summary>
    public Dictionary<string, string> BuildEnv()
    {
        var d = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(BaseUrl)) d["ANTHROPIC_BASE_URL"] = BaseUrl.Trim();
        if (!string.IsNullOrWhiteSpace(AuthToken)) d["ANTHROPIC_AUTH_TOKEN"] = AuthToken.Trim();
        foreach (var raw in (Extra ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=')) continue;
            var i = line.IndexOf('=');
            var k = line[..i].Trim();
            var v = line[(i + 1)..].Trim();
            if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0]) v = v[1..^1];
            if (k.Length > 0) d[k] = v;
        }
        return d;
    }
}

/// <summary>Reads/writes the agent profiles file (<c>DataDir\agent\profiles.json</c>) and tracks the active one.</summary>
public sealed class AgentProfilesData
{
    [JsonPropertyName("active")] public string Active { get; set; } = "";
    [JsonPropertyName("profiles")] public List<AgentProfile> Profiles { get; set; } = new();
}

public static class AgentProfiles
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(AppPaths.DataDir, "agent", "profiles.json");

    public static AgentProfilesData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AgentProfilesData>(File.ReadAllText(FilePath)) ?? Default();
        }
        catch { }
        return Default();
    }

    public static void Save(AgentProfilesData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data, Opts));
    }

    /// <summary>The active profile, or the first one, or a blank subscription profile.</summary>
    public static AgentProfile Active(AgentProfilesData data) =>
        data.Profiles.FirstOrDefault(p => p.Name == data.Active)
        ?? data.Profiles.FirstOrDefault()
        ?? new AgentProfile { Name = "Claude subscription" };

    static AgentProfilesData Default() => new()
    {
        Active = "Claude subscription",
        Profiles = { new AgentProfile { Name = "Claude subscription" } },
    };
}
