using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermDeck.Core;

/// <summary>A shareable file of tools grouped by collection (termdeck-tools JSON).</summary>
public sealed class ToolPack
{
    public const string FormatName = "termdeck-tools";

    /// <summary>Must be <see cref="FormatName"/>; empty when a JSON file without it is read.</summary>
    public string Format { get; set; } = "";
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public string App { get; set; } = "";
    public List<PackCollection> Collections { get; set; } = new();
}

/// <summary>Name "" = ungrouped tools.</summary>
public sealed class PackCollection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<PackTool> Tools { get; set; } = new();
}

/// <summary>Tool fields that travel between machines. The id is kept so run history in a project still matches after re-import.</summary>
public sealed class PackTool
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ToolKind Kind { get; set; }
    public string Path { get; set; } = "";
    public string Distro { get; set; } = "";
    public string RunAsUser { get; set; } = "";
    public string WslShell { get; set; } = "bash";
    public string DefaultArgs { get; set; } = "";

    public static PackTool From(ToolDef t) => new()
    {
        Id = t.Id, Name = t.Name, Kind = t.Kind, Path = t.Path, Distro = t.Distro,
        RunAsUser = t.RunAsUser, WslShell = t.WslShell, DefaultArgs = t.DefaultArgs,
    };

    public ToolDef ToTool(string collectionId) => new()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
        Name = Name.Trim(), Kind = Kind, Path = Path.Trim(), Distro = Distro ?? "",
        RunAsUser = RunAsUser ?? "", WslShell = string.IsNullOrWhiteSpace(WslShell) ? "bash" : WslShell,
        DefaultArgs = DefaultArgs ?? "", CollectionId = collectionId,
    };
}

public static class ToolTransfer
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="collectionIds">Collections to include; "" = the ungrouped tools.</param>
    public static ToolPack Create(IEnumerable<ToolDef> tools, IEnumerable<ToolCollection> collections, ICollection<string> collectionIds)
    {
        var known = collections.ToList();
        var pack = new ToolPack { Format = ToolPack.FormatName, App = "TermDeck " + typeof(ToolTransfer).Assembly.GetName().Version?.ToString(3) };
        foreach (var c in known.Where(c => collectionIds.Contains(c.Id)))
            pack.Collections.Add(new PackCollection { Id = c.Id, Name = c.Name });
        var ungrouped = collectionIds.Contains("") ? new PackCollection() : null;

        foreach (var t in tools)
        {
            var target = pack.Collections.FirstOrDefault(c => c.Id == t.CollectionId)
                         ?? (known.Any(c => c.Id == t.CollectionId) ? null : ungrouped);
            target?.Tools.Add(PackTool.From(t));
        }
        if (ungrouped is { Tools.Count: > 0 }) pack.Collections.Add(ungrouped);
        return pack;
    }

    public static void Save(ToolPack pack, string file) =>
        File.WriteAllText(file, JsonSerializer.Serialize(pack, Options));

    /// <summary>Reads a termdeck-tools file; throws <see cref="InvalidDataException"/> with a readable message if it is not one.</summary>
    public static ToolPack Load(string file)
    {
        ToolPack? pack;
        try { pack = JsonSerializer.Deserialize<ToolPack>(File.ReadAllText(file), Options); }
        catch (JsonException ex) { throw new InvalidDataException("Not a valid JSON file: " + ex.Message); }
        if (pack == null || pack.Format != ToolPack.FormatName)
            throw new InvalidDataException("This file is not a TermDeck tools export (format “termdeck-tools”).");
        pack.Collections.ForEach(c => c.Tools.RemoveAll(t => string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Path)));
        return pack;
    }

    public static string DefaultFileName(string what) =>
        $"termdeck-tools-{ReportBuilder.SafeName(what)}-{DateTime.Now:yyyyMMdd}.json";
}
