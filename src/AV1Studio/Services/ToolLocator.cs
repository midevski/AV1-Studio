using System.IO;
using System.Text.RegularExpressions;
using AV1Studio.Models;

namespace AV1Studio.Services;

public sealed record EncoderAvailability(string Id, string Name, bool IsHardware, bool Available, string Reason);

public sealed class ToolStatus
{
    public string? AbAv1Path { get; set; }
    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }

    public string? AbAv1Version { get; set; }
    public Version? AbAv1SemVer { get; set; }
    public string? FfmpegVersion { get; set; }
    public string? SvtAv1Version { get; set; }
    public Version? SvtAv1SemVer { get; set; }

    // Capabilities detected from `--help` output, so the GUI adapts to the installed ab-av1.
    public bool CrfSearchJson { get; set; }
    public bool EncodeVerify { get; set; }
    public bool EncodeFailFast { get; set; }
    public bool HasTempDirArg { get; set; }
    public bool FfmpegHasSvtAv1 { get; set; }
    public bool FfmpegHasLibVmaf { get; set; }
    /// <summary>GPU AV1 encoders usable by ab-av1 (NVENC/QSV) that passed a real 1-frame test encode.</summary>
    public List<string> HardwareAv1Encoders { get; } = new();

    /// <summary>Every AV1 encoder usable in Manual mode on this machine (libsvtav1 + working GPU encoders incl. AMF).</summary>
    public List<string> ManualEncoders { get; } = new();

    /// <summary>AV1 decoders that actually decoded a test clip (id → label). Hardware DECODE ≠ hardware ENCODE.</summary>
    public List<(string Id, string Label, bool Hardware)> Av1Decoders { get; } = new();

    /// <summary>Encoder → "Supported pixel formats" from ffmpeg -h encoder=….</summary>
    public Dictionary<string, string> EncoderPixelFormats { get; } = new();

    /// <summary>Every known AV1 encoder with its availability and, if unavailable, the reason.</summary>
    public List<EncoderAvailability> EncoderAvailability { get; } = new();

    public string? FfprobeVersion { get; set; }

    public bool FfmpegHasZscale { get; set; }
    public bool FfmpegHasDav1d { get; set; }
    /// <summary>A real test proved this FFmpeg can write AV1 video into an MP4 file.</summary>
    public bool CanWriteAv1Mp4 { get; set; }

    public bool HardwareAvailable(string encoder) => HardwareAv1Encoders.Contains(encoder);

    public List<string> Problems { get; } = new();
    public List<string> Notes { get; } = new();

    public bool Ready => AbAv1Path != null && FfmpegPath != null && FfprobePath != null
                         && FfmpegHasSvtAv1 && FfmpegHasLibVmaf;

    /// <summary>ab-av1 ≥ 0.11 defaults svt-av1 to 0.25 CRF steps which need SVT-AV1 ≥ 4.0.</summary>
    public bool NeedsIntegerCrfIncrement =>
        AbAv1SemVer is { } a && a >= new Version(0, 11, 0) && SvtAv1SemVer is { } s && s.Major < 4;

    public string Summary =>
        Ready
            ? $"ab-av1 {AbAv1Version} · FFmpeg {FfmpegVersion} · SVT-AV1 {SvtAv1Version ?? "?"}"
            : "Tools missing — open Settings › Tools";
}

