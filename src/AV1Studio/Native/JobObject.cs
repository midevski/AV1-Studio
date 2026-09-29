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
/// Keeps every process of a watched job at the requested priority class (High / Above normal, which a job cannot
/// enforce itself without administrator rights) for the whole lifetime of the job:
/// * each new process is raised the moment Windows reports it (JOB_OBJECT_MSG_NEW_PROCESS);
/// * because those notifications are not guaranteed, and Windows or other programs can lower a priority later
///   (e.g. efficiency mode), every process of every watched job is re-checked once per second and restored;
/// * Windows power throttling (EcoQoS) is switched off for the encoder processes;
/// * the enforcer thread runs at time-critical priority, so busy encoder threads can never starve it, and
///   AV1 Studio itself is raised to the same class while such jobs run, so the window stays responsive.
/// </summary>
internal static class PriorityEnforcer
{
    private const int JobObjectAssociateCompletionPortInformation = 7;
    private const uint JOB_OBJECT_MSG_NEW_PROCESS = 6;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint WAIT_TIMEOUT = 258;
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private sealed record Entry(JobObject Job, uint Priority)
    {
        /// <summary>Processes whose power throttling was already switched off (done once per process).</summary>
        public HashSet<int> Unthrottled { get; } = new();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nuint, Entry> Watched = new();
    private static readonly object Gate = new();
    private static IntPtr _port;
    private static long _nextKey;
    private static ProcessPriorityClass? _ownOriginal;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT { public IntPtr CompletionKey; public IntPtr CompletionPort; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateIoCompletionPort(IntPtr file, IntPtr existingPort, UIntPtr key, uint threads);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetQueuedCompletionStatus(IntPtr port, out uint bytes, out UIntPtr key, out IntPtr overlapped, uint timeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

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
                new Thread(Loop) { IsBackground = true, Name = "priority enforcer", Priority = ThreadPriority.Highest }.Start();
            }
        }
        var key = (nuint)Interlocked.Increment(ref _nextKey);
        Watched[key] = new Entry(job, (uint)priority);
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
        UpdateOwnPriority();
    }

    public static void Forget(JobObject job)
    {
        if (job.EnforcerKey is nuint key && Watched.TryRemove(key, out _)) UpdateOwnPriority();
    }

    /// <summary>AV1 Studio runs at the highest watched class while such jobs run (it is mostly idle), and returns
    /// to its original class afterwards.</summary>
    private static void UpdateOwnPriority()
    {
        lock (Gate)
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                if (Watched.IsEmpty)
                {
                    if (_ownOriginal is ProcessPriorityClass original) self.PriorityClass = original;
                    _ownOriginal = null;
                    return;
                }
                _ownOriginal ??= self.PriorityClass;
                var highest = Watched.Values.Max(e => Rank((ProcessPriorityClass)e.Priority));
                var target = highest >= Rank(ProcessPriorityClass.High) ? ProcessPriorityClass.High : ProcessPriorityClass.AboveNormal;
                if (Rank(target) > Rank(self.PriorityClass)) self.PriorityClass = target;
            }
            catch { /* cosmetic: only affects the window's responsiveness */ }
        }
    }

    private static int Rank(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.Idle => 0,
        ProcessPriorityClass.BelowNormal => 1,
        ProcessPriorityClass.Normal => 2,
        ProcessPriorityClass.AboveNormal => 3,
        ProcessPriorityClass.High => 4,
        _ => 5,
    };

    private static void Loop()
    {
        // time-critical within this process: always scheduled ahead of the encoder threads it supervises
        try { Thread.CurrentThread.Priority = ThreadPriority.Highest; SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL); } catch { }
        var nextSweep = DateTime.UtcNow + SweepInterval;
        while (true)
        {
            uint wait = (uint)Math.Max(0, (nextSweep - DateTime.UtcNow).TotalMilliseconds);
            if (GetQueuedCompletionStatus(_port, out uint msg, out var key, out var data, wait))
            {
                if (msg == JOB_OBJECT_MSG_NEW_PROCESS && Watched.TryGetValue((nuint)key, out var entry))
                    Enforce(entry, (int)data);
            }
            if (DateTime.UtcNow >= nextSweep)
            {
                foreach (var entry in Watched.Values)
                {
                    int[] ids;
                    try { ids = entry.Job.ProcessIds(); } catch { continue; }
                    foreach (var pid in ids) Enforce(entry, pid);
                    lock (entry.Unthrottled) entry.Unthrottled.IntersectWith(ids); // forget exited processes
                }
                nextSweep = DateTime.UtcNow + SweepInterval;
            }
        }
    }

    /// <summary>Restores the priority class if it differs, and switches power throttling off once.</summary>
    private static void Enforce(Entry entry, int pid)
    {
        var h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return; // already exited
        try
        {
            if (GetPriorityClass(h) != entry.Priority) SetPriorityClass(h, entry.Priority);
            bool first;
            lock (entry.Unthrottled) first = entry.Unthrottled.Add(pid);
            if (first)
            {
                // ControlMask = execution speed, StateMask = 0 → never throttle (no EcoQoS / efficiency mode)
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0,
                };
                SetProcessInformation(h, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            }
        }
        finally { CloseHandle(h); }
    }

    private const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);
}
