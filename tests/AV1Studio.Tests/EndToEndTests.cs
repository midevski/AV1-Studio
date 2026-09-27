using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Tests;

/// <summary>
/// Real pipeline tests against real ab-av1 / ffmpeg / ffprobe binaries.
/// Set AV1STUDIO_TEST_TOOLS to a folder containing ab-av1.exe, ffmpeg.exe and ffprobe.exe;
/// without it these tests are no-ops.
/// </summary>
[Collection("e2e")]
public class EndToEndTests : IDisposable
{
    private static readonly string? ToolsDir = Environment.GetEnvironmentVariable("AV1STUDIO_TEST_TOOLS");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abav1gui-e2e-" + Guid.NewGuid().ToString("N")[..8]);

    public EndToEndTests()
    {
        AppPaths.Root = Path.Combine(_root, "appdata");
        AppPaths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static bool Enabled => ToolsDir != null && File.Exists(Path.Combine(ToolsDir, "ab-av1.exe"));

    private AppSettings Settings(string dest) => new()
    {
        AbAv1Path = Path.Combine(ToolsDir!, "ab-av1.exe"),
        FfmpegPath = Path.Combine(ToolsDir!, "ffmpeg.exe"),
        FfprobePath = Path.Combine(ToolsDir!, "ffprobe.exe"),
        DestinationFolder = dest,
        Preset = 12,
        Samples = 1,
        SampleDuration = "2s",
        MaxEncodedPercent = 95,
        TargetVmaf = 90,
        Priority = EncoderPriority.BelowNormal,
    };

    /// <summary>Movie with eng+fre audio, eng subtitle, 2 chapters, title — in an awkward path.</summary>
    private async Task<string> MakeSourceAsync(string name, int seconds = 20, string size = "640x360")
    {
        var dir = Path.Combine(_root, "Library (é) [x]", "Sub folder");
        Directory.CreateDirectory(dir);
        var srt = Path.Combine(dir, "s.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:04,000\nHello\n");
        var meta = Path.Combine(dir, "m.txt");
        File.WriteAllText(meta, $";FFMETADATA1\ntitle=Test Movie\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND={seconds * 500}\ntitle=A\n[CHAPTER]\nTIMEBASE=1/1000\nSTART={seconds * 500}\nEND={seconds * 1000}\ntitle=B\n");
        var output = Path.Combine(dir, name);
        var (code, _, err) = await ChildProcess.RunCaptureAsync(Path.Combine(ToolsDir!, "ffmpeg.exe"),
        [
            "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc=s={size}:r=24:d={seconds}",
            "-f", "lavfi", "-i", $"sine=f=440:d={seconds}",
            "-f", "lavfi", "-i", $"sine=f=660:d={seconds}",
            "-i", srt, "-i", meta,
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map_metadata", "4", "-map_chapters", "4",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "8", "-c:a", "ac3", "-c:s", "srt",
            "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=fre", "-metadata:s:s:0", "language=eng",
            output,
        ], timeout: TimeSpan.FromMinutes(5));
        Assert.True(code == 0, err);
        File.Delete(srt); File.Delete(meta);
        return output;
    }

    private static QueueItem ItemFor(string path)
    {
        var fi = new FileInfo(path);
        return new QueueItem
        {
            SourcePath = path, SourceRoot = Path.GetDirectoryName(Path.GetDirectoryName(path)),
            SourceSize = fi.Length, SourceModifiedUtc = fi.LastWriteTimeUtc,
        };
    }

    [Fact]
    public async Task Full_pipeline_encodes_verifies_and_deletes_source()
    {
        if (!Enabled) return;
        var src = await MakeSourceAsync("L'été vidéo (2020).mkv");
        var dest = Path.Combine(_root, "Out AV1");
        var s = Settings(dest);
        s.DeleteSourceAfterSuccess = true;
        s.AudioLanguages = "eng";
        var tools = await ToolLocator.DetectAsync(s);
        Assert.True(tools.Ready, string.Join("; ", tools.Problems));

        var item = ItemFor(src);
        var cache = new AnalysisCache();
        var qp = new QueueProcessor(s, tools, cache);
        await qp.RunAsync([item], RunMode.AnalyzeAndEncode);

        Assert.True(item.Status == ItemStatus.Deleted, $"{item.Status}: {item.ErrorMessage} {item.StatusDetail}");
        Assert.NotNull(item.Search);
        Assert.NotEmpty(item.Attempts);
        Assert.False(File.Exists(src));
        var expected = Path.Combine(dest, "Sub folder", "L'été vidéo (2020).mkv"); // original name kept in the destination
        Assert.Equal(expected, item.OutputPath);
        Assert.True(File.Exists(expected));
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(expected)!), OutputPlanner.IsOurTemporaryFile);

