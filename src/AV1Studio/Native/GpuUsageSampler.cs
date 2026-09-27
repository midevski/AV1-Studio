using System.Runtime.InteropServices;

namespace AV1Studio.Native;

/// <summary>
/// Measures GPU video-engine utilisation of specific processes using Windows' own
/// "\GPU Engine(*)\Utilization Percentage" performance counters (PDH), filtered by PID and by
/// encode/codec engines. Only real measurements are reported: null means "not available".
/// </summary>
public sealed class GpuUsageSampler : IDisposable
{
    private IntPtr _query;
    private IntPtr _counter;
    private bool _primed;

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? src, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

    public static GpuUsageSampler? TryCreate()
    {
        try
        {
            var s = new GpuUsageSampler();
            if (PdhOpenQuery(null, IntPtr.Zero, out s._query) != 0) return null;
            if (PdhAddEnglishCounter(s._query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out s._counter) != 0)
            {
                s.Dispose();
                return null;
            }
            return s;
        }
        catch { return null; } // pdh.dll missing / counters disabled
    }

    /// <summary>Sum of encode/codec engine utilisation (%) for the given PIDs, or null when unmeasurable.</summary>
    public double? Sample(IReadOnlyCollection<int> pids)
    {
        if (_query == IntPtr.Zero || pids.Count == 0) return null;
        if (PdhCollectQueryData(_query) != 0) return null;
        if (!_primed) { _primed = true; return null; } // rate counter: needs two samples

        uint size = 0;
        uint rc = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
        if (rc != PDH_MORE_DATA || size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out uint count, buf) != 0) return null;
            // PDH_FMT_COUNTERVALUE_ITEM_W { LPWSTR szName; { DWORD CStatus; double value } } = 24 bytes on x64
            int itemSize = IntPtr.Size + 16;
            var prefixes = pids.Select(p => $"pid_{p}_").ToArray();
            double total = 0;
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                var item = buf + i * itemSize;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                if (!prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                if (!(name.Contains("Encode", StringComparison.OrdinalIgnoreCase) || name.Contains("Codec", StringComparison.OrdinalIgnoreCase))) continue;
                uint status = (uint)Marshal.ReadInt32(item + IntPtr.Size);
                if (status != 0) continue;
                total += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
                any = true;
            }
            return any ? Math.Clamp(total, 0, 100) : 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}
