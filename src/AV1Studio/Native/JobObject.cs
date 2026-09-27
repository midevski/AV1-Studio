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
    private long _lastCpu100ns;
    private DateTime _lastSample = DateTime.UtcNow;

    public JobObject(ProcessPriorityClass? priority, ulong? affinity = null)
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (priority is ProcessPriorityClass p && p != ProcessPriorityClass.RealTime)
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
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)len))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
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
            CloseHandle(_handle); // KILL_ON_JOB_CLOSE ends anything still running
            _handle = IntPtr.Zero;
        }
    }
}