        var probe = await FfprobeService.ProbeAsync(tools.FfprobePath!, expected);
        Assert.Equal("av1", probe.MainVideo!.Codec);
        Assert.Equal("eng", probe.Audio.Single().Language);   // French dropped by language rule
        Assert.Equal("eng", probe.Subtitles.Single().Language);
        Assert.Equal(2, probe.ChapterCount);
        Assert.Equal("Test Movie", probe.Title);
        Assert.Equal(20, probe.DurationSeconds!.Value, 0);
        Assert.Single(cache.Entries);
    }

    [Fact]
    public async Task Failed_verification_preserves_source_and_removes_partial()
    {
        if (!Enabled) return;
        var src = await MakeSourceAsync("verify-fail.mkv");
        var dest = Path.Combine(_root, "Out");
        var s = Settings(dest);
        s.DeleteSourceAfterSuccess = true;
        // Produce an output that is 10 s shorter than the source. ab-av1's own --verify is off,
        // so the app's independent duration check is what must catch it.
        s.CustomVideoFilter = "trim=duration=10,setpts=PTS-STARTPTS";
        s.AudioMode = AudioMode.Remove;
        s.SubtitleMode = SubtitleMode.Remove;
        s.AbAv1Verify = false;
        var tools = await ToolLocator.DetectAsync(s);

        var item = ItemFor(src);
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([item], RunMode.AnalyzeAndEncode);

        Assert.Equal(ItemStatus.Failed, item.Status);
        Assert.Contains("Duration", item.ErrorMessage);
        Assert.True(File.Exists(src));
        Assert.Equal(new FileInfo(src).Length, item.SourceSize);
        var outDir = Path.Combine(dest, "Sub folder");
        Assert.False(Directory.Exists(outDir) && Directory.GetFiles(outDir).Length > 0, "no output or partial may remain");
    }

    [Fact]
    public async Task Cancelling_an_encode_leaves_source_intact_and_no_partial()
    {
        if (!Enabled) return;
        var src = await MakeSourceAsync("cancel.mkv", seconds: 120, size: "1920x1080");
        var dest = Path.Combine(_root, "Out");
        var s = Settings(dest);
        s.Preset = 4; // slow, so there is time to cancel
        s.DeleteSourceAfterSuccess = true;
        var tools = await ToolLocator.DetectAsync(s);

        var item = ItemFor(src);
        item.CrfOverride = 35; // skip the search; go straight to encoding
        var qp = new QueueProcessor(s, tools, new AnalysisCache());
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueItem.Progress) && item.Progress > 0.5) qp.Stop();
        };
        var run = qp.RunAsync([item], RunMode.EncodeAnalyzed);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(3)));
        Assert.Same(run, finished);

        Assert.True(item.Status == ItemStatus.Ready, $"{item.Status}: {item.StatusDetail} {item.ErrorMessage}");
        Assert.True(File.Exists(src));
        var outDir = Path.Combine(dest, "Sub folder");
        Assert.True(!Directory.Exists(outDir) || Directory.GetFiles(outDir).Length == 0,
            "partial files must be cleaned up: " + (Directory.Exists(outDir) ? string.Join(", ", Directory.GetFiles(outDir)) : ""));
    }

    [Fact]
    public async Task Unticking_delete_source_during_a_run_keeps_the_source()
    {
        if (!Enabled) return;
        var src = await MakeSourceAsync("keep-me.mkv");
        var s = Settings(Path.Combine(_root, "Out"));
        s.DeleteSourceAfterSuccess = true; // snapshot at start of run…
        var tools = await ToolLocator.DetectAsync(s);

        var item = ItemFor(src);
        var qp = new QueueProcessor(s, tools, new AnalysisCache()) { DeletionStillAllowed = () => false }; // …then unticked
        await qp.RunAsync([item], RunMode.AnalyzeAndEncode);

        Assert.Equal(ItemStatus.Completed, item.Status);
        Assert.True(File.Exists(src));
        Assert.True(File.Exists(item.OutputPath));
        Assert.False(item.SourceDeleted);
    }

    [Fact]
    public async Task Gpu_encoding_pipeline_when_hardware_is_available()
    {
        if (!Enabled) return;
        var s = Settings(Path.Combine(_root, "Out"));
        var tools = await ToolLocator.DetectAsync(s);
        if (!tools.HardwareAvailable("av1_nvenc")) return; // machine without a working AV1 NVENC encoder

        var src = await MakeSourceAsync("gpu.mkv");
        s.HardwareEncoding = true;
        s.HardwareEncoder = "av1_nvenc";
        s.HardwarePreset = "p5";
        s.DeleteSourceAfterSuccess = true;
        var item = ItemFor(src);
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([item], RunMode.AnalyzeAndEncode);

        Assert.True(item.Status == ItemStatus.Deleted, $"{item.Status}: {item.ErrorMessage}");
        Assert.Contains("-e av1_nvenc", item.LastEncodeCommand);
        var probe = await FfprobeService.ProbeAsync(tools.FfprobePath!, item.OutputPath!);
        Assert.Equal("av1", probe.MainVideo!.Codec);
        Assert.Equal("yuv420p10le", probe.MainVideo.PixFmt);
    }

    // ============================================================ Manual AV1 mode

    private static QueueItem ManualItem(string path) { var i = ItemFor(path); i.Mode = EncodeMode.Manual; return i; }

    [Fact]
    public async Task Manual_svt_pipeline_uses_ffmpeg_directly_verifies_and_deletes_source()
    {
        if (!Enabled) return;
        var src = await MakeSourceAsync("manual (é).mkv");
        var dest = Path.Combine(_root, "Out manual");
        var s = Settings(dest);
        s.DeleteSourceAfterSuccess = true;
        s.Manual = new ManualSettings { Encoder = "libsvtav1", Quality = 40, Preset = "12", AudioLanguages = "eng" };
        var tools = await ToolLocator.DetectAsync(s);
        Assert.Contains("libsvtav1", tools.ManualEncoders);

        var item = ManualItem(src);
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([item], RunMode.AnalyzeAndEncode);

        Assert.True(item.Status == ItemStatus.Deleted, $"{item.Status}: {item.ErrorMessage} / {item.ErrorWhy}");
        Assert.Null(item.Search);                                     // no ab-av1 analysis in Manual mode
        Assert.Null(item.LastCrfSearchCommand);
        Assert.StartsWith(CommandLineQuote(tools.FfmpegPath!), item.LastEncodeCommand);
        Assert.Contains("-c:v:0 libsvtav1", item.LastEncodeCommand);
        Assert.False(File.Exists(src));
        var probe = await FfprobeService.ProbeAsync(tools.FfprobePath!, item.OutputPath!);
        Assert.Equal("av1", probe.MainVideo!.Codec);
        Assert.Equal("yuv420p10le", probe.MainVideo.PixFmt);
        Assert.Equal("eng", probe.Audio.Single().Language);
        Assert.Single(probe.Subtitles);
        Assert.Equal(2, probe.ChapterCount);
        Assert.Equal("Test Movie", probe.Title);
    }

    private static string CommandLineQuote(string exe) => AV1Studio.Util.CommandLine.Quote(exe);

    [Fact]
    public async Task Manual_svt_preserves_hdr10_metadata()
    {
        if (!Enabled) return;
        var dir = Path.Combine(_root, "hdr");
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "hdr10.mkv");
        var (code, _, err) = await ChildProcess.RunCaptureAsync(Path.Combine(ToolsDir!, "ffmpeg.exe"),
        [
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=640x360:r=24:d=4",
            "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
            "-x265-params", "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:hdr10=1:" +
                            "master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", src,
        ], timeout: TimeSpan.FromMinutes(3));
        Assert.True(code == 0, err);

        var s = Settings(Path.Combine(_root, "Out hdr"));
        s.Manual = new ManualSettings { Encoder = "libsvtav1", Quality = 35, Preset = "12" };
        var tools = await ToolLocator.DetectAsync(s);
        var srcProbe = await FfprobeService.ProbeAsync(tools.FfprobePath!, src);
        Assert.Equal("HDR10", srcProbe.HdrFormat);

        var item = ManualItem(src);
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([item], RunMode.AnalyzeAndEncode);
        Assert.True(item.Status == ItemStatus.Completed, $"{item.Status}: {item.ErrorMessage}");

        var outProbe = await FfprobeService.ProbeAsync(tools.FfprobePath!, item.OutputPath!);
        var v = outProbe.MainVideo!;
        Assert.Equal("av1", v.Codec);
        Assert.Equal("smpte2084", v.ColorTransfer);
        Assert.Equal("bt2020", v.ColorPrimaries);
        Assert.Equal("yuv420p10le", v.PixFmt);
        Assert.Equal("HDR10", outProbe.HdrFormat);
        Assert.Equal("1000,400", v.ContentLight);
        Assert.NotNull(v.MasteringDisplay);
    }

    [Fact]
    public async Task Manual_nvenc_pipeline_when_available()
    {
        if (!Enabled) return;
        var s = Settings(Path.Combine(_root, "Out nv"));
        var tools = await ToolLocator.DetectAsync(s);
        if (!tools.ManualEncoders.Contains("av1_nvenc")) return;
        var src = await MakeSourceAsync("nv.mkv");
        s.Manual = new ManualSettings { Encoder = "av1_nvenc", Quality = 30, Preset = "p5" };
        var item = ManualItem(src);
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([item], RunMode.AnalyzeAndEncode);
        Assert.True(item.Status == ItemStatus.Completed, $"{item.Status}: {item.ErrorMessage}");
        Assert.Contains("av1_nvenc", item.LastEncodeCommand);
        Assert.Equal("av1", (await FfprobeService.ProbeAsync(tools.FfprobePath!, item.OutputPath!)).MainVideo!.Codec);
    }

    [Fact]
    public async Task Preview_encodes_a_segment_with_the_selected_settings()
    {
        if (!Enabled) return;
        var src = await MakeSourceAsync("preview.mkv");
        var s = Settings(Path.Combine(_root, "Out"));
        s.Manual = new ManualSettings { Encoder = "libsvtav1", Quality = 38, Preset = "12" };
        var tools = await ToolLocator.DetectAsync(s);
        var item = ManualItem(src);
        item.Probe = await FfprobeService.ProbeAsync(tools.FfprobePath!, src);

        var r = await PreviewService.RunAsync(item, EncodeMode.Manual, s, tools, 5, CancellationToken.None);
        Assert.True(r.Size > 0);
        Assert.InRange(r.Seconds, 4, 6);
        Assert.NotNull(r.SourceFrame);
        Assert.NotNull(r.EncodedFrame);
        Assert.NotNull(r.SourceSegmentPath);
        Assert.Contains("-crf:v:0 38", r.Command);
        Assert.True(r.EstimatedFullSize > r.Size);
        Assert.True(File.Exists(src));                 // the source is never touched by a preview
        Assert.Equal(ItemStatus.Waiting, item.Status); // and the queue item is unchanged
    }

    [Fact]
    public async Task Cancelling_one_file_lets_the_queue_continue()
    {
        if (!Enabled) return;
        var slow = await MakeSourceAsync("slow.mkv", seconds: 120, size: "1920x1080");
        var fast = await MakeSourceAsync("fast.mkv");
        var dest = Path.Combine(_root, "Out");
        var s = Settings(dest);
        s.Manual = new ManualSettings { Encoder = "libsvtav1", Quality = 35, Preset = "4" };
        var tools = await ToolLocator.DetectAsync(s);

        var a = ManualItem(slow);
        var b = ManualItem(fast);
        var qp = new QueueProcessor(s, tools, new AnalysisCache());
        a.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueItem.Progress) && a.Progress > 0.5) qp.StopItem(a, ItemStatus.Cancelled);
        };
        s.Manual.Preset = "4";
        var run = qp.RunAsync([a, b], RunMode.AnalyzeAndEncode);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(4))));

        Assert.Equal(ItemStatus.Cancelled, a.Status);
        Assert.True(File.Exists(slow));
        Assert.True(b.Status == ItemStatus.Completed, $"{b.Status}: {b.ErrorMessage}");
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(dest, "Sub folder")), OutputPlanner.IsOurTemporaryFile);
    }

    [Fact]
    public async Task Analysis_is_cached_and_reused()
    {
        if (!Enabled) return;
        var src = await MakeSourceAsync("cache.mkv");
        var s = Settings(Path.Combine(_root, "Out"));
        var tools = await ToolLocator.DetectAsync(s);
        var cache = new AnalysisCache();

        var first = ItemFor(src);
        await new QueueProcessor(s, tools, cache).RunAsync([first], RunMode.AnalyzeOnly);
        Assert.Equal(ItemStatus.CrfFound, first.Status);
        Assert.Null(first.OutputPath); // analyze-only never encodes

        // Same file re-added (fresh queue item): no new crf-search command should run.
        var second = ItemFor(src);
        await new QueueProcessor(s, tools, cache).RunAsync([second], RunMode.AnalyzeOnly);
        Assert.Equal(first.Search!.Crf, second.Search!.Crf);
        Assert.Null(second.LastCrfSearchCommand);

        // Changing the target VMAF invalidates the cached result.
        var s2 = s.Clone(); s2.TargetVmaf = 85;
        var third = ItemFor(src);
        await new QueueProcessor(s2, tools, cache).RunAsync([third], RunMode.AnalyzeOnly);
        Assert.NotNull(third.LastCrfSearchCommand);
    }
}
