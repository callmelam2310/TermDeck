using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TermDeck.Core;

/// <summary>
/// A minimal terminal screen that replays output to recover its text: cursor positioning, erase line/screen,
/// scrolling into scrollback, and colors (kept as SGR sequences per cell). Lines are not wrapped at the terminal
/// width, so a long logical line stays one line. Full-screen apps (alternate screen) are skipped, like in a log.
/// It is an approximation — just enough for ConPTY output, which repaints the screen with cursor moves.
/// </summary>
public sealed class TextScreen
{
    struct Cell
    {
        public char Ch;
        public int Style;
    }

    readonly List<List<Cell>> _lines = new() { new() };
    readonly List<string> _styles = new() { "" };
    readonly Dictionary<string, int> _styleIds = new() { [""] = 0 };
    readonly bool _keepSgr;
    readonly StringBuilder _pending = new();

    int _rows;
    int _top;   // index of the first screen line in _lines
    int _row;   // cursor, absolute line index
    int _col;
    int _style;
    string _styleSeq = "";
    int _savedRow, _savedCol;
    bool _alt;
    int _altRow, _altCol, _altTop;

    public TextScreen(int rows, bool keepSgr)
    {
        _rows = Math.Max(1, rows);
        _keepSgr = keepSgr;
    }

    public void Resize(int rows)
    {
        rows = Math.Max(1, rows);
        // Growing pulls lines back from scrollback (like xterm.js); shrinking keeps the cursor on screen.
        if (rows > _rows) _top = Math.Max(0, _top - (rows - _rows));
        _rows = rows;
        if (_row >= _top + _rows) _top = _row - _rows + 1;
    }

    public void Feed(string data)
    {
        string s;
        if (_pending.Length > 0)
        {
            s = _pending.Append(data).ToString();
            _pending.Clear();
        }
        else s = data;

        var len = s.Length;
        var i = 0;
        while (i < len)
        {
            var c = s[i];
            if (c == '\x1b')
            {
                var consumed = Escape(s, i);
                if (consumed < 0)
                {
                    _pending.Append(s, i, len - i); // sequence continues in the next chunk
                    return;
                }
                i += consumed;
                continue;
            }
            i++;
            if (_alt) continue;
            switch (c)
            {
                case '\r': _col = 0; break;
                case '\n': LineFeed(); break;
                case '\b': if (_col > 0) _col--; break;
                case '\t': _col = (_col / 8 + 1) * 8; break;
                default:
                    if (c >= ' ' && c != '\x7f') Put(c);
                    break;
            }
        }
    }

    /// <summary>Handles the escape sequence at s[i]; returns its length, or -1 if it is incomplete.</summary>
    int Escape(string s, int i)
    {
        var len = s.Length;
        if (i + 1 >= len) return -1;
        var n = s[i + 1];
        switch (n)
        {
            case '[':
            {
                var j = i + 2;
                while (j < len && s[j] >= 0x30 && s[j] <= 0x3F) j++;
                var paramEnd = j;
                while (j < len && s[j] >= 0x20 && s[j] <= 0x2F) j++;
                if (j >= len) return -1;
                Csi(s.Substring(i + 2, paramEnd - i - 2), s[j]);
                return j - i + 1;
            }
            case ']' or 'P' or '_' or '^' or 'X':
            {
                // OSC / DCS / APC / PM / SOS: up to BEL or ST (ESC \).
                var j = i + 2;
                while (j < len && s[j] != '\x07' && !(s[j] == '\x1b' && j + 1 < len && s[j + 1] == '\\')) j++;
                if (j >= len || (s[j] == '\x1b' && j + 1 >= len)) return -1;
                return (s[j] == '\x07' ? j + 1 : j + 2) - i;
            }
            case '(' or ')' or '*' or '+' or '#' or '%':
                return i + 2 < len ? 3 : -1;
            case '7': _savedRow = _row - _top; _savedCol = _col; return 2;
            case '8': MoveTo(_top + _savedRow, _savedCol); return 2;
            case 'D': if (!_alt) LineFeed(); return 2;
            case 'E': if (!_alt) { LineFeed(); _col = 0; } return 2;
            case 'M': if (!_alt && _row > _top) _row--; return 2;
            default: return 2;
        }
    }

