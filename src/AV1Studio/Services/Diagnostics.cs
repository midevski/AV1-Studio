using System.IO;
using System.Text;
using AV1Studio.Models;
using AV1Studio.Util;

namespace AV1Studio.Services;

/// <summary>
/// Builds a diagnostic report for bug reports. Personal data is removed: the Windows user name and profile
/// paths are replaced by placeholders, and library/destination paths are never included.
/// </summary>
public static class Diagnostics
{
    public static string Build(SystemInfo sys, ToolStatus t, AppSettings s)
    {
        var sb = new StringBuilder();
        void Line(string k, string? v) => sb.AppendLine($"{k,-28} {v}");

        sb.AppendLine($"=== {AppInfo.Name} diagnostic report ===");
        Line("AV1 Studio version", AppInfo.Version);
        Line("Generated (UTC)", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"));
        Line("Windows", sys.WindowsVersion);
        Line(".NET runtime", Environment.Version.ToString());
        sb.AppendLine();
        sb.AppendLine("--- Hardware ---");
        Line("CPU", $"{sys.Cpu} ({sys.CpuVendor}, {sys.Architecture})");
        Line("Logical processors", sys.LogicalCores.ToString());
        Line("RAM", Fmt.Bytes((long)sys.RamBytes));
        if (sys.Gpus.Count == 0) Line("GPU", "none detected");
        foreach (var g in sys.Gpus) Line("GPU", $"{g.Name} ({g.Vendor}, {(g.IsIntegrated ? "integrated" : "discrete")})");
        sb.AppendLine();
        sb.AppendLine("--- Tools ---");
        Line("FFmpeg", $"{t.FfmpegVersion ?? "not found"} [{Location(t.FfmpegPath)}]");
        Line("FFprobe", $"{t.FfprobeVersion ?? "not found"} [{Location(t.FfprobePath)}]");
        Line("ab-av1", $"{t.AbAv1Version ?? "not found"} [{Location(t.AbAv1Path)}]");
        Line("SVT-AV1 library", t.SvtAv1Version ?? (t.FfmpegHasSvtAv1 ? "present (version unknown)" : "not available"));
        Line("libvmaf", t.FfmpegHasLibVmaf ? "available" : "NOT available");
        Line("zscale (tone mapping)", t.FfmpegHasZscale ? "available" : "not available");
        Line("ab-av1 features", $"json={t.CrfSearchJson}, verify={t.EncodeVerify}, fail-fast={t.EncodeFailFast}");
        sb.AppendLine();
        sb.AppendLine("--- AV1 encoders ---");
        foreach (var e in t.EncoderAvailability)
            Line(e.Name, (e.Available ? "✓ " : "✕ ") + e.Reason);
        sb.AppendLine();
        sb.AppendLine("--- AV1 decoders (tested) ---");
        if (t.Av1Decoders.Count == 0) sb.AppendLine("  none detected");
        foreach (var d in t.Av1Decoders) Line(d.Label, d.Hardware ? "hardware" : "software");
        sb.AppendLine();
        sb.AppendLine("--- Relevant configuration ---");
        Line("Default mode", s.DefaultMode == EncodeMode.AbAv1 ? "AB-AV1" : "Manual AV1");
        Line("AB-AV1", $"VMAF {Fmt.Num(s.TargetVmaf)}, preset {s.Preset?.ToString() ?? "default"}, GPU {(s.HardwareEncoding ? s.HardwareEncoder : "off")}");
        Line("Manual AV1", ManualCommands.Describe(s.Manual));
        Line("Container / naming", $"{s.Container} / {s.Naming}, collisions: {s.Collision}");
        Line("Destination folder set", string.IsNullOrWhiteSpace(s.DestinationFolder) ? "no (next to source)" : "yes (path hidden)");
        Line("Mirror folder tree", s.MirrorFolderTree.ToString());
        Line("Delete source", $"{s.DeleteSourceAfterSuccess} ({s.DeleteMode})");
        Line("Verification", $"decode={s.AbAv1Verify}, fail-fast={s.FailFast}, track counts={s.VerifyStreamCounts}, tolerance={s.DurationToleranceSeconds}s, copy hash={s.VerifyCopiesWithHash}");
        Line("CPU (CRF search)", ResourcePlanner.Plan(s.SearchCpu).Description);
        Line("CPU (encoding)", ResourcePlanner.Plan(s.EncodeCpu).Description);
        Line("Concurrent jobs", s.ConcurrentJobs.ToString());
        Line("Theme", s.Theme.ToString());
        if (t.Problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- Problems ---");
            foreach (var p in t.Problems) sb.AppendLine("  " + p);
        }
        return Redact(sb.ToString());
    }

    /// <summary>Where a tool was found, without exposing personal paths.</summary>
    public static string Location(string? path)
    {
        if (path is null) return "missing";
        var dir = Path.GetDirectoryName(path) ?? "";
        if (dir.StartsWith(AppPaths.Tools, StringComparison.OrdinalIgnoreCase)) return "downloaded by AV1 Studio";
        if (dir.StartsWith(AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "next to AV1 Studio";
        var pathEnv = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
        if (pathEnv.Any(p => string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return "found on PATH";
        return "custom location";
    }

    /// <summary>Replace the user name and profile folders with placeholders.</summary>
    public static string Redact(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var user = Environment.UserName;
        if (user.Length >= 3) text = text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        var machine = Environment.MachineName;
        if (machine.Length >= 3) text = text.Replace(machine, "<computer>", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
