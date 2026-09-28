using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AV1Studio.Models;

namespace AV1Studio.Services;

/// <summary>How the encoder processes of one stage may use the CPU. Applied to the whole process tree
/// (ab-av1 + every FFmpeg / VMAF process it starts) through the Windows Job Object.</summary>
/// <param name="AffinityMask">Logical processors the tree may run on (null = all).</param>
/// <param name="Threads">Logical processors made available (for display and encoder thread hints).</param>
public sealed record ResourcePlan(ProcessPriorityClass Priority, ulong? AffinityMask, int Threads, int LogicalProcessors, string Description);

/// <summary>
/// Turns a CPU usage choice into a concrete plan for the detected CPU: the mode decides how many logical
/// processors the encoder may use (affinity), the priority is chosen separately. Both are enforced by Windows
/// for every process in the tree. Realtime priority is never used.
/// </summary>
public static class ResourcePlanner
{
    public static ResourcePlan Plan(CpuProfile p, int? logicalOverride = null)
    {
        int logical = Math.Max(1, logicalOverride ?? Environment.ProcessorCount);
        // Affinity masks address processor group 0 only (max 64 logical processors).
        bool canMask = logical <= 64;

        (int threads, string label) = p.Mode switch
        {
            CpuUsageMode.Maximum => (logical, "Maximum"),
            CpuUsageMode.Balanced => (Math.Max(1, (int)Math.Round(logical * 0.75)), "Balanced"),
            CpuUsageMode.Low => (Math.Max(1, logical / 2), "Low"),
            CpuUsageMode.Custom => (p.Threads <= 0 ? logical : Math.Clamp(p.Threads, 1, logical), "Custom"),
            _ => (logical - AutoReserve(logical), "Auto"),
        };
        var prio = ToClass(p.Priority);

        ulong? mask = null;
        if (canMask)
        {
            if (p.Mode == CpuUsageMode.Custom && TryParseAffinity(p.Affinity, logical, out var custom))
            {
                mask = custom;
                threads = System.Numerics.BitOperations.PopCount(custom);
            }
            else if (threads < logical)
            {
                // Use the highest-numbered logical processors: Windows prefers the low ones for interactive
                // work, so the desktop keeps some cores to itself.
                ulong m = 0;
                for (int i = logical - threads; i < logical; i++) m |= 1UL << i;
                mask = m;
            }
        }

        string desc = $"{label}: {threads} of {logical} logical processors, {PriorityText(prio)} priority" +
                      (!canMask && threads < logical ? " (more than 64 logical processors: thread limit not enforced)" : "");
        return new ResourcePlan(prio, mask, threads, logical, desc);
    }

    /// <summary>The priority that matches a mode; applied when the user switches modes (and still editable).</summary>
    public static ProcessPriority SuggestedPriority(CpuUsageMode mode) => mode switch
    {
        CpuUsageMode.Maximum => ProcessPriority.High,
        CpuUsageMode.Balanced => ProcessPriority.BelowNormal,
        CpuUsageMode.Low => ProcessPriority.Idle,
        _ => ProcessPriority.Normal,
    };

    /// <summary>Auto keeps a little CPU for Windows, the UI and background tasks; scales with the CPU size.</summary>
    public static int AutoReserve(int logical) => logical switch
    {
        <= 2 => 0,
        <= 8 => 1,
        _ => Math.Max(2, logical / 8),
    };

    public static ProcessPriorityClass ToClass(ProcessPriority p) => p switch
    {
        ProcessPriority.Idle => ProcessPriorityClass.Idle,
        ProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        ProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
        ProcessPriority.High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal, // Realtime is intentionally not representable
    };

    public static string PriorityText(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.Idle => "Low (idle)",
        ProcessPriorityClass.BelowNormal => "Below normal",
        ProcessPriorityClass.AboveNormal => "Above normal",
        ProcessPriorityClass.High => "High",
        _ => "Normal",
    };

    /// <summary>Parse "0-7,12,14" into a mask of logical processors (0-based). Invalid → false.</summary>
    public static bool TryParseAffinity(string? text, int logical, out ulong mask)
    {
        mask = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(part.Trim(), @"^(\d+)(?:-(\d+))?$");
            if (!m.Success) { mask = 0; return false; }
            int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : a;
            if (a > b || b >= Math.Min(logical, 64)) { mask = 0; return false; }
            for (int i = a; i <= b; i++) mask |= 1UL << i;
        }
        return mask != 0;
    }
}
