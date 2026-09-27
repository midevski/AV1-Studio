using System.IO;
using System.Text.RegularExpressions;

namespace AV1Studio.Services;

/// <summary>One detected device and what it can actually do with AV1 (from real tests, not model names).</summary>
public sealed record DeviceCapability(
    string Name,
    string Kind,              // "CPU", "NVIDIA GPU", "Intel GPU", "AMD GPU", "GPU"
    string DecodeText,        // e.g. "✓ Supported (NVDEC)" / "✕ Not supported"
    bool DecodeOk,
    string EncodeText,        // e.g. "✓ Supported (av1_nvenc)" / "✕ Not supported"
    bool EncodeOk,
    string Note);

/// <summary>
/// Hardware capability detection. Every claim is backed by an actual FFmpeg run on this machine:
/// * AV1 encode: 1-frame test encode per encoder (libsvtav1, av1_nvenc, av1_qsv, av1_amf)
/// * AV1 decode: decode of a tiny generated AV1 clip with each hardware path (NVDEC, QSV, D3D11VA)
/// Hardware decoding and hardware encoding are reported separately: e.g. an NVIDIA RTX 30-series GPU decodes
/// AV1 in hardware but has no AV1 hardware encoder.
/// </summary>
public static class HardwareCapabilities
{
    public static readonly (string Id, string Name, string Vendor)[] AllHardwareEncoders =
    [
        ("av1_nvenc", "NVIDIA NVENC AV1", "NVIDIA"),
        ("av1_qsv", "Intel Quick Sync AV1", "Intel"),
        ("av1_amf", "AMD AMF AV1", "AMD"),
    ];

