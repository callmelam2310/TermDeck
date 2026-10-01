using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using TermDeck.Core;
using TermDeck.Terminal;

namespace TermDeck.Views;

/// <summary>Saves what a terminal shows as plain text, ANSI (colors kept) or HTML.</summary>
public static class TerminalExport
{
    public static async Task SaveAsync(Window? owner, TerminalHost term, string baseName, string initialDir, bool visibleOnly = false)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = visibleOnly ? "Export visible screen" : "Export terminal text",
            InitialDirectory = Directory.Exists(initialDir) ? initialDir : null,
            FileName = ReportBuilder.SafeName(baseName) + (visibleOnly ? "-screen" : ""),
            Filter = visibleOnly
                ? "Plain text (*.txt)|*.txt"
                : "Plain text (*.txt)|*.txt|Text with colors — ANSI, view with cat / less -R (*.ansi)|*.ansi|HTML with colors (*.html)|*.html",
            AddExtension = true,
        };
        if (dlg.ShowDialog(owner) != true) return;

        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        var content = ext switch
        {
            ".html" or ".htm" when !visibleOnly => WrapHtml(await term.GetBufferHtmlAsync(), baseName),
            ".ansi" when !visibleOnly => await term.GetBufferAnsiAsync(),
            _ => await term.GetBufferTextAsync(visibleOnly),
        };
        if (content.Length == 0)
        {
            MessageBox.Show(owner!, "The terminal is empty (or not ready yet).", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, content + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner!, "Could not save the file:\n" + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public static async Task CopyAsync(TerminalHost term)
    {
        var text = await term.GetBufferTextAsync();
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>serializeAsHTML returns a clipboard fragment (&lt;html&gt;&lt;body&gt;…); give it a charset and title so it opens correctly as a file.</summary>
    static string WrapHtml(string fragment, string title)
    {
        if (fragment.Length == 0) return "";
        var head = $"<!doctype html>\n<html>\n<head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>" +
                   "<style>body{margin:0;background:#0c0c0c}pre{margin:0;padding:8px}</style></head>";
        return fragment.StartsWith("<html>", StringComparison.OrdinalIgnoreCase)
            ? head + fragment["<html>".Length..]
            : head + "<body>" + fragment + "</body></html>";
    }
}
