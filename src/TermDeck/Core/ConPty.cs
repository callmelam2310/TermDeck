using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TermDeck.Core;

/// <summary>Credentials to launch a Windows tool under another account via CreateProcessWithLogonW.</summary>
public sealed record WinCredential(string Account, string Password)
{
    /// <summary>Splits "DOMAIN\user" / "user@domain" / "user" into (user, domain?) for CreateProcessWithLogonW.</summary>
    public (string User, string? Domain) Split()
    {
        var a = Account.Trim();
        var bs = a.IndexOf('\\');
        if (bs > 0) return (a[(bs + 1)..], a[..bs]);
        return (a, null); // local account or UPN (user@domain) — domain stays null
    }
}

/// <summary>
/// A process running inside a Windows Pseudo Console (ConPTY): keeps ANSI colors, progress bars, Ctrl+C and interactive stdin.
/// The process is assigned to a Job Object so Stop / app exit kills the whole process tree.
/// </summary>
public sealed class PtyProcess : IDisposable
{
    IntPtr _hpc;
    IntPtr _hProcess;
    IntPtr _job;
    readonly FileStream _input;
    readonly FileStream _output;
    int _ptyClosed;

    public int ProcessId { get; }
    public Stream Output => _output;

    PtyProcess(IntPtr hpc, IntPtr hProcess, IntPtr job, int pid, FileStream input, FileStream output)
    {
        _hpc = hpc;
        _hProcess = hProcess;
        _job = job;
        ProcessId = pid;
        _input = input;
        _output = output;
    }

    public static PtyProcess Start(string commandLine, string? workingDir, int cols, int rows, WinCredential? cred = null)
    {
        if (!Native.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input)");
        if (!Native.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output)");

        var size = new Native.COORD { X = (short)Math.Clamp(cols, 10, 1000), Y = (short)Math.Clamp(rows, 5, 1000) };
        var hr = Native.CreatePseudoConsole(size, inRead, outWrite, 0, out var hpc);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole");

        var attrSize = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        var attrList = Marshal.AllocHGlobal(attrSize);
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
            if (!Native.UpdateProcThreadAttribute(attrList, 0, Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

            var si = new Native.STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            // Empty std handles: stop the child from inheriting the app's stdout/stderr (when redirected)
            // and force it onto the pseudo console.
            si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            si.lpAttributeList = attrList;

            var cmd = (commandLine + "\0").ToCharArray();
            var cwd = string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir) ? null : workingDir;
            const uint flags = Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_SUSPENDED;
            bool ok;
            Native.PROCESS_INFORMATION pi;
            if (cred == null)
            {
                ok = Native.CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    flags, IntPtr.Zero, cwd, ref si, out pi);
            }
            else
            {
                // The child runs in a new logon session. It reaches the pseudo console through the attribute list
                // (not handle inheritance), so ConPTY keeps working; the environment is the target user's.
                var (user, domain) = cred.Split();
                ok = Native.CreateProcessWithLogonW(user, domain, cred.Password, Native.LOGON_WITH_PROFILE,
                    null, cmd, flags, IntPtr.Zero, cwd, ref si, out pi);
            }
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                Native.ClosePseudoConsole(hpc);
                inRead.Dispose(); inWrite.Dispose(); outRead.Dispose(); outWrite.Dispose();
                throw new Win32Exception(err);
            }

            var job = CreateKillOnCloseJob();
            // Assigning a run-as process (a different logon session) to the job can fail; if so, drop the job
            // so Kill() terminates the process handle directly instead of an empty job that kills nothing.
            if (job != IntPtr.Zero && !Native.AssignProcessToJobObject(job, pi.hProcess))
            {
                Native.CloseHandle(job);
                job = IntPtr.Zero;
            }
            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);

            // ConPTY holds its own copies of these ends.
            inRead.Dispose();
            outWrite.Dispose();

            return new PtyProcess(hpc, pi.hProcess, job, pi.dwProcessId,
                new FileStream(inWrite, FileAccess.Write, 1, false),
                new FileStream(outRead, FileAccess.Read, 1, false));
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    static IntPtr CreateKillOnCloseJob()
    {
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info,
            (uint)Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        return job;
    }

    public void Write(byte[] data)
    {
        try { _input.Write(data, 0, data.Length); _input.Flush(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Resize(int cols, int rows)
    {
        if (Volatile.Read(ref _ptyClosed) != 0) return;
        Native.ResizePseudoConsole(_hpc, new Native.COORD { X = (short)Math.Clamp(cols, 10, 1000), Y = (short)Math.Clamp(rows, 5, 1000) });
    }

    /// <summary>Blocks until the process exits and returns its exit code.</summary>
    public int WaitForExit()
    {
        Native.WaitForSingleObject(_hProcess, Native.INFINITE);
        return Native.GetExitCodeProcess(_hProcess, out var code) ? unchecked((int)code) : -1;
    }

    public void Kill()
    {
        if (_job != IntPtr.Zero) Native.TerminateJobObject(_job, 1);
        else Native.TerminateProcess(_hProcess, 1);
    }

    /// <summary>Closes the pseudo console so the output reader gets EOF. Only call while a reader is still draining.</summary>
    public void ClosePty()
    {
        if (Interlocked.Exchange(ref _ptyClosed, 1) == 0)
            Native.ClosePseudoConsole(_hpc);
    }

    public void Dispose()
    {
        ClosePty();
        try { _input.Dispose(); } catch { }
        try { _output.Dispose(); } catch { }
        if (_hProcess != IntPtr.Zero) { Native.CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
        if (_job != IntPtr.Zero) { Native.CloseHandle(_job); _job = IntPtr.Zero; }
    }
}

internal static class Native
{
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint LOGON_WITH_PROFILE = 0x00000001;
    public const uint INFINITE = 0xFFFFFFFF;
    public const int STARTF_USESTDHANDLES = 0x00000100;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    public const int JobObjectExtendedLimitInformation = 9;
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? lpApplicationName, char[] lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    // Runs the child in a new logon session for another account. No bInheritHandles parameter: the pseudo
    // console is passed through the STARTUPINFOEX attribute list, so ConPTY still attaches.
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessWithLogonW(string lpUsername, string? lpDomain, string lpPassword,
        uint dwLogonFlags, string? lpApplicationName, char[] lpCommandLine, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    public static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

    [DllImport("kernel32.dll")]
    public static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);

    [DllImport("kernel32.dll")]
    public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);

    [DllImport("kernel32.dll")]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    public static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);
}