    public static async Task<bool> TestEncodeAsync(ToolStatus st, string encoderList, string id, CancellationToken ct)
    {
        if (!Regex.IsMatch(encoderList, $@"\s{Regex.Escape(id)}\s")) return false;
        try
        {
            var (code, _, _) = await ChildProcess.RunCaptureAsync(st.FfmpegPath!,
            [
                "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:r=24:d=0.2",
                "-frames:v", "1", "-c:v", id, "-f", "null", "-",
            ], ct, TimeSpan.FromSeconds(30));
            return code == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    public static async Task DetectAsync(ToolStatus st, string encoderList, CancellationToken ct)
    {
        if (st.FfmpegPath is null) return;

        // ---- Manual-mode encoders (each proven by a real test encode) with explanations ----
        void Report(string id, string name, bool hw, bool inBuild, bool ok)
        {
            string reason = ok ? "Available (test encode succeeded)"
                : !inBuild ? $"The required FFmpeg encoder ({id}) is not included in this FFmpeg build."
                : hw ? "Your GPU or its driver does not support AV1 hardware encoding with this encoder (a test encode failed)."
                : "The encoder is present but a test encode failed.";
            st.EncoderAvailability.Add(new EncoderAvailability(id, name, hw, ok, reason));
            if (ok) st.ManualEncoders.Add(id);
        }
        Report("libsvtav1", "SVT-AV1 (CPU)", false, st.FfmpegHasSvtAv1, st.FfmpegHasSvtAv1);
        bool aomInBuild = Regex.IsMatch(encoderList, @"\slibaom-av1\s");
        Report("libaom-av1", "libaom-av1 (CPU)", false, aomInBuild, aomInBuild && await TestEncodeAsync(st, encoderList, "libaom-av1", ct));
        foreach (var (id, name, _) in AllHardwareEncoders)
        {
            bool inBuild = Regex.IsMatch(encoderList, $@"\s{Regex.Escape(id)}\s");
            bool ok = st.HardwareAv1Encoders.Contains(id) || (id == "av1_amf" && await TestEncodeAsync(st, encoderList, id, ct));
            Report(id, name, true, inBuild, ok);
        }

        // ---- pixel formats per usable encoder ----
        foreach (var id in st.ManualEncoders)
        {
            try
            {
                var (_, help, _) = await ChildProcess.RunCaptureAsync(st.FfmpegPath, ["-hide_banner", "-h", $"encoder={id}"], ct);
                var m = Regex.Match(help, @"Supported pixel formats:\s*(.+)");
                if (m.Success) st.EncoderPixelFormats[id] = m.Groups[1].Value.Trim();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        // ---- AV1 decoding ----
        string decoders = "";
        try { decoders = (await ChildProcess.RunCaptureAsync(st.FfmpegPath, ["-hide_banner", "-decoders"], ct)).Stdout; }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        st.FfmpegHasDav1d = Regex.IsMatch(decoders, @"\slibdav1d\s");
        if (st.FfmpegHasDav1d) st.Av1Decoders.Add(("libdav1d", "dav1d (CPU software decoder)", false));

        var sample = Path.Combine(AppPaths.Temp, "av1-capability-test.mkv");
        if (!await MakeAv1SampleAsync(st, sample, ct)) return;
        try
        {
            var tests = new List<(string Id, string Label, string[] Args)>();
            if (Regex.IsMatch(decoders, @"\sav1_cuvid\s"))
                tests.Add(("av1_cuvid", "NVIDIA NVDEC", ["-c:v", "av1_cuvid", "-i", sample, "-f", "null", "-"]));
            if (Regex.IsMatch(decoders, @"\sav1_qsv\s"))
                tests.Add(("av1_qsv", "Intel Quick Sync", ["-c:v", "av1_qsv", "-i", sample, "-f", "null", "-"]));
            if (Regex.IsMatch(decoders, @"\sav1_amf\s"))
                tests.Add(("av1_amf", "AMD AMF", ["-c:v", "av1_amf", "-i", sample, "-f", "null", "-"]));
            // Generic DirectX Video Acceleration: frames must stay in GPU memory, so a software fallback fails.
            tests.Add(("d3d11va", "D3D11VA (DirectX, default adapter)",
                ["-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11", "-i", sample, "-vf", "hwdownload,format=nv12", "-f", "null", "-"]));

            foreach (var (id, label, args) in tests)
            {
                try
                {
                    var (code, _, _) = await ChildProcess.RunCaptureAsync(st.FfmpegPath,
                        ["-hide_banner", "-nostdin", "-v", "error", .. args], ct, TimeSpan.FromSeconds(30));
                    if (code == 0) st.Av1Decoders.Add((id, label, true));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        }
        finally
        {
            try { File.Delete(sample); } catch { }
        }
    }

    private static async Task<bool> MakeAv1SampleAsync(ToolStatus st, string path, CancellationToken ct)
    {
        if (!st.FfmpegHasSvtAv1) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var (code, _, _) = await ChildProcess.RunCaptureAsync(st.FfmpegPath!,
            [
                "-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=320x240:r=24:d=0.5",
                "-c:v", "libsvtav1", "-preset", "12", "-pix_fmt", "yuv420p", path,
            ], ct, TimeSpan.FromSeconds(60));
            return code == 0 && File.Exists(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>Per-device summary for the CPU &amp; Hardware page.</summary>
    public static List<DeviceCapability> Describe(SystemInfo sys, ToolStatus st)
    {
        var list = new List<DeviceCapability>();
        bool dav1d = st.Av1Decoders.Any(d => d.Id == "libdav1d");
        list.Add(new DeviceCapability(sys.Cpu, "CPU",
            dav1d ? "✓ Supported (dav1d, software)" : "? Unknown (no dav1d in this FFmpeg)", dav1d,
            st.ManualEncoders.Contains("libsvtav1") ? "✓ Supported (SVT-AV1, software)" : "✕ SVT-AV1 missing from FFmpeg",
            st.ManualEncoders.Contains("libsvtav1"),
            $"{sys.LogicalCores} threads"));

        bool d3d = st.Av1Decoders.Any(d => d.Id == "d3d11va");
        foreach (var g in sys.Gpus)
        {
            string? decId = g.Vendor switch { "NVIDIA" => "av1_cuvid", "Intel" => "av1_qsv", "AMD" => "av1_amf", _ => null };
            string? encId = g.Vendor switch { "NVIDIA" => "av1_nvenc", "Intel" => "av1_qsv", "AMD" => "av1_amf", _ => null };
            bool decOk = decId != null && st.Av1Decoders.Any(d => d.Id == decId);
            string decText = decOk
                ? $"✓ Supported ({st.Av1Decoders.First(d => d.Id == decId).Label})"
                : d3d && (decId is null || sys.Gpus.Count == 1)
                    ? "✓ Supported (D3D11VA)"
                    : "✕ Not supported / not detected";
            bool encOk = encId != null && st.ManualEncoders.Contains(encId);
            string encText = encOk ? $"✓ Supported ({encId})" : "✕ Not supported";
            list.Add(new DeviceCapability(g.Name, g.Vendor is "NVIDIA" or "Intel" or "AMD" ? $"{g.Vendor} GPU" : "GPU",
                decText, decOk || decText.StartsWith("✓"), encText, encOk,
                encOk ? "Hardware AV1 encoding available" : "Use SVT-AV1 (CPU) for AV1 encoding"));
        }
        return list;
    }

    public static string Recommendation(ToolStatus st)
    {
        var hw = st.EncoderAvailability.Where(e => e.IsHardware && e.Available).Select(e => e.Name).ToList();
        if (!st.ManualEncoders.Contains("libsvtav1"))
            return hw.Count > 0
                ? $"SVT-AV1 is not available in this FFmpeg build. Available: {string.Join(", ", hw)} (hardware). Install a full FFmpeg build for CPU encoding."
                : "No AV1 encoder is available yet — install FFmpeg with libsvtav1 (Settings › Tools).";
        var text = "Recommended AV1 encoder: SVT-AV1 (CPU) — typically the most compression-efficient option available here, " +
                   "at the cost of encoding time and CPU load.";
        if (hw.Count > 0)
            text += $" For speed or low CPU usage: {string.Join(", ", hw)} (hardware).";
        else
            text += " No hardware AV1 encoder was detected on this machine.";
        return text;
    }
}