/// <summary>Status of one dependency for the ✓/✕ list (text always states the state; colour is secondary).</summary>
public sealed record DependencyRow(string Name, bool Ok, string Status)
{
    public static IReadOnlyList<DependencyRow> From(ToolStatus t)
    {
        static DependencyRow R(string name, bool ok, string okText, string missing) =>
            new(name, ok, ok ? "✓ " + okText : "✕ " + missing);
        return
        [
            R("FFmpeg", t.FfmpegPath != null, $"FFmpeg {t.FfmpegVersion}", "Not found — Install or Configure"),
            R("FFprobe", t.FfprobePath != null, $"FFprobe {t.FfprobeVersion}", "Not found — Install or Configure"),
            R("ab-av1", t.AbAv1Path != null, $"ab-av1 {t.AbAv1Version}", "Not found — needed for AB-AV1 mode only"),
            R("SVT-AV1", t.FfmpegHasSvtAv1, $"libsvtav1 {t.SvtAv1Version ?? ""}".Trim(), t.FfmpegPath == null ? "Needs FFmpeg" : "This FFmpeg build has no libsvtav1 — use a full build"),
            R("libvmaf", t.FfmpegHasLibVmaf, "available (VMAF measurement)", t.FfmpegPath == null ? "Needs FFmpeg" : "This FFmpeg build has no libvmaf — AB-AV1 mode cannot measure quality"),
            R("GPU AV1 encode", t.HardwareAv1Encoders.Count > 0 || t.ManualEncoders.Any(e => e != "libsvtav1" && e != "libaom-av1"),
                string.Join(", ", t.ManualEncoders.Where(e => e != "libsvtav1" && e != "libaom-av1")), "None available (optional) — CPU encoding is used"),
            R("AV1 decode", t.Av1Decoders.Count > 0, string.Join(", ", t.Av1Decoders.Select(d => d.Label)), "No AV1 decoder found in FFmpeg"),
        ];
    }
}

