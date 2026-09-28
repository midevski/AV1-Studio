using System.IO;
using AV1Studio.Models;

namespace AV1Studio.Services;

public sealed record VerificationStep(string Name, bool Passed, string Detail);

public sealed class VerificationResult
{
    public List<VerificationStep> Steps { get; } = new();
    public bool Passed => Steps.Count > 0 && Steps.All(s => s.Passed);
    public string? FirstFailure => Steps.FirstOrDefault(s => !s.Passed) is { } f ? $"{f.Name}: {f.Detail}" : null;
    public ProbeInfo? OutputProbe { get; set; }
    public long OutputSize { get; set; }
}

public sealed record VerificationInput(
    int ExitCode,
    string OutputPath,
    ProbeInfo SourceProbe,
    string SourcePath,
    long SourceSizeAtStart,
    DateTime SourceMtimeAtStart,
    int ExpectedAudio,
    int ExpectedSubtitles,
    bool CheckStreamCounts,
    double DurationToleranceSeconds,
    bool RunDecodeCheck);

/// <summary>
/// Independent verification of an encode. Every step must pass before the output is moved into
/// place and before the source may be deleted. Steps stop at the first failure.
/// </summary>
public static class VerificationService
{
    public static async Task<VerificationResult> VerifyAsync(VerificationInput v, ToolStatus tools, CancellationToken ct)
    {
        var r = new VerificationResult();

        bool Step(string name, bool ok, string detail)
        {
            r.Steps.Add(new VerificationStep(name, ok, detail));
            return ok;
        }

        if (!Step("Process exit code", v.ExitCode == 0, $"exit code {v.ExitCode}")) return r;

        var fi = new FileInfo(v.OutputPath);
        if (!Step("Output exists", fi.Exists, fi.Exists ? v.OutputPath : $"not found: {v.OutputPath}")) return r;

        r.OutputSize = fi.Length;
        if (!Step("Output size > 0", fi.Length > 0, $"{fi.Length:N0} bytes")) return r;

        ProbeInfo outProbe;
        try
        {
            outProbe = await FfprobeService.ProbeAsync(tools.FfprobePath!, v.OutputPath, ct);
            r.OutputProbe = outProbe;
            Step("FFprobe can read output", true, outProbe.FormatName ?? "ok");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Step("FFprobe can read output", false, ex.Message);
            return r;
        }

        // The real container, as read by FFprobe, must be the one chosen (the extension alone proves nothing).
        var container = OutputContainers.FromExtension(Path.GetExtension(v.OutputPath));
        if (!Step($"Container is {container.Label}", OutputContainers.Matches(container, outProbe, out var actualContainer), actualContainer))
            return r;

        var video = outProbe.MainVideo;
        if (!Step("Video stream present", video != null, video is null ? "no video stream" : $"{video.Codec} {video.Width}×{video.Height}"))
            return r;
        if (!Step("Video codec is AV1", string.Equals(video!.Codec, "av1", StringComparison.OrdinalIgnoreCase), video.Codec ?? "unknown"))
            return r;

        double? inDur = v.SourceProbe.DurationSeconds;
        double? outDur = outProbe.DurationSeconds;
        if (inDur is double id && id > 0)
        {
            if (outDur is not double od)
            {
                if (!Step("Duration matches source", false, "output has no readable duration")) return r;
            }
            else
            {
                // Allow a small absolute tolerance; relax proportionally for very long files only.
                double tol = Math.Max(v.DurationToleranceSeconds, id * 0.0005);
                double diff = Math.Abs(id - od);
                string detail = $"source {id:0.00}s, output {od:0.00}s, Δ {diff:0.00}s (tolerance {tol:0.00}s)";
                if (!Step("Duration matches source", diff <= tol, detail)) return r;
            }
        }
        else
        {
            Step("Duration matches source", true, "source has no duration — skipped");
        }

        if (v.CheckStreamCounts)
        {
            int a = outProbe.Audio.Count(), s = outProbe.Subtitles.Count();
            if (!Step("Audio tracks", a == v.ExpectedAudio, $"expected {v.ExpectedAudio}, found {a}")) return r;
            if (!Step("Subtitle tracks", s == v.ExpectedSubtitles, $"expected {v.ExpectedSubtitles}, found {s}")) return r;
        }

        if (v.RunDecodeCheck)
        {
            var (ok, detail) = await DecodeCheckAsync(tools, v.OutputPath, ct);
            if (!Step("Full decode without errors", ok, detail)) return r;
        }

        // The source must be exactly the file we encoded (not replaced/modified meanwhile).
        var src = new FileInfo(v.SourcePath);
        bool unchanged = src.Exists && src.Length == v.SourceSizeAtStart && src.LastWriteTimeUtc == v.SourceMtimeAtStart;
        Step("Source unchanged during encode", unchanged,
            unchanged ? "size and modification time match" : "source was modified, moved or deleted during the encode");

        return r;
    }

    /// <summary>Messages FFmpeg prints at error level while reading valid files; they are not decode errors.
    /// (FFmpeg writes MP4 chapters as a QuickTime chapter track and then reports it as "not found" when reading.)</summary>
    private static readonly string[] BenignDemuxerMessages =
    [
        "Referenced QT chapter track not found",
    ];

    internal static bool IsBenignDemuxerMessage(string line) =>
        BenignDemuxerMessages.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Own decode check used when the installed ab-av1 has no --verify.</summary>
    private static async Task<(bool, string)> DecodeCheckAsync(ToolStatus tools, string file, CancellationToken ct)
    {
        try
        {
            var (code, _, err) = await ChildProcess.RunCaptureAsync(tools.FfmpegPath!,
            [
                "-hide_banner", "-nostdin", "-v", "error", "-xerror",
                "-i", file, "-map", "0:v?", "-map", "0:a?", "-f", "null", "-",
            ], ct, TimeSpan.FromHours(12));
            var firstErr = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => !IsBenignDemuxerMessage(l));
            return code == 0 && firstErr is null
                ? (true, "decoded without errors")
                : (false, firstErr ?? $"ffmpeg exit {code}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
