using System.Diagnostics;
using System.Globalization;
using System.IO;
using AV1Studio.Models;
using AV1Studio.Util;

namespace AV1Studio.Services;

public sealed class PreviewResult
{
    public string OutputPath { get; init; } = "";
    public string? SourceSegmentPath { get; init; }
    public string? SourceFrame { get; init; }
    public string? EncodedFrame { get; init; }
    public long Size { get; init; }
    public double Seconds { get; init; }
    public double StartSeconds { get; init; }
    public double BitrateKbps { get; init; }
    public TimeSpan EncodeTime { get; init; }
    public double? EncodeFps { get; init; }
    public string Encoder { get; init; } = "";
    public string QualityText { get; init; } = "";
    public string Preset { get; init; } = "";
    public string Command { get; init; } = "";
    public long? EstimatedFullSize { get; init; }
    public bool FramesToneMapped { get; init; }
}

/// <summary>Encodes a short segment with the exact selected settings so results can be judged before a full encode.</summary>
public static class PreviewService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string Folder => Path.Combine(AppPaths.Temp, "preview");

    public static async Task<PreviewResult> RunAsync(QueueItem item, EncodeMode mode, AppSettings s, ToolStatus tools,
        int seconds, CancellationToken ct)
    {
        var probe = item.Probe ?? throw new InvalidOperationException("The file has not been read yet (ffprobe). Wait a moment and try again.");
        double total = probe.DurationSeconds ?? 0;
        double len = Math.Clamp(seconds, 2, 600);
        if (total > 0) len = Math.Min(len, total);
        double start = total > len ? Math.Round(Math.Min(total * 0.33, total - len), 3) : 0;

        // Our own scratch folder: safe to clear.
        if (Directory.Exists(Folder)) { try { Directory.Delete(Folder, true); } catch { } }
        Directory.CreateDirectory(Folder);

        string ext, output, command, encoder, qualityText, preset;
        var sw = Stopwatch.StartNew();
        EncodeOutcome enc;

        if (mode == EncodeMode.Manual)
        {
            var m = s.Manual;
            var spec = ManualCommands.Spec(m.Encoder);
            ext = OutputPlanner.ContainerExtension(s.Container, item.SourcePath);
            output = Path.Combine(Folder, "preview." + ext);
            var streams = AbAv1Commands.PlanStreams(TrackOptions.FromManual(m), probe, ext, item.AudioSelection, item.SubtitleSelection);
            if (streams.Blocker != null) throw new InvalidOperationException(streams.Blocker);
            PreviewOnlyVideoAndAudio(streams);
            double q = item.CrfOverride ?? m.Quality;
            enc = await ManualEncodeRunner.RunAsync(item, m, s, tools, output, ext, streams, q, ct, start, len, isPreview: true);
            command = enc.CommandLine ?? "";
            encoder = spec.Name;
            qualityText = $"{spec.QualityName} {Fmt.Num(q)}";
            preset = m.Preset;
        }
        else
        {
            if (item.EffectiveCrf is not double crf)
                throw new InvalidOperationException("AB-AV1 needs a CRF first: run \"Analyze\" on this file (or set a CRF override) before previewing.");
            ext = OutputPlanner.ContainerExtension(s.Container, item.SourcePath);
            output = Path.Combine(Folder, "preview." + ext);
            var streams = AbAv1Commands.PlanStreams(s, probe, ext, item.AudioSelection, item.SubtitleSelection);
            PreviewOnlyVideoAndAudio(streams);
            var ps = s.Clone();
            ps.AbAv1Verify = false; // a segment never matches the full source duration
            var args = AbAv1Commands.Encode(ps, tools, probe, item.SourcePath, crf, output, streams, null);
            var (inSeek, outSeek) = ManualCommands.SplitSeek(start); // same A/V-accurate hybrid seek as Manual previews
            if (inSeek > 0) args.AddRange(["--enc-input", "ss=" + inSeek.ToString("0.###", Inv)]);
            if (outSeek > 0) args.AddRange(["--enc", "ss=" + outSeek.ToString("0.###", Inv)]);
            args.AddRange(["--enc", "t=" + len.ToString("0.###", Inv)]);
            var tail = new Tail(40);
            using var child = ChildProcess.Start(tools.AbAv1Path!, args, l => Log.Tool($"[ab-av1 preview] {l}", item.FileName),
                l => { tail.Add(l); Log.Tool($"[ab-av1 preview] {l}", item.FileName); },
                environment: ToolLocator.ChildEnvironment(tools), resources: ResourcePlanner.Plan(s.EncodeCpu));
            command = child.CommandLineText;
            Log.Command(command, item.FileName);
            int code = await child.WaitAsync(ct);
            enc = new EncodeOutcome
            {
                ExitCode = code, Cancelled = ct.IsCancellationRequested,
                Error = code != 0 ? tail.LastMatching(l => l.StartsWith("Error:")) ?? $"ab-av1 exited with {code}" : null,
            };
            encoder = AbAv1Commands.EncoderDescription(s);
            qualityText = $"CRF {Fmt.Num(crf)}" + (item.CrfOverride != null ? " (override)" : " (detected)");
            preset = s.HardwareEncoding ? (string.IsNullOrWhiteSpace(s.HardwarePreset) ? "default" : s.HardwarePreset) : s.Preset?.ToString() ?? "ab-av1 default";
        }
        sw.Stop();
        ct.ThrowIfCancellationRequested();
        if (enc.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("Preview encode failed: " + (enc.Error ?? "no output produced"));

        long size = new FileInfo(output).Length;
        double outDur = len;
        try { outDur = (await FfprobeService.ProbeAsync(tools.FfprobePath!, output, ct)).DurationSeconds ?? len; } catch { }

        // Matching frames for side-by-side comparison (HDR frames are tone-mapped for display only).
        bool toneMap = probe.IsHdr && tools.FfmpegHasZscale;
        string display = (toneMap ? "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv," : "")
                         + "scale=960:-2,format=rgb24";
        double mid = len / 2;
        var srcFrame = Path.Combine(Folder, "source.png");
        var encFrame = Path.Combine(Folder, "encoded.png");
        await Frame(tools, item.SourcePath, start + mid, srcFrame, display, ct);
        bool encodedIsHdr = probe.IsHdr && !(mode == EncodeMode.Manual && s.Manual.Hdr == HdrHandling.ToneMapToSdr);
        await Frame(tools, output, mid, encFrame, encodedIsHdr ? display : "scale=960:-2,format=rgb24", ct);

        // Same segment of the source (stream copy) for playback comparison.
        var segment = Path.Combine(Folder, "source-segment.mkv");
        try
        {
            await ChildProcess.RunCaptureAsync(tools.FfmpegPath!,
            [
                "-hide_banner", "-nostdin", "-v", "error", "-y", "-ss", start.ToString("0.###", Inv), "-t", len.ToString("0.###", Inv),
                "-i", item.SourcePath, "-map", "0:v:0", "-map", "0:a?", "-c", "copy", segment,
            ], ct, TimeSpan.FromMinutes(2));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        long frames = probe.MainVideo?.FrameRate is double fps ? (long)(fps * len) : 0;
        return new PreviewResult
        {
            OutputPath = output,
            SourceSegmentPath = File.Exists(segment) ? segment : null,
            SourceFrame = File.Exists(srcFrame) ? srcFrame : null,
            EncodedFrame = File.Exists(encFrame) ? encFrame : null,
            Size = size,
            Seconds = outDur,
            StartSeconds = start,
            BitrateKbps = outDur > 0 ? size * 8 / outDur / 1000 : 0,
            EncodeTime = sw.Elapsed,
            EncodeFps = frames > 0 && sw.Elapsed.TotalSeconds > 0 ? frames / sw.Elapsed.TotalSeconds : null,
            Encoder = encoder,
            QualityText = qualityText,
            Preset = preset,
            Command = command,
            EstimatedFullSize = total > 0 && outDur > 0 ? (long)(size / outDur * total) : null,
            FramesToneMapped = toneMap,
        };
    }

    /// <summary>A preview is about picture/sound quality and size: subtitles, chapters and attachments are left
    /// out (their timing would otherwise stretch the segment's duration and distort bitrate/size estimates).</summary>
    private static void PreviewOnlyVideoAndAudio(StreamPlan streams)
    {
        streams.EncArgs.RemoveAll(a => a.StartsWith("c:s=") || a.StartsWith("disposition:s:") || a.StartsWith("map=-0:s:") || a == "sn");
        streams.EncArgs.AddRange(["sn", "map_chapters=-1"]);
        if (!streams.EncArgs.Contains("map=-0:t?")) streams.EncArgs.Add("map=-0:t?");
    }

    private static async Task Frame(ToolStatus tools, string input, double at, string output, string vf, CancellationToken ct)
    {
        try
        {
            await ChildProcess.RunCaptureAsync(tools.FfmpegPath!,
            [
                "-hide_banner", "-nostdin", "-v", "error", "-y", "-ss", at.ToString("0.###", Inv), "-i", input,
                "-frames:v", "1", "-vf", vf, output,
            ], ct, TimeSpan.FromMinutes(1));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }
}
