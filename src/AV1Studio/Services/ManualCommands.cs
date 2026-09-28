using System.Globalization;
using System.Text.RegularExpressions;
using AV1Studio.Models;
using AV1Studio.Util;

namespace AV1Studio.Services;

/// <summary>Static description of a Manual-mode AV1 encoder: what its quality knob is called, its range,
/// presets and how FFmpeg is told to use it. Option names verified against `ffmpeg -h encoder=…`.</summary>
public sealed record EncoderSpec(
    string Id,
    string Name,
    bool IsHardware,
    string QualityName,
    double QualityMin,
    double QualityMax,
    double DefaultQuality,
    IReadOnlyList<(string Value, string Label)> Presets,
    string DefaultPreset,
    string Pix10,
    string Pix8,
    string QualityHelp);

/// <summary>
/// Builds the FFmpeg command for Manual AV1 mode (ab-av1 is not involved). Returned as an argument
/// list and executed via ProcessStartInfo.ArgumentList — never through a shell.
/// </summary>
public static class ManualCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly IReadOnlyList<EncoderSpec> Encoders =
    [
        new("libsvtav1", "SVT-AV1 — CPU / software", false, "CRF", 0, 63, 30,
            [.. Enumerable.Range(0, 14).Select(i => (i.ToString(), $"{i} — {SvtPresetText(i)}"))], "5",
            "yuv420p10le", "yuv420p",
            "Constant Rate Factor. Lower generally means higher quality and larger files; higher means smaller files and lower quality. " +
            "There is no universally optimal value — it depends on the source and your preferences. Use Preview to judge."),
        new("libaom-av1", "libaom-av1 — CPU / software (reference encoder)", false, "CRF", 0, 63, 30,
            [.. Enumerable.Range(0, 9).Select(i => (i.ToString(), $"cpu-used {i}{(i == 0 ? " — slowest" : i == 8 ? " — fastest" : i == 4 ? " — balanced" : "")}"))], "6",
            "yuv420p10le", "yuv420p",
            "Constant quality (-crf with -b:v 0). Lower generally means higher quality and larger files. libaom is usually much slower than SVT-AV1 at similar settings."),
        new("av1_nvenc", "NVIDIA NVENC AV1 — hardware", true, "CQ", 1, 63, 30,
            [("p1", "p1 — fastest"), ("p2", "p2"), ("p3", "p3"), ("p4", "p4 — medium (default)"), ("p5", "p5 — slow"), ("p6", "p6"), ("p7", "p7 — slowest / most efficient")], "p5",
            "p010le", "nv12",
            "Constant-quality level for NVENC (VBR with -cq, no bitrate cap). Lower generally means higher quality and larger files."),
        new("av1_qsv", "Intel Quick Sync AV1 — hardware", true, "ICQ", 1, 51, 28,
            [("veryfast", "veryfast"), ("faster", "faster"), ("fast", "fast"), ("medium", "medium (default)"), ("slow", "slow"), ("slower", "slower"), ("veryslow", "veryslow")], "medium",
            "p010le", "nv12",
            "Intelligent constant quality (-global_quality). Lower generally means higher quality and larger files."),
        new("av1_amf", "AMD AMF AV1 — hardware (experimental)", true, "QP", 0, 255, 120,
            [("speed", "speed"), ("balanced", "balanced"), ("quality", "quality"), ("high_quality", "high quality")], "quality",
            "p010le", "nv12",
            "Constant QP (0–255, AV1 scale). Lower means higher quality and larger files. Untested by the developer on AMD hardware."),
    ];

    public static EncoderSpec Spec(string id) => Encoders.FirstOrDefault(e => e.Id == id) ?? Encoders[0];

    public static string SvtPresetText(int p) => p switch
    {
        <= 1 => "very slow, highest efficiency",
        <= 3 => "slow, very high efficiency",
        <= 5 => "balanced (slower side)",
        <= 7 => "balanced / faster",
        <= 9 => "fast",
        <= 11 => "very fast",
        _ => "fastest, lowest efficiency",
    };

    // ======================================================================== validation

    public static List<string> Validate(ManualSettings m, ToolStatus tools)
    {
        var errors = new List<string>();
        var spec = Spec(m.Encoder);
        if (!tools.ManualEncoders.Contains(m.Encoder))
            errors.Add($"The encoder \"{m.Encoder}\" is not available on this PC (not in FFmpeg, or the hardware test encode failed). Choose another encoder.");
        if (m.Quality < spec.QualityMin || m.Quality > spec.QualityMax)
            errors.Add($"{spec.QualityName} must be between {spec.QualityMin} and {spec.QualityMax} for {spec.Name}.");
        if (!spec.Presets.Any(p => p.Value == m.Preset))
            errors.Add($"Preset \"{m.Preset}\" is not valid for {spec.Name}.");
        if (!string.IsNullOrWhiteSpace(m.Crop) && !Regex.IsMatch(m.Crop.Trim(), @"^\d+:\d+:\d+:\d+$"))
            errors.Add("Crop must be w:h:x:y (e.g. 1920:800:0:140).");
        if (!string.IsNullOrWhiteSpace(m.Fps) && !Regex.IsMatch(m.Fps.Trim(), @"^\d+(\.\d+)?(/\d+(\.\d+)?)?$"))
            errors.Add("Frame rate must be a number or ratio (e.g. 24 or 24000/1001).");
        if (!string.IsNullOrWhiteSpace(m.Keyint) && !Regex.IsMatch(m.Keyint.Trim(), @"^\d+s?$"))
            errors.Add("Keyframe interval must be frames (240) or seconds (10s).");
        if (m.Resolution == ResolutionMode.Custom && (m.CustomWidth < 16 || m.CustomHeight < 16 || m.CustomWidth % 2 != 0 || m.CustomHeight % 2 != 0))
            errors.Add("Custom resolution needs an even width and height of at least 16.");
        if (!string.IsNullOrWhiteSpace(m.AudioBitrate) && !Regex.IsMatch(m.AudioBitrate.Trim(), @"^\d+[kKmM]?$"))
            errors.Add("Audio bitrate must look like 160k.");
        if (m.FilmGrain is < 0 or > 50) errors.Add("Film grain must be 0–50.");
        if (m.FastDecode is < 0 or > 2) errors.Add("Fast decode must be 0–2.");
        if (m.TileColumns is < 0 or > 6 || m.TileRows is < 0 or > 6) errors.Add("Tiles are log2 values 0–6.");
        if (!string.IsNullOrWhiteSpace(m.MaxBitrate) && ParseBitrateKbps(m.MaxBitrate) is null)
            errors.Add("Maximum bitrate must look like 12M or 8000k.");
        if (m.HwBFrames is < 0 or > 7) errors.Add("B-frames must be 0–7.");
        if (m.HwLookahead is < 0 or > 100) errors.Add("Lookahead must be 0–100 frames.");
        if (m.Hdr == HdrHandling.ToneMapToSdr && !tools.FfmpegHasZscale)
            errors.Add("HDR → SDR tone mapping needs an FFmpeg build with the zscale filter.");
        var svtErrors = new List<string>();
        AbAv1Commands.ParseSvtParams(m.SvtParams, svtErrors);
        errors.AddRange(svtErrors.Where(e => !e.Contains("controlled by ab-av1"))); // crf/preset handled below
        foreach (var bad in ParseExtraArgs(m.ExtraFfmpegArgs).Where(a => ReservedFfmpeg.Contains(a.Name)))
            errors.Add($"FFmpeg option \"-{bad.Name}\" is managed by the Manual AV1 settings; use the dedicated control.");
        return errors;
    }

    private static readonly HashSet<string> ReservedFfmpeg = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "y", "n", "f", "c", "codec", "c:v", "vcodec", "crf", "cq", "global_quality", "qp_i", "qp_p", "preset",
        "pix_fmt", "vf", "filter:v", "svtav1-params", "progress", "map",
    };

    internal static List<(string Name, string? Value)> ParseExtraArgs(string text) =>
        (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l =>
            {
                var a = l.TrimStart('-');
                int eq = a.IndexOf('=');
                return eq < 0 ? (a, (string?)null) : (a[..eq], a[(eq + 1)..]);
            }).ToList();

    // ======================================================================== warnings

    /// <summary>Warnings about potentially destructive or lossy choices (shown before encoding).</summary>
    public static List<string> Warnings(ManualSettings m, ProbeInfo probe)
    {
        var w = new List<string>();
        var spec = Spec(m.Encoder);
        if (probe.IsHdr)
        {
            if (m.Hdr == HdrHandling.ToneMapToSdr)
                w.Add("HDR → SDR tone mapping converts the colours and permanently discards the HDR information.");
            else if (m.BitDepth == BitDepth.Bit8)
                w.Add("The source is HDR but 8-bit output is selected: this may cause banding and can discard HDR information. 10-bit is recommended for HDR.");
            if (probe.MainVideo?.DolbyVision == true)
                w.Add("Dolby Vision dynamic metadata cannot be carried over by re-encoding; the HDR10/HLG base layer information is kept.");
            if (spec.Id != "libsvtav1" && m.Hdr == HdrHandling.Preserve && (probe.MainVideo?.MasteringDisplay != null || probe.MainVideo?.ContentLight != null))
                w.Add($"{spec.Name} keeps HDR colour tags, but FFmpeg cannot pass HDR10 mastering-display / content-light metadata to it. SVT-AV1 preserves them.");
            if (!string.IsNullOrWhiteSpace(m.ColorTransfer) || !string.IsNullOrWhiteSpace(m.ColorPrimaries) || !string.IsNullOrWhiteSpace(m.ColorMatrix))
                w.Add("Colour tag overrides are active on an HDR source: wrong tags make players display colours incorrectly.");
        }
        if (m.Grayscale) w.Add("Grayscale removes all colour information.");
        if (m.Resolution == ResolutionMode.Custom) w.Add($"Custom resolution {m.CustomWidth}×{m.CustomHeight} may change the aspect ratio or upscale.");
        return w;
    }

    // ======================================================================== filters

    public static string? VideoFilter(ManualSettings m, ProbeInfo probe)
    {
        var f = new List<string>();
        if (m.Detelecine) f.Add("fieldmatch,bwdif=mode=send_frame:deint=interlaced,decimate");
        else if (m.Deinterlace != Deinterlace.Off)
            f.Add(m.Deinterlace == Deinterlace.Always ? "bwdif=mode=send_frame:deint=all" : "bwdif=mode=send_frame:deint=interlaced");
        if (!string.IsNullOrWhiteSpace(m.Crop)) f.Add("crop=" + m.Crop.Trim());
        if (m.Denoise != Denoise.Off)
            f.Add(m.Denoise switch
            {
                Denoise.Light => "hqdn3d=2:1.5:3:2.25",
                Denoise.Medium => "hqdn3d=4:3:6:4.5",
                _ => "hqdn3d=8:6:12:9",
            });
        if (m.Deblock) f.Add("deblock=filter=weak:block=4");

        int? srcH = probe.MainVideo?.Height;
        int? targetH = m.Resolution switch { ResolutionMode.P1080 => 1080, ResolutionMode.P1440 => 1440, ResolutionMode.P2160 => 2160, _ => null };
        if (targetH is int th && (srcH is null || srcH > th)) f.Add($"scale=-2:{th}:flags=lanczos"); // downscale only
        if (m.Resolution == ResolutionMode.Custom) f.Add($"scale={m.CustomWidth}:{m.CustomHeight}:flags=lanczos");
        if (!string.IsNullOrWhiteSpace(m.Fps)) f.Add("fps=" + m.Fps.Trim());

        if (m.Hdr == HdrHandling.ToneMapToSdr && probe.IsHdr)
            f.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        if (m.Grayscale) f.Add("hue=s=0");
        if (!string.IsNullOrWhiteSpace(m.CustomFilter)) f.Add(m.CustomFilter.Trim());
        return f.Count == 0 ? null : string.Join(",", f);
    }

    public static string FilterSummary(ManualSettings m)
    {
        var parts = new List<string>();
        if (m.Detelecine) parts.Add("detelecine");
        else if (m.Deinterlace != Deinterlace.Off) parts.Add($"deinterlace ({m.Deinterlace.ToString().ToLowerInvariant()})");
        if (!string.IsNullOrWhiteSpace(m.Crop)) parts.Add("crop");
        if (m.Denoise != Denoise.Off) parts.Add($"denoise ({m.Denoise.ToString().ToLowerInvariant()})");
        if (m.Deblock) parts.Add("deblock");
        if (m.Resolution != ResolutionMode.Source) parts.Add("resize");
        if (!string.IsNullOrWhiteSpace(m.Fps)) parts.Add("frame rate");
        if (m.Hdr == HdrHandling.ToneMapToSdr) parts.Add("HDR→SDR tone map");
        if (m.Grayscale) parts.Add("grayscale");
        if (!string.IsNullOrWhiteSpace(m.CustomFilter)) parts.Add("custom");
        return parts.Count == 0 ? "No processing" : string.Join(", ", parts);
    }

    // ======================================================================== command

    /// <param name="startSeconds">Preview: seek position (input option, fast seek).</param>
    /// <param name="durationSeconds">Preview: length to encode.</param>
    public static List<string> Build(ManualSettings m, ProbeInfo probe, string input, string output, string ext,
        StreamPlan streams, double quality, string? progressFile, bool failFast,
        double? startSeconds = null, double? durationSeconds = null)
    {
        var spec = Spec(m.Encoder);
        var main = probe.MainVideo ?? throw new InvalidOperationException("No video stream");
        int v = main.TypeIndex; // output video index == input video index (all video streams are mapped)
        var a = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-n" };
        if (failFast) a.Add("-xerror");
        // Preview: hybrid seek. A fast input seek lands on a keyframe up to 20 s early; the precise output seek
        // then trims every stream (including stream-copied audio) to the exact same point, keeping A/V in sync.
        var (inputSeek, outputSeek) = SplitSeek(startSeconds);
        if (inputSeek > 0) { a.Add("-ss"); a.Add(inputSeek.ToString("0.###", Inv)); }
        a.Add("-i"); a.Add(input);
        if (outputSeek > 0) { a.Add("-ss"); a.Add(outputSeek.ToString("0.###", Inv)); }
        if (durationSeconds is double dur) { a.Add("-t"); a.Add(dur.ToString("0.###", Inv)); }

        // ---- mapping: everything, then the stream plan removes/converts tracks ----
        a.Add("-map"); a.Add("0");
        a.Add("-c"); a.Add("copy");
        a.Add($"-c:v:{v}"); a.Add(spec.Id);

        // ---- video ----
        if (VideoFilter(m, probe) is string vf) { a.Add($"-filter:v:{v}"); a.Add(vf); }
        bool tenBit = m.BitDepth == BitDepth.Bit10;
        a.Add($"-pix_fmt:v:{v}"); a.Add(tenBit ? spec.Pix10 : spec.Pix8);

        string q = quality.ToString("0.##", Inv);
        switch (spec.Id)
        {
            case "libsvtav1":
                a.Add($"-crf:v:{v}"); a.Add(((int)Math.Round(quality)).ToString(Inv));
                a.Add($"-preset:v:{v}"); a.Add(m.Preset);
                break;
            case "libaom-av1":
                a.Add($"-crf:v:{v}"); a.Add(((int)Math.Round(quality)).ToString(Inv));
                a.Add($"-b:v:{v}"); a.Add("0");
                a.Add($"-cpu-used:v:{v}"); a.Add(m.Preset);
                a.Add($"-row-mt:v:{v}"); a.Add("1");
                break;
            case "av1_nvenc":
                a.Add($"-rc:v:{v}"); a.Add("vbr");
                a.Add($"-cq:v:{v}"); a.Add(q);
                a.Add($"-b:v:{v}"); a.Add("0");
                a.Add($"-preset:v:{v}"); a.Add(m.Preset);
                a.Add($"-tune:v:{v}"); a.Add("hq");
                break;
            case "av1_qsv":
                a.Add($"-global_quality:v:{v}"); a.Add(((int)Math.Round(quality)).ToString(Inv));
                a.Add($"-preset:v:{v}"); a.Add(m.Preset);
                break;
            case "av1_amf":
                a.Add($"-rc:v:{v}"); a.Add("cqp");
                a.Add($"-qp_i:v:{v}"); a.Add(((int)Math.Round(quality)).ToString(Inv));
                a.Add($"-qp_p:v:{v}"); a.Add(((int)Math.Round(quality)).ToString(Inv));
                a.Add($"-quality:v:{v}"); a.Add(m.Preset);
                break;
        }

        if (KeyintFrames(m, probe) is int g) { a.Add($"-g:v:{v}"); a.Add(g.ToString(Inv)); }

        // GPU encoder workload controls (only the options the encoder actually has)
        if (spec.Id is "av1_nvenc" or "av1_qsv" && m.HwBFrames is int bf) { a.Add($"-bf:v:{v}"); a.Add(bf.ToString(Inv)); }
        if (spec.Id == "av1_nvenc")
        {
            if (m.HwLookahead is int la) { a.Add($"-rc-lookahead:v:{v}"); a.Add(la.ToString(Inv)); }
            if (m.HwMultipass is "disabled" or "qres" or "fullres") { a.Add($"-multipass:v:{v}"); a.Add(m.HwMultipass); }
            if (m.HwSpatialAq) { a.Add($"-spatial-aq:v:{v}"); a.Add("1"); }
            if (m.HwTemporalAq) { a.Add($"-temporal-aq:v:{v}"); a.Add("1"); }
        }
        if (spec.Id == "av1_qsv" && m.HwLookahead is int qla)
        {
            a.Add($"-extbrc:v:{v}"); a.Add("1");
            a.Add($"-look_ahead_depth:v:{v}"); a.Add(qla.ToString(Inv));
        }
        // Optional bitrate cap (capped constant quality). SVT-AV1 uses its own mbr parameter below.
        if (ParseBitrateKbps(m.MaxBitrate) is int kbps && spec.Id != "libsvtav1" && spec.Id != "libaom-av1")
        {
            a.Add($"-maxrate:v:{v}"); a.Add($"{kbps}k");
            a.Add($"-bufsize:v:{v}"); a.Add($"{kbps * 2}k");
        }

        // ---- colour: keep the source's tags (or user overrides); HDR static metadata for SVT-AV1 ----
        bool toneMap = m.Hdr == HdrHandling.ToneMapToSdr && probe.IsHdr;
        string? prim = Pick(m.ColorPrimaries, toneMap ? "bt709" : main.ColorPrimaries);
        string? trc = Pick(m.ColorTransfer, toneMap ? "bt709" : main.ColorTransfer);
        string? mat = Pick(m.ColorMatrix, toneMap ? "bt709" : main.ColorSpace);
        string? rng = Pick(m.ColorRange, toneMap ? "tv" : main.ColorRange);
        if (prim != null) { a.Add($"-color_primaries:v:{v}"); a.Add(prim); }
        if (trc != null) { a.Add($"-color_trc:v:{v}"); a.Add(trc); }
        if (mat != null) { a.Add($"-colorspace:v:{v}"); a.Add(mat); }
        if (rng != null) { a.Add($"-color_range:v:{v}"); a.Add(rng); }

        if (spec.Id == "libsvtav1")
        {
            var p = new List<string>();
            if (quality != Math.Round(quality)) p.Add("crf=" + q); // quarter-step CRF (SVT-AV1 4.0+)
            if (m.Tune is int tune) p.Add($"tune={tune}");
            p.Add($"scd={(m.SceneDetection ? 1 : 0)}");
            if (m.FilmGrain > 0)
            {
                p.Add($"film-grain={m.FilmGrain}");
                p.Add($"film-grain-denoise={(m.FilmGrainDenoise ? 1 : 0)}");
            }
            if (m.FastDecode > 0) p.Add($"fast-decode={m.FastDecode}");
            if (m.TileColumns > 0) p.Add($"tile-columns={m.TileColumns}");
            if (m.TileRows > 0) p.Add($"tile-rows={m.TileRows}");
            if (m.Threads > 0) p.Add($"lp={m.Threads}");
            if (ParseBitrateKbps(m.MaxBitrate) is int mbr) p.Add($"mbr={mbr}");
            if (!toneMap && probe.IsHdr)
            {
                p.Add("enable-hdr=1");
                if (main.MasteringDisplay != null) p.Add("mastering-display=" + main.MasteringDisplay);
                if (main.ContentLight != null) p.Add("content-light=" + main.ContentLight);
            }
            p.AddRange(AbAv1Commands.ParseSvtParams(m.SvtParams)); // crf/preset/keyint/scd are rejected there
            a.Add($"-svtav1-params:v:{v}"); a.Add(string.Join(":", p));
        }
        else if (m.Threads > 0)
        {
            a.Add("-threads"); a.Add(m.Threads.ToString(Inv));
        }

        // ---- audio / subtitles / metadata (shared stream plan) ----
        if (streams.AudioCodec != null)
        {
            a.Add("-c:a"); a.Add(streams.AudioCodec);
            if (streams.Downmix) { a.Add("-ac"); a.Add("2"); }
        }
        a.AddRange(AbAv1Commands.ToFfmpegArgs(streams.EncArgs));

        // ---- container ----
        var container = OutputContainers.FromExtension(ext);
        if (container.Muxer is "matroska" or "webm") a.Add("-dn");
        a.AddRange(container.FfmpegArgs);
        a.Add("-max_muxing_queue_size"); a.Add("4096");

        foreach (var (name, value) in ParseExtraArgs(m.ExtraFfmpegArgs).Where(x => !ReservedFfmpeg.Contains(x.Name)))
        {
            a.Add("-" + name);
            if (value != null) a.Add(value);
        }

        if (progressFile != null) { a.Add("-progress"); a.Add(progressFile); }
        a.Add("-f"); a.Add(container.Muxer);
        a.Add(output);
        return a;
    }

    /// <summary>Split a seek position into a fast input seek and a precise output seek (preview only).</summary>
    public static (double Input, double Output) SplitSeek(double? start)
    {
        if (start is not double st || st <= 0) return (0, 0);
        double input = Math.Max(0, Math.Round(st - 20, 3));
        return (input, Math.Round(st - input, 3));
    }

    /// <summary>"12M" / "8000k" / "8000" → kbit/s.</summary>
    public static int? ParseBitrateKbps(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        var m = Regex.Match(t, @"^(\d+(?:\.\d+)?)([km]?)$");
        if (!m.Success) return null;
        double v = double.Parse(m.Groups[1].Value, Inv);
        return (int)Math.Round(m.Groups[2].Value switch { "m" => v * 1000, "k" => v, _ => v });
    }

    private static string? Pick(string overrideValue, string? source) =>
        !string.IsNullOrWhiteSpace(overrideValue) ? overrideValue.Trim()
        : string.IsNullOrWhiteSpace(source) || source == "unknown" ? null : source;

    private static int? KeyintFrames(ManualSettings m, ProbeInfo probe)
    {
        var k = (m.Keyint ?? "").Trim();
        if (k.Length == 0) return null;
        if (k.EndsWith('s'))
        {
            double fps = ParseFps(m.Fps) ?? probe.MainVideo?.FrameRate ?? 0;
            if (fps <= 0 || !double.TryParse(k[..^1], NumberStyles.Float, Inv, out var secs)) return null;
            return Math.Max(1, (int)Math.Round(fps * secs));
        }
        return int.TryParse(k, out var frames) && frames > 0 ? frames : null;
    }

    private static double? ParseFps(string fps)
    {
        if (string.IsNullOrWhiteSpace(fps)) return null;
        var parts = fps.Trim().Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, Inv, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, Inv, out var d) && d != 0) return n / d;
        return double.TryParse(fps, NumberStyles.Float, Inv, out var x) ? x : null;
    }

    public static string Describe(ManualSettings m) =>
        $"{Spec(m.Encoder).Name.Split(" — ")[0]} · {Spec(m.Encoder).QualityName} {Fmt.Num(m.Quality)} · preset {m.Preset} · {(m.BitDepth == BitDepth.Bit10 ? "10-bit" : "8-bit")}";
}
