using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static AV1Studio.Native.Win32;

namespace AV1Studio.Native;

/// <summary>
/// Windows Job Object wrapping one child process tree (ab-av1 + the ffmpeg processes it spawns).
/// * KILL_ON_JOB_CLOSE: if the GUI exits or crashes, the encoder tree is terminated too, so no
///   orphan keeps writing a half-finished file behind our back.
/// * Priority class limit applies to every process in the tree.
/// * Accounting lets the GUI show CPU/RAM usage of the whole tree cheaply.
/// </summary>
public sealed class JobObject : IDisposable
{
    private IntPtr _handle;
    internal nuint? EnforcerKey { get; set; }
    private long _lastCpu100ns;
    private DateTime _lastSample = DateTime.UtcNow;

    public JobObject(ProcessPriorityClass? priority, ulong? affinity = null)
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        var prio = priority is ProcessPriorityClass p && p != ProcessPriorityClass.RealTime ? p : (ProcessPriorityClass?)null;
        // A job priority limit above Normal needs SeIncreaseBasePriorityPrivilege (administrators only). In that case
        // the limit is not set on the job; instead every process is raised as soon as it joins (see PriorityEnforcer),
        // which needs no privilege. Kill-on-close and affinity always apply.
        bool jobLimit = prio is ProcessPriorityClass pc && pc != ProcessPriorityClass.High && pc != ProcessPriorityClass.AboveNormal;
        if (!TrySetLimits(jobLimit ? prio : null, affinity, out int error))
            throw new Win32Exception(error);
        if (prio is ProcessPriorityClass raised && !jobLimit)
            PriorityEnforcer.Watch(this, raised);
    }

    internal IntPtr Handle => _handle;

    private bool TrySetLimits(ProcessPriorityClass? priority, ulong? affinity, out int error)
    {
        error = 0;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (priority is ProcessPriorityClass p)
        {
            info.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_PRIORITY_CLASS;
            info.BasicLimitInformation.PriorityClass = (uint)p;
        }
        if (affinity is ulong mask && mask != 0)
        {
            info.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_AFFINITY;
            info.BasicLimitInformation.Affinity = (UIntPtr)mask;
        }
        int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)len)) return true;
            error = Marshal.GetLastWin32Error();
            return false;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Assign(Process process) => Assign(process.Handle);

    public void Assign(IntPtr processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Terminate()
    {
        if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1);
    }

    /// <summary>CPU usage of the whole tree since the previous call, as % of total machine capacity.</summary>
    public double? SampleCpuPercent()
    {
        if (_handle == IntPtr.Zero) return null;
        int len = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            if (!QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, ptr, (uint)len, out _)) return null;
            var acc = Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(ptr);
            long cpu = acc.TotalUserTime + acc.TotalKernelTime;
            var now = DateTime.UtcNow;
            double wall100ns = (now - _lastSample).Ticks;
            double? pct = null;
            if (_lastCpu100ns > 0 && wall100ns > 0)
                pct = Math.Clamp((cpu - _lastCpu100ns) / (wall100ns * Environment.ProcessorCount) * 100.0, 0, 100);
            _lastCpu100ns = cpu;
            _lastSample = now;
            return pct;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
    [System.Runtime.InteropServices.DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);

    /// <summary>PIDs of all live processes in the job (the tool and every child it spawned).</summary>
    public int[] ProcessIds()
    {
        if (_handle == IntPtr.Zero) return [];
        const int maxIds = 256;
        int len = 8 + IntPtr.Size * maxIds;
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            if (!QueryInformationJobObject(_handle, JobObjectBasicProcessIdList, ptr, (uint)len, out _)) return [];
            int count = Math.Min(Marshal.ReadInt32(ptr, 4), maxIds);
            var ids = new int[count];
            for (int i = 0; i < count; i++) ids[i] = (int)Marshal.ReadIntPtr(ptr, 8 + i * IntPtr.Size);
            return ids;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>Freeze (pause) every process of the tree without killing it.</summary>
    public void Suspend() => ForEachProcess(p => NtSuspendProcess(p.Handle));

    public void Resume() => ForEachProcess(p => NtResumeProcess(p.Handle));

    private void ForEachProcess(Action<Process> action)
    {
        foreach (var pid in ProcessIds())
        {
            try { using var p = Process.GetProcessById(pid); action(p); }
            catch { /* exited */ }
        }
    }

    /// <summary>Sum of private memory of all live processes in the job.</summary>
    public long? SampleMemoryBytes()
    {
        if (_handle == IntPtr.Zero) return null;
        long total = 0;
        ForEachProcess(p => total += p.PrivateMemorySize64);
        return total;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            PriorityEnforcer.Forget(this);
            CloseHandle(_handle); // KILL_ON_JOB_CLOSE ends anything still running
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Raises every process that joins a watched job to the requested priority class, the moment Windows reports it
/// (JOB_OBJECT_MSG_NEW_PROCESS on one shared I/O completion port). Used for High / Above normal, which a job
/// cannot enforce itself without administrator rights.
/// </summary>
internal static class PriorityEnforcer
{
    private const int JobObjectAssociateCompletionPortInformation = 7;
    private const uint JOB_OBJECT_MSG_NEW_PROCESS = 6;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nuint, uint> Watched = new();
    private static readonly object Gate = new();
    private static IntPtr _port;
    private static long _nextKey;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT { public IntPtr CompletionKey; public IntPtr CompletionPort; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateIoCompletionPort(IntPtr file, IntPtr existingPort, UIntPtr key, uint threads);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetQueuedCompletionStatus(IntPtr port, out uint bytes, out UIntPtr key, out IntPtr overlapped, uint timeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    public static void Watch(JobObject job, ProcessPriorityClass priority)
    {
        lock (Gate)
        {
            if (_port == IntPtr.Zero)
            {
                _port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
                if (_port == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                new Thread(Loop) { IsBackground = true, Name = "priority enforcer" }.Start();
            }
        }
        var key = (nuint)Interlocked.Increment(ref _nextKey);
        Watched[key] = (uint)priority;
        job.EnforcerKey = key;
        var info = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT { CompletionKey = (IntPtr)key, CompletionPort = _port };
        int len = Marshal.SizeOf<JOBOBJECT_ASSOCIATE_COMPLETION_PORT>();
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job.Handle, JobObjectAssociateCompletionPortInformation, ptr, (uint)len))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static void Forget(JobObject job)
    {
        if (job.EnforcerKey is nuint key) Watched.TryRemove(key, out _);
    }

    private static void Loop()
    {
        while (true)
        {
            if (!GetQueuedCompletionStatus(_port, out uint msg, out var key, out var data, uint.MaxValue)) continue;
            if (msg != JOB_OBJECT_MSG_NEW_PROCESS || !Watched.TryGetValue((nuint)key, out uint priority)) continue;
            var h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, (int)data);
            if (h == IntPtr.Zero) continue; // already exited
            SetPriorityClass(h, priority);
            CloseHandle(h);
        }
    }
}
