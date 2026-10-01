using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TermDeck.Views;

/// <summary>
/// A small, dependency-free Markdown → WPF FlowDocument renderer for the AI agent transcript. It covers what a
/// chat reply uses — headings, bullet/numbered lists, fenced and inline code, bold/italic, blockquotes — and
/// renders anything else as plain text. Not a full CommonMark implementation.
/// </summary>
public static class Md
{
    public static List<Block> ToBlocks(string markdown, FontFamily mono, Brush codeBg)
    {
        var blocks = new List<Block>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // Fenced code block
            if (line.TrimStart().StartsWith("```"))
            {
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) { code.AppendLine(lines[i]); i++; }
                if (i < lines.Length) i++; // closing fence
                var p = new Paragraph(new Run(code.ToString().TrimEnd('\n')))
                {
                    FontFamily = mono,
                    FontSize = 12,
                    Background = codeBg,
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 4, 0, 4),
                };
                blocks.Add(p);
                continue;
            }

            // Blank line
            if (line.Trim().Length == 0) { i++; continue; }

            // Heading
            if (line.StartsWith('#'))
            {
                var level = 0;
                while (level < line.Length && line[level] == '#') level++;
                var p = new Paragraph { Margin = new Thickness(0, 8, 0, 2) };
                var r = new Run(line[level..].Trim()) { FontWeight = FontWeights.Bold, FontSize = level <= 1 ? 16 : level == 2 ? 14.5 : 13.5 };
                p.Inlines.Add(r);
                blocks.Add(p);
                i++;
                continue;
            }

            // Blockquote
            if (line.TrimStart().StartsWith("> "))
            {
                var p = new Paragraph { Margin = new Thickness(10, 2, 0, 2), Foreground = Brushes.Gray };
                AddInlines(p, line.TrimStart()[2..]);
                blocks.Add(p);
                i++;
                continue;
            }

            // List (bullet or numbered) — collect consecutive items
            if (IsListItem(line, out _))
            {
                var list = new List { Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(18, 0, 0, 0) };
                list.MarkerStyle = line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* ")
                    ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal;
                while (i < lines.Length && IsListItem(lines[i], out var content))
                {
                    var item = new ListItem();
                    var p = new Paragraph { Margin = new Thickness(0) };
                    AddInlines(p, content);
                    item.Blocks.Add(p);
                    list.ListItems.Add(item);
                    i++;
                }
                blocks.Add(list);
                continue;
            }

            // Paragraph: gather until a blank line / structural line
            var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
            var first = true;
            while (i < lines.Length && lines[i].Trim().Length > 0 && !lines[i].StartsWith('#')
                   && !lines[i].TrimStart().StartsWith("```") && !lines[i].TrimStart().StartsWith("> ")
                   && !IsListItem(lines[i], out _))
            {
                if (!first) para.Inlines.Add(new LineBreak());
                AddInlines(para, lines[i]);
                first = false;
                i++;
            }
            blocks.Add(para);
        }
        return blocks;
    }

    static bool IsListItem(string line, out string content)
    {
        var t = line.TrimStart();
        if (t.StartsWith("- ") || t.StartsWith("* ")) { content = t[2..]; return true; }
        var dot = t.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && dot <= 3 && int.TryParse(t[..dot], out _)) { content = t[(dot + 2)..]; return true; }
        content = "";
        return false;
    }

    /// <summary>Inline **bold**, *italic*, `code`.</summary>
    static void AddInlines(Paragraph p, string text)
    {
        var i = 0;
        var buf = new StringBuilder();
        void Flush() { if (buf.Length > 0) { p.Inlines.Add(new Run(buf.ToString())); buf.Clear(); } }
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    p.Inlines.Add(new Run(text[(i + 1)..end]) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) });
                    i = end + 1;
                    continue;
                }
            }
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    Flush();
                    p.Inlines.Add(new Bold(new Run(text[(i + 2)..end])));
                    i = end + 2;
                    continue;
                }
            }
            if (c == '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i)
                {
                    Flush();
                    p.Inlines.Add(new Italic(new Run(text[(i + 1)..end])));
                    i = end + 1;
                    continue;
                }
            }
            buf.Append(c);
            i++;
        }
        Flush();
    }
}
