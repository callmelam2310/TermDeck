using System;
using System.Windows;
using TermDeck.Core;
using TermDeck.Terminal;

namespace TermDeck.Views;

/// <summary>Content of a document tab: a tool tab or a free shell tab.</summary>
public interface IDocView : IDisposable
{
    UIElement Element => (UIElement)this;

    /// <summary>The tool (or a stand-in for a shell) — used by the Files panel to map and quote paths.</summary>
    ToolDef Tool { get; }
    string Cwd { get; }
    /// <summary>The terminal, or null for views that have none (the AI agent tab).</summary>
    TerminalHost? Terminal { get; }
    /// <summary>Base file name (no extension) for exporting what the terminal shows.</summary>
    string ExportName { get; }
    bool HasRunning { get; }
    int RunningCount { get; }

    event Action<IDocView>? CwdChanged;
    event Action<IDocView>? RunningChanged;

    void SetCwd(string dir, bool raise);
    void InsertText(string text);
    void FocusInput();
    void OpenFind();
    void ApplyFont();
    void KillAll();
}
