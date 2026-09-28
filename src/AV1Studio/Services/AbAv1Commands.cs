using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AV1Studio.Models;
using AV1Studio.Util;

namespace AV1Studio.Services;

/// <summary>Audio / subtitle / metadata choices, taken from either the AB-AV1 or the Manual AV1 settings.</summary>
public sealed record TrackOptions
{
    public AudioMode AudioMode { get; init; }
    public string AudioCodec { get; init; } = "libopus";
    public string AudioBitrate { get; init; } = "";
    public bool DownmixToStereo { get; init; }
    public string AudioLanguages { get; init; } = "";
    public SubtitleMode SubtitleMode { get; init; }
    public string SubtitleLanguages { get; init; } = "";
    public bool ForcedSubtitlesOnly { get; init; }
    public string DefaultSubtitleLanguage { get; init; } = "";
    public bool KeepChapters { get; init; } = true;
    public bool KeepGlobalMetadata { get; init; } = true;
    public bool KeepAttachments { get; init; } = true;
    /// <summary>ab-av1 always encodes the first video stream; the Manual engine maps the main video itself.</summary>
    public bool RequireMainVideoFirst { get; init; } = true;

    public static TrackOptions FromAbAv1(AppSettings s) => new()
    {
        AudioMode = s.AudioMode, AudioCodec = s.AudioCodec, AudioBitrate = s.AudioBitrate, DownmixToStereo = s.DownmixToStereo,
        AudioLanguages = s.AudioLanguages, SubtitleMode = s.SubtitleMode, SubtitleLanguages = s.SubtitleLanguages,
        KeepChapters = s.KeepChapters, KeepGlobalMetadata = s.KeepGlobalMetadata, KeepAttachments = s.KeepAttachments,
        RequireMainVideoFirst = true,
    };

    public static TrackOptions FromManual(ManualSettings m) => new()
    {
        AudioMode = m.AudioMode, AudioCodec = m.AudioCodec, AudioBitrate = m.AudioBitrate, DownmixToStereo = m.DownmixToStereo,
        AudioLanguages = m.AudioLanguages, SubtitleMode = m.SubtitleMode, SubtitleLanguages = m.SubtitleLanguages,
        ForcedSubtitlesOnly = m.ForcedSubtitlesOnly, DefaultSubtitleLanguage = m.DefaultSubtitleLanguage,
        KeepChapters = m.KeepChapters, KeepGlobalMetadata = m.KeepGlobalMetadata, KeepAttachments = m.KeepAttachments,
        RequireMainVideoFirst = false,
    };
}

/// <summary>Stream handling decided for one file (which tracks survive, codecs to use).</summary>
public sealed class StreamPlan
{
    /// <summary>ab-av1 --acodec value (applies to all audio). Null = ab-av1 default ("copy").</summary>
    public string? AudioCodec { get; set; }
    public bool Downmix { get; set; }
    /// <summary>Extra "--enc" values (ffmpeg output options) — maps, per-stream codecs, etc.</summary>
    public List<string> EncArgs { get; } = new();
    public int ExpectedAudio { get; set; }
    public int ExpectedSubtitles { get; set; }
    public List<string> Warnings { get; } = new();
    /// <summary>Set when the file cannot be processed safely with these settings.</summary>
    public string? Blocker { get; set; }
}

/// <summary>
/// Builds ab-av1 argument lists. Only options documented in ab-av1's CLI are used
/// (see docs/CONFIGURATION.md for the mapping). Everything is returned as an argument
/// list — never as a shell string — and executed via ProcessStartInfo.ArgumentList.
/// </summary>
public static class AbAv1Commands
{
    /// <summary>svt-av1 keys ab-av1 rejects in --svt because it sets them itself.</summary>
    private static readonly string[] SvtDenied = ["crf", "preset", "keyint", "scd", "input-depth"];

    private static readonly Regex SvtToken = new(@"^[A-Za-z0-9][A-Za-z0-9_\-]*=[^\s:=]+$", RegexOptions.Compiled);
    private static readonly Regex EncToken = new(@"^-?[A-Za-z0-9][A-Za-z0-9_:\-]*(=.*)?$", RegexOptions.Compiled);

