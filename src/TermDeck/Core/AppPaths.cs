using System;
using System.IO;

namespace TermDeck.Core;

/// <summary>
/// Portable mode: a portable.txt file or a data\ folder next to the exe → everything is stored in .\data\.
/// Otherwise %APPDATA%\TermDeck is used.
/// </summary>
public static class AppPaths
{
    public static string ExeDir { get; } = AppContext.BaseDirectory.TrimEnd('\\', '/');
    public static bool IsPortable { get; }
    public static string DataDir { get; }

    static AppPaths()
    {
        IsPortable = File.Exists(Path.Combine(ExeDir, "portable.txt")) || Directory.Exists(Path.Combine(ExeDir, "data"));
        DataDir = IsPortable
            ? Path.Combine(ExeDir, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TermDeck");
        Directory.CreateDirectory(DataDir);
    }

    public static string ConfigFile => Path.Combine(DataDir, "config.json");

    public static string WebViewDir => IsPortable
        ? Path.Combine(DataDir, "webview")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermDeck", "webview");

    public static string DefaultProjectDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TermDeck", "Default");

    /// <summary>Relative paths (.\tools\x.exe) resolve against the exe folder; bare names (nmap.exe) are left for PATH lookup.</summary>
    public static string ResolveToolPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return path;
        if (!path.Contains('\\') && !path.Contains('/')) return path;
        return Path.GetFullPath(Path.Combine(ExeDir, path));
    }
}