    void Csi(string p, char final)
    {
        var priv = p.Length > 0 && p[0] is '?' or '>' or '<' or '=';
        if (priv)
        {
            if (final is 'h' or 'l' && (p.Contains("1049") || p.Contains("1047") || p == "?47")) SetAlt(final == 'h');
            return;
        }
        if (_alt) return;

        int N(int index, int fallback)
        {
            var parts = p.Split(';');
            return index < parts.Length && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0
                ? v : fallback;
        }

        switch (final)
        {
            case 'm': Sgr(p); break;
            case 'H' or 'f': MoveTo(_top + N(0, 1) - 1, N(1, 1) - 1); break;
            case 'A': _row = Math.Max(_top, _row - N(0, 1)); break;
            case 'B': MoveTo(_row + N(0, 1), _col); break;
            case 'C': _col += N(0, 1); break;
            case 'D': _col = Math.Max(0, _col - N(0, 1)); break;
            case 'E': MoveTo(_row + N(0, 1), 0); break;
            case 'F': _row = Math.Max(_top, _row - N(0, 1)); _col = 0; break;
            case 'G' or '`': _col = N(0, 1) - 1; break;
            case 'd': MoveTo(_top + N(0, 1) - 1, _col); break;
            case 'K': EraseLine(p == "" ? 0 : N(0, 0)); break;
            case 'J': EraseDisplay(p == "" ? 0 : N(0, 0)); break;
            case 'X': Blank(_col, N(0, 1)); break;
            case 'P':
            {
                var line = Line(_row);
                if (_col < line.Count) line.RemoveRange(_col, Math.Min(N(0, 1), line.Count - _col));
                break;
            }
            case '@':
            {
                var line = Line(_row);
                if (_col < line.Count) line.InsertRange(_col, Blanks(N(0, 1)));
                break;
            }
            case 's': _savedRow = _row - _top; _savedCol = _col; break;
            case 'u': MoveTo(_top + _savedRow, _savedCol); break;
        }
    }

    void SetAlt(bool on)
    {
        if (on == _alt) return;
        if (on) { _altRow = _row; _altCol = _col; _altTop = _top; }
        else { _row = _altRow; _col = _altCol; _top = _altTop; }
        _alt = on;
    }

    void Sgr(string p)
    {
        if (!_keepSgr) return;
        if (p.Length == 0 || p == "0") _styleSeq = "";
        else if (p.StartsWith("0;", StringComparison.Ordinal)) _styleSeq = "\x1b[" + p + "m";
        else
        {
            _styleSeq += "\x1b[" + p + "m";
            if (_styleSeq.Length > 160) _styleSeq = _styleSeq[^160..][_styleSeq[^160..].IndexOf('\x1b')..];
        }
        if (!_styleIds.TryGetValue(_styleSeq, out _style))
        {
            _style = _styles.Count;
            _styles.Add(_styleSeq);
            _styleIds[_styleSeq] = _style;
        }
    }

    List<Cell> Line(int row)
    {
        while (_lines.Count <= row) _lines.Add(new List<Cell>());
        return _lines[row];
    }

    static IEnumerable<Cell> Blanks(int n)
    {
        for (var k = 0; k < n; k++) yield return new Cell { Ch = ' ' };
    }

    void MoveTo(int row, int col)
    {
        _row = Math.Clamp(row, _top, _top + _rows - 1);
        _col = Math.Max(0, col);
        Line(_row);
    }

    void LineFeed()
    {
        _row++;
        Line(_row);
        if (_row >= _top + _rows) _top = _row - _rows + 1;
    }

    void Put(char c)
    {
        var line = Line(_row);
        while (line.Count < _col) line.Add(new Cell { Ch = ' ' });
        var cell = new Cell { Ch = c, Style = _style };
        if (_col < line.Count) line[_col] = cell;
        else line.Add(cell);
        _col++;
    }

    void Blank(int from, int count)
    {
        var line = Line(_row);
        for (var k = from; k < Math.Min(line.Count, from + count); k++) line[k] = new Cell { Ch = ' ' };
    }

    void EraseLine(int mode)
    {
        var line = Line(_row);
        switch (mode)
        {
            case 0: if (_col < line.Count) line.RemoveRange(_col, line.Count - _col); break;
            case 1: Blank(0, _col + 1); break;
            case 2: line.Clear(); break;
        }
    }

    void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                if (_row + 1 < _lines.Count) _lines.RemoveRange(_row + 1, _lines.Count - _row - 1);
                break;
            case 1:
                for (var r = _top; r < _row; r++) Line(r).Clear();
                Blank(0, _col + 1);
                break;
            case 2:
            {
                // Clear screen: keep what was on it in the log, as scrollback, and start a fresh screen below.
                var cursor = _row - _top;
                while (_lines.Count > 0 && _lines[^1].Count == 0) _lines.RemoveAt(_lines.Count - 1);
                _top = _lines.Count;
                _row = _top + cursor;
                Line(_row);
                break;
            }
            // 3 = clear scrollback: ignored, the log keeps everything.
        }
    }

    /// <summary>The text of every line (scrollback + screen), trailing spaces trimmed, runs of blank lines collapsed.</summary>
    public string Result()
    {
        var sb = new StringBuilder();
        var blank = 0;
        var started = false;
        foreach (var line in _lines)
        {
            var end = line.Count;
            while (end > 0 && line[end - 1].Ch == ' ') end--;
            if (end == 0)
            {
                if (started) blank++;
                continue;
            }
            if (started) sb.Append('\n', Math.Min(blank, 1) + 1);
            started = true;
            blank = 0;

            var style = 0;
            for (var k = 0; k < end; k++)
            {
                var cell = line[k];
                if (cell.Style != style)
                {
                    if (style != 0) sb.Append("\x1b[0m");
                    sb.Append(_styles[cell.Style]);
                    style = cell.Style;
                }
                sb.Append(cell.Ch);
            }
            if (style != 0) sb.Append("\x1b[0m");
        }
        return sb.ToString();
    }
}