    // ffmpeg options ab-av1 already controls (mirrors ab-av1's reserved list) plus our own.
    private static readonly HashSet<string> EncReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "y", "n", "pix_fmt", "crf", "preset", "vf", "filter:v", "c:a", "codec:a", "acodec",
        "c:v", "c:v:0", "codec:v", "codec:v:0", "vcodec", "svtav1-params", "progress",
    };

    public static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text" };

    private static readonly HashSet<string> Mp4Audio = new(StringComparer.OrdinalIgnoreCase)
        { "aac", "ac3", "eac3", "mp3", "opus", "flac", "alac", "mp2" };

    private static readonly HashSet<string> WebmAudio = new(StringComparer.OrdinalIgnoreCase) { "opus", "vorbis" };

    // ------------------------------------------------------------------ validation

    public static List<string> ParseSvtParams(string text, List<string>? errors = null)
    {
        var result = new List<string>();
        foreach (var raw in Regex.Split(text ?? "", @"[\s:]+"))
        {
            var tok = raw.Trim().TrimStart('-');
            if (tok.Length == 0) continue;
            if (!SvtToken.IsMatch(tok)) { errors?.Add($"Invalid SVT-AV1 parameter \"{raw}\" (expected key=value)"); continue; }
            var key = tok[..tok.IndexOf('=')];
            if (SvtDenied.Any(d => key.Equals(d, StringComparison.OrdinalIgnoreCase)))
            {
                errors?.Add($"SVT-AV1 \"{key}\" is controlled by ab-av1 — use the dedicated setting instead");
                continue;
            }
            result.Add(tok);
        }
        return result;
    }

    public static List<string> ParseEncArgs(string text, List<string>? errors = null)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = raw.Trim();
            if (tok.Length == 0 || tok.StartsWith('#')) continue;
            if (!EncToken.IsMatch(tok)) { errors?.Add($"Invalid FFmpeg argument \"{tok}\" (expected name=value)"); continue; }
            var name = tok.TrimStart('-').Split('=')[0];
            if (EncReserved.Contains(name))
            {
                errors?.Add($"FFmpeg option \"-{name}\" is managed by ab-av1/this app — use the dedicated setting");
                continue;
            }
            result.Add(tok.TrimStart('-'));
        }
        return result;
    }

    public static List<string> Validate(AppSettings s)
    {
        var errors = new List<string>();
        ParseSvtParams(s.SvtParams, errors);
        ParseEncArgs(s.ExtraEncArgs, errors);
        ParseEncArgs(s.ExtraEncInputArgs, errors);
        if (s.TargetVmaf is < 50 or > 100) errors.Add("Target VMAF must be between 50 and 100.");
        if (s.MinCrf is double a && s.MaxCrf is double b && a >= b) errors.Add("Min CRF must be lower than max CRF.");
        if (s.Preset is int p && (p < 0 || p > 13)) errors.Add("SVT-AV1 preset must be 0–13.");
        if (!string.IsNullOrWhiteSpace(s.Crop) && !Regex.IsMatch(s.Crop.Trim(), @"^\d+:\d+:\d+:\d+$"))
            errors.Add("Crop must be w:h:x:y (e.g. 1920:800:0:140).");
        if (!string.IsNullOrWhiteSpace(s.Fps) && !Regex.IsMatch(s.Fps.Trim(), @"^\d+(\.\d+)?(/\d+(\.\d+)?)?$"))
            errors.Add("FPS must be a number or ratio (e.g. 24 or 24000/1001).");
        if (!string.IsNullOrWhiteSpace(s.Keyint) && !Regex.IsMatch(s.Keyint.Trim(), @"^\d+[a-z]*$"))
            errors.Add("Keyframe interval must be frames (240) or a duration (10s).");
        if (!string.IsNullOrWhiteSpace(s.AudioBitrate) && !Regex.IsMatch(s.AudioBitrate.Trim(), @"^\d+[kKmM]?$"))
            errors.Add("Audio bitrate must look like 160k.");
        if (s.ConcurrentJobs < 1 || s.ConcurrentJobs > 8) errors.Add("Concurrent jobs must be 1–8.");
        if (s.HardwareEncoding)
        {
            if (!ToolLocator.SupportedHardwareEncoders.Any(e => e.Id == s.HardwareEncoder))
                errors.Add($"Unsupported hardware encoder \"{s.HardwareEncoder}\".");
            var hp = (s.HardwarePreset ?? "").Trim();
            if (hp.Length > 0 && s.HardwareEncoder == "av1_nvenc" && !Regex.IsMatch(hp, "^p[1-7]$"))
                errors.Add("NVENC preset must be p1 (fastest) … p7 (best quality), or empty.");
            if (hp.Length > 0 && s.HardwareEncoder == "av1_qsv" && !HardwarePresets("av1_qsv").Any(x => x.Value == hp))
                errors.Add("Quick Sync preset must be veryfast, faster, fast, medium, slow, slower or veryslow, or empty.");
        }
        return errors;
    }

    /// <summary>Preset choices for a GPU encoder ("" = encoder default).</summary>
    public static IReadOnlyList<(string Value, string Label)> HardwarePresets(string encoder) => encoder switch
    {
        "av1_qsv" =>
        [
            ("", "Encoder default"), ("veryfast", "veryfast"), ("faster", "faster"), ("fast", "fast"),
            ("medium", "medium"), ("slow", "slow"), ("slower", "slower"), ("veryslow", "veryslow (best quality)"),
        ],
        _ =>
        [
            ("", "Encoder default (p4)"), ("p1", "p1 — fastest"), ("p2", "p2 — faster"), ("p3", "p3 — fast"),
            ("p4", "p4 — medium"), ("p5", "p5 — slow (good quality)"), ("p6", "p6 — slower"), ("p7", "p7 — slowest (best quality)"),
        ],
    };

    public static string EncoderDescription(AppSettings s) => !s.HardwareEncoding
        ? "SVT-AV1 (CPU)"
        : (ToolLocator.SupportedHardwareEncoders.FirstOrDefault(e => e.Id == s.HardwareEncoder).Name ?? s.HardwareEncoder) + " — GPU";

    // ------------------------------------------------------------------ video filter

    public static string? BuildVideoFilter(AppSettings s, ProbeInfo? probe)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Crop)) filters.Add("crop=" + s.Crop.Trim());
        int? srcH = probe?.MainVideo?.Height;
        if (s.MaxHeight > 0 && (srcH is null || srcH > s.MaxHeight))
            filters.Add($"scale=-2:{s.MaxHeight}:flags=lanczos"); // downscale only, keep aspect, even width
        if (!string.IsNullOrWhiteSpace(s.Fps)) filters.Add("fps=" + s.Fps.Trim());
        if (!string.IsNullOrWhiteSpace(s.CustomVideoFilter)) filters.Add(s.CustomVideoFilter.Trim());
        return filters.Count == 0 ? null : string.Join(",", filters);
    }

    // ------------------------------------------------------------------ shared encode args

    /// <summary>ab-av1 "Encode" args shared by crf-search and encode (so the searched CRF is
    /// valid for the final encode). Input is added by the caller.</summary>
    public static List<string> SharedEncodeArgs(AppSettings s, ProbeInfo? probe)
    {
        var a = new List<string>();
        if (s.HardwareEncoding)
        {
            // GPU AV1 encoder driven by ab-av1 (-e). SVT-AV1-only options (--svt, --scd, lp) do not apply.
            a.Add("-e"); a.Add(s.HardwareEncoder);
            if (!string.IsNullOrWhiteSpace(s.HardwarePreset)) { a.Add("--preset"); a.Add(s.HardwarePreset.Trim()); }
            // ab-av1 sets no pixel format for GPU encoders; keep 10-bit like the SVT-AV1 default
            // (ffmpeg maps yuv420p10le to the GPU's p010le).
            a.Add("--pix-format"); a.Add(string.IsNullOrWhiteSpace(s.PixelFormat) ? "yuv420p10le" : s.PixelFormat.Trim());
        }
        else
        {
            // Default: SVT-AV1 on the CPU (ab-av1's default encoder, so no -e is passed).
            if (s.Preset is int p) { a.Add("--preset"); a.Add(p.ToString()); }
            if (!string.IsNullOrWhiteSpace(s.PixelFormat)) { a.Add("--pix-format"); a.Add(s.PixelFormat.Trim()); }
        }
        if (BuildVideoFilter(s, probe) is string vf) { a.Add("--vfilter"); a.Add(vf); }
        if (!string.IsNullOrWhiteSpace(s.Keyint)) { a.Add("--keyint"); a.Add(s.Keyint.Trim()); }
        if (!s.HardwareEncoding)
        {
            if (s.Scd != ScdMode.Default) { a.Add("--scd"); a.Add(s.Scd == ScdMode.On ? "true" : "false"); }
            foreach (var svt in ParseSvtParams(s.SvtParams)) { a.Add("--svt"); a.Add(svt); }
            if (s.EncoderThreads > 0) { a.Add("--svt"); a.Add($"lp={s.EncoderThreads}"); }
        }
        foreach (var e in ParseEncArgs(s.ExtraEncInputArgs)) { a.Add("--enc-input"); a.Add(e); }
        foreach (var e in ParseEncArgs(s.ExtraEncArgs)) { a.Add("--enc"); a.Add(e); }
        return a;
    }

    // ------------------------------------------------------------------ crf-search

    public static List<string> CrfSearch(AppSettings s, ToolStatus tools, ProbeInfo? probe, string input,
        string tempDir)
    {
        var a = new List<string> { "crf-search", "-i", input };
        a.AddRange(SharedEncodeArgs(s, probe));
        a.Add("--min-vmaf"); a.Add(Fmt.Arg(s.TargetVmaf));
        AddSearchArgs(a, s, tools);
        if (tools.HasTempDirArg) { a.Add("--temp-dir"); a.Add(tempDir); }
        if (tools.CrfSearchJson) { a.Add("--stdout-format"); a.Add("json"); }
        return a;
    }

    private static void AddSearchArgs(List<string> a, AppSettings s, ToolStatus tools)
    {
        if (s.MaxEncodedPercent is double mep) { a.Add("--max-encoded-percent"); a.Add(Fmt.Arg(mep)); }
        if (s.MinCrf is double mn) { a.Add("--min-crf"); a.Add(Fmt.Arg(mn)); }
        if (s.MaxCrf is double mx) { a.Add("--max-crf"); a.Add(Fmt.Arg(mx)); }
        if (s.CrfIncrement is double inc) { a.Add("--crf-increment"); a.Add(Fmt.Arg(inc)); }
        else if (tools.NeedsIntegerCrfIncrement && !s.HardwareEncoding) { a.Add("--crf-increment"); a.Add("1"); }
        if (s.Thorough) a.Add("--thorough");
        if (s.Samples is int n and > 0)
        {
            // A fixed count overrides --sample-every / --min-samples, so those are not passed.
            a.Add("--samples"); a.Add(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            // Auto: ab-av1 decides the number of samples from the video's duration.
            if (!string.IsNullOrWhiteSpace(s.SampleEvery)) { a.Add("--sample-every"); a.Add(s.SampleEvery.Trim()); }
            if (s.MinSamples is int ms) { a.Add("--min-samples"); a.Add(ms.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        }
        if (!string.IsNullOrWhiteSpace(s.SampleDuration)) { a.Add("--sample-duration"); a.Add(s.SampleDuration.Trim()); }
        if (!s.AbAv1SampleCache) { a.Add("--cache"); a.Add("false"); }
        foreach (var v in (s.VmafArgs ?? "").Split(['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries))
        { a.Add("--vmaf"); a.Add(v.Trim()); }
        if (!string.IsNullOrWhiteSpace(s.VmafScale)) { a.Add("--vmaf-scale"); a.Add(s.VmafScale.Trim()); }
    }

    /// <summary>Stable identifier of everything that influences the CRF search result.
    /// If it changes, a cached analysis is not reused.</summary>
    public static string SearchFingerprint(AppSettings s, ToolStatus tools, ProbeInfo? probe)
    {
        var a = SharedEncodeArgs(s, probe);
        a.Add("vmaf=" + Fmt.Arg(s.TargetVmaf));
        AddSearchArgs(a, s, tools);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", a)));
        return Convert.ToHexString(bytes)[..16];
    }

    // ------------------------------------------------------------------ encode

    public static List<string> Encode(AppSettings s, ToolStatus tools, ProbeInfo? probe, string input,
        double crf, string partialOutput, StreamPlan streams, string? progressFile)
    {
        var a = new List<string> { "encode", "-i", input, "--crf", Fmt.Arg(crf), "-o", partialOutput };
        a.AddRange(SharedEncodeArgs(s, probe));
        if (streams.AudioCodec != null) { a.Add("--acodec"); a.Add(streams.AudioCodec); }
        if (streams.Downmix) a.Add("--downmix-to-stereo");
        foreach (var e in streams.EncArgs) { a.Add("--enc"); a.Add(e); }
        foreach (var e in OutputContainers.FromExtension(System.IO.Path.GetExtension(partialOutput)).EncArgs) { a.Add("--enc"); a.Add(e); }
        if (s.AbAv1Verify && tools.EncodeVerify) a.Add("--verify");
        if (s.FailFast && tools.EncodeFailFast) a.Add("--fail-fast");
        // ffmpeg's machine readable progress (key=value) is written to a file the GUI tails once a second.
        if (progressFile != null) { a.Add("--enc"); a.Add("progress=" + progressFile); }
        return a;
    }

    // ------------------------------------------------------------------ stream plan

    public static StreamPlan PlanStreams(AppSettings s, ProbeInfo probe, string outputExt,
        List<int>? audioSelection, List<int>? subtitleSelection) =>
        PlanStreams(TrackOptions.FromAbAv1(s), probe, outputExt, audioSelection, subtitleSelection);

    /// <summary>Decide which tracks survive and how (shared by the AB-AV1 and Manual engines).
    /// Returned EncArgs use ab-av1 "--enc" syntax ("name=value"); the Manual engine converts them
    /// to plain FFmpeg arguments with <see cref="ToFfmpegArgs"/>.</summary>
    public static StreamPlan PlanStreams(TrackOptions s, ProbeInfo probe, string outputExt,
        List<int>? audioSelection, List<int>? subtitleSelection)
    {
        var plan = new StreamPlan();
        var ext = outputExt.ToLowerInvariant();
        bool mkv = ext == "mkv", mp4 = ext == "mp4", webm = ext == "webm";

        var main = probe.MainVideo;
        if (main is null) { plan.Blocker = "No video stream found"; return plan; }
        if (main.TypeIndex != 0 && s.RequireMainVideoFirst)
        {
            plan.Blocker = "The first video stream is cover art, not the main video; ab-av1 always encodes the first video stream.";
            return plan;
        }

        // ---------- audio ----------
        var audio = probe.Audio.ToList();
        var keepAudio = SelectTracks(audio, audioSelection, s.AudioLanguages, "audio", plan);
        if (s.AudioMode == AudioMode.Remove || keepAudio.Count == 0)
        {
            if (audio.Count > 0) plan.EncArgs.Add("an");
            plan.ExpectedAudio = 0;
        }
        else
        {
            foreach (var d in audio.Except(keepAudio)) plan.EncArgs.Add($"map=-0:a:{d.TypeIndex}");
            plan.ExpectedAudio = keepAudio.Count;

            if (s.AudioMode == AudioMode.Transcode)
            {
                plan.AudioCodec = string.IsNullOrWhiteSpace(s.AudioCodec) ? "libopus" : s.AudioCodec.Trim();
                if (mp4 && plan.AudioCodec.StartsWith("pcm_"))
                {
                    plan.Warnings.Add("MP4 cannot store PCM audio — using AAC instead (choose MKV to keep PCM).");
                    plan.AudioCodec = "aac";
                }
                if (webm && plan.AudioCodec is not ("libopus" or "libvorbis"))
                {
                    plan.Warnings.Add("WebM only supports Opus/Vorbis audio — using libopus.");
                    plan.AudioCodec = "libopus";
                }
                if (!string.IsNullOrWhiteSpace(s.AudioBitrate)) plan.EncArgs.Add("b:a=" + s.AudioBitrate.Trim());
                plan.Downmix = s.DownmixToStereo;
            }
            else
            {
                // Copy mode: copy everything the container accepts, re-encode only incompatible tracks.
                var allowed = mp4 ? Mp4Audio : webm ? WebmAudio : null;
                if (allowed != null)
                {
                    for (int outIdx = 0; outIdx < keepAudio.Count; outIdx++)
                    {
                        var t = keepAudio[outIdx];
                        if (t.Codec != null && allowed.Contains(t.Codec)) continue;
                        plan.EncArgs.Add($"c:a:{outIdx}=libopus");
                        plan.EncArgs.Add($"b:a:{outIdx}={OpusBitrate(t.Channels)}");
                        plan.Warnings.Add($"Audio {t.Describe()} is not supported in .{ext}; it will be re-encoded to Opus.");
                    }
                }
            }
        }

        // ---------- subtitles ----------
        var subs = probe.Subtitles.ToList();
        var keepSubs = s.SubtitleMode == SubtitleMode.Remove ? new List<StreamInfo>()
            : SelectTracks(subs, subtitleSelection, s.SubtitleMode == SubtitleMode.Languages ? s.SubtitleLanguages : "", "subtitle", plan);
        if (s.ForcedSubtitlesOnly)
            keepSubs = keepSubs.Where(t => t.IsForced).ToList();
        if (!mkv)
        {
            foreach (var t in keepSubs.Where(t => t.Codec is null || !TextSubtitleCodecs.Contains(t.Codec)).ToList())
            {
                plan.Warnings.Add($"Subtitle {t.Describe()} cannot be stored in .{ext} and will be dropped (use MKV to keep it).");
                keepSubs.Remove(t);
            }
        }
        if (keepSubs.Count == 0)
        {
            if (subs.Count > 0) plan.EncArgs.Add("sn");
        }
        else
        {
            foreach (var d in subs.Except(keepSubs)) plan.EncArgs.Add($"map=-0:s:{d.TypeIndex}");
            if (mp4) plan.EncArgs.Add("c:s=mov_text");
            if (webm) plan.EncArgs.Add("c:s=webvtt");
            if (mkv)
            {
                // MP4 text subtitles (mov_text) cannot be stored in Matroska as they are: convert them to SRT.
                for (int outIdx = 0; outIdx < keepSubs.Count; outIdx++)
                    if (string.Equals(keepSubs[outIdx].Codec, "mov_text", StringComparison.OrdinalIgnoreCase))
                        plan.EncArgs.Add($"c:s:{outIdx}=srt");
            }
            if (!string.IsNullOrWhiteSpace(s.DefaultSubtitleLanguage))
            {
                var lang = s.DefaultSubtitleLanguage.Trim().ToLowerInvariant();
                for (int outIdx = 0; outIdx < keepSubs.Count; outIdx++)
                {
                    bool isDefault = (keepSubs[outIdx].Language ?? "").ToLowerInvariant() == lang
                                     && keepSubs.FindIndex(t => (t.Language ?? "").ToLowerInvariant() == lang) == outIdx;
                    var flags = new List<string>();
                    if (isDefault) flags.Add("default");
                    if (keepSubs[outIdx].IsForced) flags.Add("forced");
                    plan.EncArgs.Add($"disposition:s:{outIdx}={(flags.Count == 0 ? "0" : string.Join("+", flags))}");
                }
            }
        }
        plan.ExpectedSubtitles = keepSubs.Count;

        // ---------- attachments / data / metadata ----------
        bool hasAttachments = probe.Attachments.Any();
        if (hasAttachments && (!mkv || !s.KeepAttachments))
        {
            plan.EncArgs.Add("map=-0:t?");
            if (!mkv && s.KeepAttachments)
                plan.Warnings.Add($"Attachments (fonts/cover) cannot be stored in .{ext} and will be dropped.");
        }
        if (!mkv && probe.Data.Any()) plan.EncArgs.Add("dn"); // ab-av1 already adds -dn for mkv/webm
        if (!s.KeepGlobalMetadata) plan.EncArgs.Add("map_metadata=-1");
        if (!s.KeepChapters) plan.EncArgs.Add("map_chapters=-1");

        return plan;
    }

    /// <summary>Convert ab-av1 "--enc" values to FFmpeg arguments using ab-av1's own rule:
    /// the first '=' separates option and value ("map=-0:a:1" → "-map", "-0:a:1").</summary>
    public static List<string> ToFfmpegArgs(IEnumerable<string> encArgs)
    {
        var result = new List<string>();
        foreach (var raw in encArgs)
        {
            var a = raw.TrimStart('-');
            int eq = a.IndexOf('=');
            if (eq < 0) result.Add("-" + a);
            else { result.Add("-" + a[..eq]); result.Add(a[(eq + 1)..]); }
        }
        return result;
    }

    private static string OpusBitrate(int? channels) => channels switch
    {
        null or <= 2 => "160k",
        <= 6 => "384k",
        _ => "512k",
    };

    private static List<StreamInfo> SelectTracks(List<StreamInfo> tracks, List<int>? explicitSelection,
        string languages, string kind, StreamPlan plan)
    {
        if (explicitSelection != null)
            return tracks.Where(t => explicitSelection.Contains(t.TypeIndex)).ToList();

        var langs = (languages ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToLowerInvariant()).ToHashSet();
        if (langs.Count == 0) return tracks.ToList();

        var keep = tracks.Where(t => langs.Contains((t.Language ?? "und").ToLowerInvariant())).ToList();
        if (keep.Count == 0 && tracks.Count > 0 && kind == "audio")
        {
            // Never silently produce a file without any audio because of a language filter.
            plan.Warnings.Add($"No audio track matches languages [{languages}] — keeping all audio tracks.");
            return tracks.ToList();
        }
        return keep;
    }
}
