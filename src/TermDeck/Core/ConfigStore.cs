using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermDeck.Core;

public static class ConfigStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), Options) ?? new AppConfig();
        }
        catch (Exception)
        {
            // Corrupt file: keep a copy so the user can recover it, then start with an empty config.
            try { File.Copy(AppPaths.ConfigFile, AppPaths.ConfigFile + ".broken", true); } catch { }
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        var tmp = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        File.Move(tmp, AppPaths.ConfigFile, true);
    }
}