/// <summary>Finds ab-av1 / ffmpeg / ffprobe and probes their versions & capabilities.</summary>
public static class ToolLocator
{
    public static async Task<ToolStatus> DetectAsync(AppSettings s, CancellationToken ct = default)
    {
        var st = new ToolStatus
        {
            AbAv1Path = Find(s.AbAv1Path, "ab-av1.exe"),
            FfmpegPath = Find(s.FfmpegPath, "ffmpeg.exe"),
        };
        st.FfprobePath = Find(s.FfprobePath, "ffprobe.exe",
            st.FfmpegPath is null ? null : Path.GetDirectoryName(st.FfmpegPath));

        if (st.AbAv1Path is null) st.Problems.Add("ab-av1.exe not found.");
        if (st.FfmpegPath is null) st.Problems.Add("ffmpeg.exe not found.");
        if (st.FfprobePath is null) st.Problems.Add("ffprobe.exe not found.");
        if (st.FfprobePath != null)
        {
            try
            {
                var (_, pv, _) = await ChildProcess.RunCaptureAsync(st.FfprobePath, ["-hide_banner", "-version"], ct);
                var m = Regex.Match(pv, @"ffprobe version (\S+)");
                st.FfprobeVersion = m.Success ? m.Groups[1].Value : "unknown";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { st.Problems.Add($"FFprobe failed to run: {ex.Message}"); }
        }

        var env = ChildEnvironment(st);

        if (st.FfmpegPath != null)
        {
            try
            {
                var (_, so, _) = await ChildProcess.RunCaptureAsync(st.FfmpegPath, ["-hide_banner", "-version"], ct);
                var m = Regex.Match(so, @"ffmpeg version (\S+)");
                st.FfmpegVersion = m.Success ? m.Groups[1].Value : "unknown";

                var (_, enc, _) = await ChildProcess.RunCaptureAsync(st.FfmpegPath, ["-hide_banner", "-encoders"], ct);
                st.FfmpegHasSvtAv1 = Regex.IsMatch(enc, @"\slibsvtav1\s");
                var (_, flt, _) = await ChildProcess.RunCaptureAsync(st.FfmpegPath, ["-hide_banner", "-filters"], ct);
                st.FfmpegHasLibVmaf = Regex.IsMatch(flt, @"\slibvmaf\s");

                if (!st.FfmpegHasSvtAv1) st.Problems.Add("This FFmpeg build has no libsvtav1 encoder. Use a 'full'/'gpl' build.");
                if (!st.FfmpegHasLibVmaf) st.Problems.Add("This FFmpeg build has no libvmaf filter (required for CRF search).");

                if (st.FfmpegHasSvtAv1) await DetectSvtVersion(st, ct);
                if (st.FfmpegHasSvtAv1) await DetectAv1Mp4(st, ct);
                st.FfmpegHasZscale = Regex.IsMatch(flt, @"\szscale\s");
                await DetectHardwareEncoders(st, enc, ct);
                await HardwareCapabilities.DetectAsync(st, enc, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                st.Problems.Add($"FFmpeg failed to run: {ex.Message}");
            }
        }

        if (st.AbAv1Path != null)
        {
            try
            {
                var (_, so, se) = await ChildProcess.RunCaptureAsync(st.AbAv1Path, ["--version"], ct, env: env);
                var m = Regex.Match(so + se, @"ab-av1\s+v?(\d+\.\d+\.\d+\S*)");
                st.AbAv1Version = m.Success ? m.Groups[1].Value : (so + se).Trim();
                if (m.Success && Version.TryParse(Regex.Match(m.Groups[1].Value, @"^\d+\.\d+\.\d+").Value, out var v))
                    st.AbAv1SemVer = v;

                var (_, searchHelp, _) = await ChildProcess.RunCaptureAsync(st.AbAv1Path, ["crf-search", "--help"], ct, env: env);
                var (_, encodeHelp, _) = await ChildProcess.RunCaptureAsync(st.AbAv1Path, ["encode", "--help"], ct, env: env);
                st.CrfSearchJson = searchHelp.Contains("--stdout-format");
                st.HasTempDirArg = searchHelp.Contains("--temp-dir");
                st.EncodeVerify = encodeHelp.Contains("--verify");
                st.EncodeFailFast = encodeHelp.Contains("--fail-fast");

                if (!st.CrfSearchJson)
                    st.Notes.Add("This ab-av1 has no JSON output (added in 0.11.5); falling back to parsing its text output. Updating is recommended.");
                if (!st.EncodeVerify)
                    st.Notes.Add("This ab-av1 has no --verify (added in 0.11.7); the GUI runs its own decode verification instead.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                st.Problems.Add($"ab-av1 failed to run: {ex.Message}");
            }
        }

        if (st.NeedsIntegerCrfIncrement)
            st.Notes.Add($"SVT-AV1 {st.SvtAv1Version} < 4.0: CRF search will use --crf-increment 1 (quarter steps need SVT-AV1 4.0+).");

        return st;
    }

    private static async Task DetectSvtVersion(ToolStatus st, CancellationToken ct)
    {
        // SVT-AV1 prints its library version when an encoder instance is created.
        try
        {
            var (_, so, se) = await ChildProcess.RunCaptureAsync(st.FfmpegPath!,
            [
                "-hide_banner", "-nostdin", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1:d=1",
                "-frames:v", "1", "-c:v", "libsvtav1", "-f", "null", "-",
            ], ct, TimeSpan.FromSeconds(30));
            var m = Regex.Match(so + se, @"SVT-AV1 Encoder Lib\s+v?(\d+)\.(\d+)\.(\d+)");
            if (m.Success)
            {
                st.SvtAv1Version = $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}";
                st.SvtAv1SemVer = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* informational only */ }
    }

    /// <summary>Writes one AV1 frame into a real MP4 file and reads it back: MP4 output is only offered when this works.</summary>
    private static async Task DetectAv1Mp4(ToolStatus st, CancellationToken ct)
    {
        var file = Path.Combine(AppPaths.Temp, $"av1-mp4-test-{Guid.NewGuid():N}.mp4");
        try
        {
            Directory.CreateDirectory(AppPaths.Temp);
            var (code, _, _) = await ChildProcess.RunCaptureAsync(st.FfmpegPath!,
            [
                "-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1:d=1",
                "-frames:v", "1", "-c:v", "libsvtav1", "-f", "mp4", "-movflags", "+faststart", file,
            ], ct, TimeSpan.FromSeconds(30));
            if (code == 0 && File.Exists(file) && st.FfprobePath != null)
            {
                var probe = await FfprobeService.ProbeAsync(st.FfprobePath, file, ct);
                st.CanWriteAv1Mp4 = probe.MainVideo?.Codec == "av1" && OutputContainers.Matches(OutputContainers.Mp4, probe, out _);
            }
            if (!st.CanWriteAv1Mp4) st.Notes.Add("This FFmpeg build cannot write AV1 video into MP4 files — MP4 output is unavailable (MKV works).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            st.Notes.Add($"MP4 output could not be tested: {ex.Message}");
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>Encoders ab-av1 can drive with a CRF-like quality option (nvenc: -cq, qsv: -global_quality).
    /// av1_amf is not offered: ab-av1 would pass -crf, which AMF does not support.</summary>
    public static readonly (string Id, string Name)[] SupportedHardwareEncoders =
    [
        ("av1_nvenc", "NVIDIA NVENC (RTX 40 series and newer)"),
        ("av1_qsv", "Intel Quick Sync (Arc / Core Ultra)"),
    ];

    private static async Task DetectHardwareEncoders(ToolStatus st, string encoderList, CancellationToken ct)
    {
        // Being compiled into FFmpeg is not enough (e.g. RTX 30-series GPUs have NVENC but no AV1 NVENC):
        // a real 1-frame encode proves the GPU and driver support it.
        foreach (var (id, _) in SupportedHardwareEncoders)
        {
            if (await HardwareCapabilities.TestEncodeAsync(st, encoderList, id, ct)) st.HardwareAv1Encoders.Add(id);
        }
    }

    /// <summary>ab-av1 invokes "ffmpeg"/"ffprobe" from PATH, so the chosen FFmpeg folder is
    /// prepended to the child's PATH (the user's global PATH is never modified).</summary>
    public static Dictionary<string, string> ChildEnvironment(ToolStatus st)
    {
        var env = new Dictionary<string, string>();
        var dirs = new List<string>();
        if (st.FfmpegPath != null) dirs.Add(Path.GetDirectoryName(st.FfmpegPath)!);
        if (st.FfprobePath != null) dirs.Add(Path.GetDirectoryName(st.FfprobePath)!);
        var path = string.Join(';', dirs.Distinct(StringComparer.OrdinalIgnoreCase));
        env["PATH"] = path + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
        // ab-av1 only logs progress to stderr when it is not a terminal; make sure logs stay at info.
        env["RUST_LOG"] = "ab_av1=info";
        env["NO_COLOR"] = "1";
        return env;
    }

    private static string? Find(string configured, string exeName, string? preferDir = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)) return Path.GetFullPath(configured);
            // e.g. settings copied from another PC: fall back to automatic detection instead of failing
            Log.Warn($"{exeName} was configured at a location that does not exist on this PC; searching automatically.");
        }

        var candidates = new List<string>();
        if (preferDir != null) candidates.Add(preferDir);
        candidates.Add(AppContext.BaseDirectory);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools"));
        candidates.Add(AppPaths.Tools);
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
        candidates.Add(Path.Combine(user, "scoop", "shims"));
        var choco = Environment.GetEnvironmentVariable("ChocolateyInstall");
        candidates.Add(Path.Combine(string.IsNullOrWhiteSpace(choco)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey") : choco, "bin"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"));
        // common manual install location "<system drive>\ffmpeg\bin"
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (!string.IsNullOrEmpty(systemRoot)) candidates.Add(Path.Combine(systemRoot, "ffmpeg", "bin"));
        candidates.Add(Path.Combine(user, ".cargo", "bin"));

        foreach (var dir in candidates)
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(p)) return Path.GetFullPath(p);
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
