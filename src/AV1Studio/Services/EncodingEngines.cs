using System.Diagnostics;
using System.IO;
using AV1Studio.Models;
using AV1Studio.Native;

namespace AV1Studio.Services;

/// <summary>
/// Encoding engine abstraction. Both engines write a *.partial output and hand over to the SAME
/// verification / rename / safe-deletion pipeline in <see cref="QueueProcessor"/>.
///
///   EncodingEngine
///   ├── AbAv1Engine      (target VMAF → ab-av1 crf-search → ab-av1 encode)
///   └── ManualAv1Engine  (FFmpeg + SVT-AV1 / NVENC / QSV / AMF with user parameters)
/// </summary>
public interface IEncodingEngine
{
    EncodeMode Mode { get; }
    /// <summary>Human-readable encoder name, e.g. "SVT-AV1 (CPU)".</summary>
    string EncoderName { get; }
    string PresetText { get; }
    /// <summary>True when the engine already decoded the whole output during the encode (ab-av1 --verify).</summary>
    bool VerifiesDecode { get; }
    bool IsHardware { get; }
    /// <summary>Container extension the engine writes.</summary>
    string ContainerExtension(string sourcePath);
    StreamPlan PlanStreams(QueueItem item, ProbeInfo probe);
    Task<EncodeOutcome> EncodeAsync(QueueItem item, OutputPlan output, StreamPlan streams, double quality, CancellationToken ct);
}

public sealed class AbAv1Engine(AppSettings s, ToolStatus tools) : IEncodingEngine
{
    public EncodeMode Mode => EncodeMode.AbAv1;
    public string EncoderName => AbAv1Commands.EncoderDescription(s);
    public string PresetText => s.HardwareEncoding
        ? (string.IsNullOrWhiteSpace(s.HardwarePreset) ? "default" : s.HardwarePreset)
        : s.Preset?.ToString() ?? "ab-av1 default";
    public bool VerifiesDecode => s.AbAv1Verify && tools.EncodeVerify;
    public bool IsHardware => s.HardwareEncoding;
    public string ContainerExtension(string sourcePath) => OutputPlanner.ContainerExtension(s.Container, sourcePath);

    public StreamPlan PlanStreams(QueueItem item, ProbeInfo probe) =>
        AbAv1Commands.PlanStreams(s, probe, ContainerExtension(item.SourcePath), item.AudioSelection, item.SubtitleSelection);

    public Task<EncodeOutcome> EncodeAsync(QueueItem item, OutputPlan output, StreamPlan streams, double quality, CancellationToken ct) =>
        EncodeRunner.RunAsync(item, s, tools, output, streams, quality, ct);
}

public sealed class ManualAv1Engine(ManualSettings m, AppSettings s, ToolStatus tools) : IEncodingEngine
{
    public ManualSettings Settings => m;
    public EncodeMode Mode => EncodeMode.Manual;
    public string EncoderName => ManualCommands.Spec(m.Encoder).Name;
    public string PresetText => m.Preset;
    public bool VerifiesDecode => false;
    public bool IsHardware => ManualCommands.Spec(m.Encoder).IsHardware;
    public string ContainerExtension(string sourcePath) => OutputPlanner.ContainerExtension(s.Container, sourcePath);

    public StreamPlan PlanStreams(QueueItem item, ProbeInfo probe) =>
        AbAv1Commands.PlanStreams(TrackOptions.FromManual(m), probe, ContainerExtension(item.SourcePath), item.AudioSelection, item.SubtitleSelection);

    public Task<EncodeOutcome> EncodeAsync(QueueItem item, OutputPlan output, StreamPlan streams, double quality, CancellationToken ct) =>
        ManualEncodeRunner.RunAsync(item, m, s, tools, output.PartialPath, ContainerExtension(item.SourcePath),
            streams, quality, ct);
}

/// <summary>Runs FFmpeg directly for Manual AV1 mode (also used for previews).</summary>
public static class ManualEncodeRunner
{
    public static async Task<EncodeOutcome> RunAsync(QueueItem item, ManualSettings m, AppSettings s, ToolStatus tools,
        string outputPath, string ext, StreamPlan streams, double quality, CancellationToken ct,
        double? startSeconds = null, double? durationSeconds = null, bool isPreview = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var progressFile = Path.Combine(AppPaths.Progress, item.Id.ToString("N") + (isPreview ? "-preview" : "") + ".txt");
        TryDelete(progressFile);
        var probe = item.Probe ?? throw new InvalidOperationException("File has not been probed");

        var args = ManualCommands.Build(m, probe, item.SourcePath, outputPath, ext, streams, quality, progressFile,
            s.FailFast && !isPreview, startSeconds, durationSeconds);
        var tail = new Tail(80);
        var sw = Stopwatch.StartNew();
        var outcome = new EncodeOutcome();
        double duration = durationSeconds ?? probe.DurationSeconds ?? 0;

        using var child = ChildProcess.Start(tools.FfmpegPath!, args,
            onStdout: l => Log.Tool($"[ffmpeg] {l}", item.FileName),
            onStderr: l => { tail.Add(l); Log.Tool($"[ffmpeg] {l}", item.FileName); },
            workingDirectory: AppPaths.Temp,
            resources: ResourcePlanner.Plan(s.EncodeCpu));

        if (!isPreview) { item.LastEncodeCommand = child.CommandLineText; item.ActiveProcess = child; }
        outcome.CommandLine = child.CommandLineText;
        Log.Command(child.CommandLineText, item.FileName);

        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = isPreview ? Task.CompletedTask : Task.Run(() => ProgressMonitor.RunAsync(child, item, progressFile, duration, sw,
            measureGpu: ManualCommands.Spec(m.Encoder).IsHardware, onEnd: null, monitorCts.Token));
        try
        {
            outcome.ExitCode = await child.WaitAsync(ct);
        }
        finally
        {
            if (!isPreview) item.ActiveProcess = null;
            monitorCts.Cancel();
            try { await monitor; } catch { }
            TryDelete(progressFile);
        }
        outcome.Elapsed = sw.Elapsed;
        outcome.Cancelled = ct.IsCancellationRequested;
        if (!outcome.Cancelled && outcome.ExitCode != 0)
        {
            // FFmpeg's most useful line is usually the last error-looking one.
            var err = tail.LastMatching(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                                             || l.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                                             || l.Contains("not ", StringComparison.OrdinalIgnoreCase))
                      ?? tail.LastMatching(_ => true);
            outcome.Error = err?.Trim() ?? $"FFmpeg failed (exit {outcome.ExitCode})";
            Log.Error("FFmpeg output (last lines):\n" + tail.Text, item.FileName);
        }
        return outcome;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
