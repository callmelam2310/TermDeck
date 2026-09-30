using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TermDeck.Core;

/// <summary>
/// A live run: reads output from ConPTY, writes the .cast file and keeps a buffer so a view can re-attach later.
/// </summary>
public sealed class RunSession
{
    readonly object _lock = new();
    readonly StringBuilder _buffer = new();
    readonly Stopwatch _clock = new();
    CastWriter? _cast;
    PtyProcess? _pty;
    Action<RunSession, string>? _sink;
    Thread? _reader;

    public RunRecord Record { get; }
    public bool IsRunning { get; private set; }
    public event Action<RunSession>? Exited;

    public RunSession(RunRecord record) => Record = record;

    public void Start(LaunchSpec spec, string castPath, int cols, int rows, WinCredential? cred = null)
    {
        _cast = new CastWriter(castPath, cols, rows, spec.Display);
        _clock.Start();
        IsRunning = true;
        try
        {
            _pty = PtyProcess.Start(spec.CommandLine, spec.WorkingDir, cols, rows, cred);
        }
        catch (Exception ex)
        {
            Emit($"\x1b[31mFailed to start: {ex.Message}\x1b[0m\r\n\x1b[90m{spec.CommandLine}\x1b[0m\r\n");
            Finish(-1);
            return;
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"pty-read-{Record.Id}" };
        _reader.Start();
        Task.Run(WaitLoop);
    }

    void ReadLoop()
    {
        var bytes = new byte[16384];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        while (true)
        {
            int n;
            try { n = _pty!.Output.Read(bytes, 0, bytes.Length); }
            catch (Exception) { break; }
            if (n <= 0) break;
            var c = decoder.GetChars(bytes, 0, n, chars, 0);
            if (c > 0) Emit(new string(chars, 0, c));
        }
    }

    void WaitLoop()
    {
        var code = _pty!.WaitForExit();
        // Give conhost a moment to flush the last output before closing the pseudo console.
        Thread.Sleep(150);
        _pty.ClosePty();
        _reader?.Join(3000);
        Finish(code);
    }

    void Finish(int code)
    {
        var elapsed = _clock.Elapsed;
        var color = code == 0 ? "32" : "31";
        Emit($"\r\n\x1b[90m── finished · exit \x1b[{color}m{DescribeExit(code)}\x1b[90m · {FormatDuration(elapsed)} ──\x1b[0m\r\n");
        lock (_lock)
        {
            _cast?.Dispose();
            _cast = null;
            IsRunning = false;
            Record.EndedAt = DateTime.Now;
            Record.ExitCode = code;
        }
        _pty?.Dispose();
        Exited?.Invoke(this);
    }

    void Emit(string s)
    {
        Action<RunSession, string>? sink;
        lock (_lock)
        {
            _buffer.Append(s);
            _cast?.Output(_clock.Elapsed.TotalSeconds, s);
            sink = _sink;
        }
        sink?.Invoke(this, s);
    }

    /// <summary>Attaches a view: atomically returns the output so far; new output goes through the sink.</summary>
    public string Attach(Action<RunSession, string> sink)
    {
        lock (_lock)
        {
            _sink = sink;
            return _buffer.ToString();
        }
    }

    public void Detach()
    {
        lock (_lock) _sink = null;
    }

    public void WriteInput(string text)
    {
        if (!IsRunning || _pty == null) return;
        _pty.Write(Encoding.UTF8.GetBytes(text));
    }

    public void Resize(int cols, int rows)
    {
        if (IsRunning) _pty?.Resize(cols, rows);
    }

    /// <summary>Sends Ctrl+C first; if still running after 1.5s, kills the whole process tree.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        WriteInput("\x03");
        Task.Delay(1500).ContinueWith(_ => { if (IsRunning) _pty?.Kill(); });
    }

    public void Kill()
    {
        if (IsRunning) _pty?.Kill();
    }

    /// <summary>NTSTATUS (negative) exit codes are shown in hex, with a meaning for common ones.</summary>
    public static string DescribeExit(int? code) => code switch
    {
        null => "?",
        unchecked((int)0xC000013A) => "0xC000013A (Ctrl+C)",
        unchecked((int)0xC0000005) => "0xC0000005 (access violation)",
        unchecked((int)0xC0000135) => "0xC0000135 (missing DLL)",
        < -255 => $"0x{unchecked((uint)code.Value):X8}",
        _ => code.Value.ToString(),
    };

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m{t.Seconds:00}s"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s"
        : $"{t.TotalSeconds:0.0}s";
}
