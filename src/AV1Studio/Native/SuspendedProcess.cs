using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AV1Studio.Native;

/// <summary>
/// Starts a child process SUSPENDED with its priority class set at creation, lets the caller put it into a job
/// object, then resumes it. The process therefore never runs a single instruction outside the job: every
/// FFmpeg / encoder process it spawns inherits the job's priority, affinity and kill-on-close limits.
/// No shell is involved; arguments are quoted per argument with the CommandLineToArgvW rules.
/// Only the three standard handles are inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so pipes of processes
/// started concurrently can never leak into each other.
/// </summary>
internal sealed class SuspendedProcess : IDisposable
{
    public Process Process { get; }
    public Stream StandardOutput { get; }
    public Stream StandardError { get; }
    private readonly SafeProcessHandle _handle;

    private SuspendedProcess(Process p, SafeProcessHandle handle, Stream stdout, Stream stderr)
    {
        Process = p;
        _handle = handle;
        StandardOutput = stdout;
        StandardError = stderr;
    }

    public void Dispose()
    {
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
        _handle.Dispose();
    }

    /// <param name="beforeResume">Called with the suspended process handle (e.g. to assign it to a job object).</param>
    public static SuspendedProcess Start(string exe, string commandLine, string workingDirectory,
        IDictionary<string, string>? environment, ProcessPriorityClass? priority, Action<IntPtr>? beforeResume)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null, inRead = null, inWrite = null;
        IntPtr attrList = IntPtr.Zero, handleArray = IntPtr.Zero, envBlock = IntPtr.Zero;
        var pi = new PROCESS_INFORMATION();
        try
        {
            if (!CreatePipe(out outRead, out outWrite, ref sa, 0) || !CreatePipe(out errRead, out errWrite, ref sa, 0)
                || !CreatePipe(out inRead, out inWrite, ref sa, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // our ends must not be inherited
            SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0);
            SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0);
            SetHandleInformation(inWrite, HANDLE_FLAG_INHERIT, 0);

            // restrict inheritance to exactly the three child-side pipe ends
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            handleArray = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleArray, 0, inRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handleArray, IntPtr.Size, outWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handleArray, IntPtr.Size * 2, errWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleArray,
                    (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = inRead.DangerousGetHandle();
            si.StartupInfo.hStdOutput = outWrite.DangerousGetHandle();
            si.StartupInfo.hStdError = errWrite.DangerousGetHandle();
            si.lpAttributeList = attrList;

            envBlock = BuildEnvironment(environment);
            uint flags = CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT
                         | PriorityFlag(priority);
            var cmd = new StringBuilder(commandLine);
            if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, true, flags, envBlock, workingDirectory, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var handle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            Process process;
            try
            {
                beforeResume?.Invoke(pi.hProcess);
                // The Process object is obtained while the process cannot have exited yet (it is suspended),
                // and our own handle keeps the process id from being reused.
                process = Process.GetProcessById(pi.dwProcessId);
                process.EnableRaisingEvents = true;
            }
            catch
            {
                TerminateProcess(pi.hProcess, 1);
                CloseHandle(pi.hThread);
                handle.Dispose();
                throw;
            }
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);

            // the child owns its ends now; stdin gets EOF immediately (tools must never wait for input)
            outWrite.Dispose(); errWrite.Dispose(); inRead.Dispose(); inWrite.Dispose();
            return new SuspendedProcess(process, handle,
                new FileStream(outRead, FileAccess.Read, 4096, false), new FileStream(errRead, FileAccess.Read, 4096, false));
        }
        catch
        {
            outRead?.Dispose(); errRead?.Dispose();
            outWrite?.Dispose(); errWrite?.Dispose(); inRead?.Dispose(); inWrite?.Dispose();
            throw;
        }
        finally
        {
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }
    }

    private static uint PriorityFlag(ProcessPriorityClass? p) => p switch
    {
        ProcessPriorityClass.Idle => IDLE_PRIORITY_CLASS,
        ProcessPriorityClass.BelowNormal => BELOW_NORMAL_PRIORITY_CLASS,
        ProcessPriorityClass.AboveNormal => ABOVE_NORMAL_PRIORITY_CLASS,
        ProcessPriorityClass.High => HIGH_PRIORITY_CLASS,
        _ => NORMAL_PRIORITY_CLASS, // Realtime is never used
    };

    /// <summary>Current environment plus overrides, as a sorted Unicode environment block.</summary>
    private static IntPtr BuildEnvironment(IDictionary<string, string>? overrides)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        if (overrides != null) foreach (var (k, v) in overrides) env[k] = v;
        var sb = new StringBuilder();
        foreach (var (k, v) in env) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    // ------------------------------------------------------------------ interop

    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint IDLE_PRIORITY_CLASS = 0x00000040;
    private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    private const uint HIGH_PRIORITY_CLASS = 0x00000080;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES sa, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeHandle h, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmdLine, IntPtr processAttrs, IntPtr threadAttrs,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}
