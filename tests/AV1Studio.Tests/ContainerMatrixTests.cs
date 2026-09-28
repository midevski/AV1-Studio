using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Tests;

/// <summary>
/// Output container (MKV / MP4) × mode × input container × bit depth, with real encodes, FFprobe checks of the
/// actual container and source deletion. Needs AV1STUDIO_TEST_TOOLS (see EndToEndTests); otherwise a no-op.
/// </summary>
[Collection("e2e")]
public class ContainerMatrixTests : IDisposable
{
    private static readonly string? ToolsDir = Environment.GetEnvironmentVariable("AV1STUDIO_TEST_TOOLS");
    private static bool Enabled => ToolsDir != null && File.Exists(Path.Combine(ToolsDir, "ab-av1.exe"));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av1studio-containers-" + Guid.NewGuid().ToString("N")[..8]);

    public ContainerMatrixTests()
    {
        AppPaths.Root = Path.Combine(_root, "appdata");
        AppPaths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Ffmpeg => Path.Combine(ToolsDir!, "ffmpeg.exe");
    private string Ffprobe => Path.Combine(ToolsDir!, "ffprobe.exe");

    /// <summary>Video (optionally 10-bit HDR10-tagged), AC-3 audio, an English text subtitle, chapters and a title.</summary>
    private async Task MakeVideoAsync(string path, bool hdr = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var srt = Path.Combine(_root, "s.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:04,000\nHello\n");
        var meta = Path.Combine(_root, "m.txt");
        File.WriteAllText(meta, ";FFMETADATA1\ntitle=Test Title\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=5000\ntitle=A\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=5000\nEND=10000\ntitle=B\n");
        bool mp4 = path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=s=640x360:r=24:d=10", "-f", "lavfi", "-i", "sine=f=440:d=10",
            "-i", srt, "-i", meta,
            "-map", "0:v", "-map", "1:a", "-map", "2:s", "-map_metadata", "3", "-map_chapters", "3",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12",
            "-c:a", "ac3", "-c:s", mp4 ? "mov_text" : "srt", "-metadata:s:s:0", "language=eng",
        };
        if (hdr)
            args.AddRange(["-vf", "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv",
                           "-pix_fmt", "yuv420p10le", "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", "-color_range", "tv"]);
        else
            args.AddRange(["-pix_fmt", "yuv420p"]);
        args.Add(path);
        var (code, _, err) = await ChildProcess.RunCaptureAsync(Ffmpeg, args, timeout: TimeSpan.FromMinutes(3));
        Assert.True(code == 0, err);
    }

    private AppSettings Settings(ContainerFormat container, string dest, BitDepth depth = BitDepth.Bit10) => new()
    {
        AbAv1Path = Path.Combine(ToolsDir!, "ab-av1.exe"), FfmpegPath = Ffmpeg, FfprobePath = Ffprobe,
        DestinationFolder = dest, DeleteSourceAfterSuccess = true, Container = container,
        Preset = 12, Samples = 1, SampleDuration = "2s", MaxEncodedPercent = 99, TargetVmaf = 85,
        Manual = new ManualSettings { Encoder = "libsvtav1", Quality = 40, Preset = "12", BitDepth = depth },
    };

    private static QueueItem Item(string path, string root, EncodeMode mode)
    {
        var fi = new FileInfo(path);
        return new QueueItem { SourcePath = path, SourceRoot = root, SourceSize = fi.Length, SourceModifiedUtc = fi.LastWriteTimeUtc, Mode = mode };
    }

    private static void AssertContainer(ProbeInfo p, string ext)
    {
        Assert.True(OutputContainers.Matches(OutputContainers.FromExtension(ext), p, out var actual), $"expected {ext}, found {actual}");
        Assert.Equal("av1", p.MainVideo!.Codec);
    }

    [Theory]
    // Manual AV1: both containers × both bit depths, from MKV and MP4 sources
    [InlineData(EncodeMode.Manual, "mkv", ContainerFormat.Mkv, BitDepth.Bit8)]
    [InlineData(EncodeMode.Manual, "mkv", ContainerFormat.Mkv, BitDepth.Bit10)]
    [InlineData(EncodeMode.Manual, "mkv", ContainerFormat.Mp4, BitDepth.Bit8)]
    [InlineData(EncodeMode.Manual, "mkv", ContainerFormat.Mp4, BitDepth.Bit10)]
    [InlineData(EncodeMode.Manual, "mp4", ContainerFormat.Mkv, BitDepth.Bit10)]
    [InlineData(EncodeMode.Manual, "mp4", ContainerFormat.Mp4, BitDepth.Bit10)]
    // AB-AV1 (always 10-bit by default)
    [InlineData(EncodeMode.AbAv1, "mkv", ContainerFormat.Mkv, BitDepth.Bit10)]
    [InlineData(EncodeMode.AbAv1, "mkv", ContainerFormat.Mp4, BitDepth.Bit10)]
    [InlineData(EncodeMode.AbAv1, "mp4", ContainerFormat.Mkv, BitDepth.Bit10)]
    [InlineData(EncodeMode.AbAv1, "mp4", ContainerFormat.Mp4, BitDepth.Bit10)]
    public async Task Output_uses_the_selected_container(EncodeMode mode, string inputExt, ContainerFormat container, BitDepth depth)
    {
        if (!Enabled) return;
        var root = Path.Combine(_root, "Source");
        var src = Path.Combine(root, "Movies", $"Movie.{inputExt}");
        await MakeVideoAsync(src);
        var dest = Path.Combine(_root, "Destination");
        var s = Settings(container, dest, depth);
        var tools = await ToolLocator.DetectAsync(s);
        Assert.True(tools.CanWriteAv1Mp4);

        var item = Item(src, root, mode);
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([item], RunMode.AnalyzeAndEncode);

        var ext = container == ContainerFormat.Mp4 ? "mp4" : "mkv";
        Assert.True(item.Status == ItemStatus.Deleted, $"{item.Status}: {item.ErrorMessage} {item.ErrorWhy} {item.StatusDetail}");
        Assert.Equal(Path.Combine(dest, "Movies", $"Movie.{ext}"), item.OutputPath);
        Assert.False(File.Exists(src));
        Assert.Single(Directory.GetFiles(Path.Combine(dest, "Movies"))); // no stray temporary or other-container file

        var p = await FfprobeService.ProbeAsync(Ffprobe, item.OutputPath!);
        AssertContainer(p, ext);
        Assert.Equal(depth == BitDepth.Bit8 && mode == EncodeMode.Manual ? 8 : 10, p.BitDepth);
        Assert.Single(p.Audio);
        Assert.Equal("eng", p.Subtitles.Single().Language);   // text subtitles survive in both containers
        Assert.Equal(2, p.ChapterCount);
        Assert.Equal("Test Title", p.Title);
        Assert.Equal(10, p.DurationSeconds!.Value, 0);
        Assert.Contains(item.ItemLog, l => l.Contains($"Container is {ext.ToUpperInvariant()}"));
    }

    [Theory]
    [InlineData(ContainerFormat.Mkv)]
    [InlineData(ContainerFormat.Mp4)]
    public async Task Hdr_colour_information_is_kept_in_both_containers(ContainerFormat container)
    {
        if (!Enabled) return;
        var root = Path.Combine(_root, "HdrSource");
        var src = Path.Combine(root, "hdr.mkv");
        await MakeVideoAsync(src, hdr: true);
        var s = Settings(container, Path.Combine(_root, "HdrOut"));
        var tools = await ToolLocator.DetectAsync(s);
        var item = Item(src, root, EncodeMode.Manual);
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([item], RunMode.AnalyzeAndEncode);
        Assert.True(item.Status == ItemStatus.Deleted, $"{item.Status}: {item.ErrorMessage} {item.StatusDetail}");

        var v = (await FfprobeService.ProbeAsync(Ffprobe, item.OutputPath!)).MainVideo!;
        Assert.Equal("bt2020", v.ColorPrimaries);
        Assert.Equal("smpte2084", v.ColorTransfer);
        Assert.Equal(10, (await FfprobeService.ProbeAsync(Ffprobe, item.OutputPath!)).BitDepth);
    }

    [Fact]
    public async Task A_file_that_is_not_really_the_selected_container_keeps_the_source()
    {
        if (!Enabled) return;
        var root = Path.Combine(_root, "Reuse");
        var src = Path.Combine(root, "Movie.mkv");
        await MakeVideoAsync(src);
        var dest = Path.Combine(_root, "ReuseOut");
        // an MKV file named .mp4 already sits at the destination
        await MakeVideoAsync(Path.Combine(_root, "fake.mkv"));
        var ffmpegArgs = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", Path.Combine(_root, "fake.mkv"), "-map", "0:v", "-c:v", "libsvtav1", "-preset", "12", "-f", "matroska", Path.Combine(dest, "Movie.mp4") };
        Directory.CreateDirectory(dest);
        Assert.Equal(0, (await ChildProcess.RunCaptureAsync(Ffmpeg, ffmpegArgs, timeout: TimeSpan.FromMinutes(2))).Code);

        var s = Settings(ContainerFormat.Mp4, dest);
        s.Collision = CollisionPolicy.ReuseIfValid;
        var tools = await ToolLocator.DetectAsync(s);
        var item = Item(src, root, EncodeMode.Manual);
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([item], RunMode.AnalyzeAndEncode);

        Assert.NotEqual(ItemStatus.Deleted, item.Status);   // the fake MP4 is not accepted …
        Assert.True(File.Exists(src));                         // … so the source is kept
        Assert.Contains(item.ItemLog, l => l.Contains("✘ Container is MP4"));
    }

    [Fact]
    public async Task Existing_output_in_the_other_container_is_not_taken_for_the_new_one()
    {
        if (!Enabled) return;
        var root = Path.Combine(_root, "Switch");
        var src = Path.Combine(root, "Movie.mkv");
        await MakeVideoAsync(src);
        var dest = Path.Combine(_root, "SwitchOut");
        var s = Settings(ContainerFormat.Mkv, dest);
        s.DeleteSourceAfterSuccess = false;
        var tools = await ToolLocator.DetectAsync(s);
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([Item(src, root, EncodeMode.Manual)], RunMode.AnalyzeAndEncode);
        Assert.True(File.Exists(Path.Combine(dest, "Movie.mkv")));

        // switching to MP4: Movie.mkv must not count as an existing Movie.mp4
        s.Container = ContainerFormat.Mp4;
        var item = Item(src, root, EncodeMode.Manual);
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([item], RunMode.AnalyzeAndEncode);
        Assert.Equal(ItemStatus.Completed, item.Status);
        Assert.Equal(Path.Combine(dest, "Movie.mp4"), item.OutputPath);
        Assert.Null(item.ItemLog.FirstOrDefault(l => l.Contains("reused")));
    }

    [Fact]
    public async Task Folder_replica_uses_the_selected_container_for_every_video()
    {
        if (!Enabled) return;
        var src = Path.Combine(_root, "Library");
        await MakeVideoAsync(Path.Combine(src, "Movies", "Avatar", "Avatar.mkv"));
        await MakeVideoAsync(Path.Combine(src, "Movies", "Interstellar", "Interstellar.mp4"));
        await MakeVideoAsync(Path.Combine(src, "Series", "Season 1", "Episode 01.mp4"));
        File.WriteAllBytes(Path.Combine(src, "Movies", "Avatar", "poster.jpg"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(src, "Movies", "Avatar", "Avatar.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        Directory.CreateDirectory(Path.Combine(src, "Documents"));
        File.WriteAllText(Path.Combine(src, "Documents", "info.txt"), "information");
        Directory.CreateDirectory(Path.Combine(src, "Empty"));

        var dest = Path.Combine(_root, "Replica");
        var s = Settings(ContainerFormat.Mp4, dest);
        var tools = await ToolLocator.DetectAsync(s);
        var scan = FolderMirror.Scan(src, s, CancellationToken.None);
        var (job, items) = FolderMirror.CreateJob(scan, s, EncodeMode.Manual, null);
        await new QueueProcessor(s, tools, new AnalysisCache()) { Folders = () => [job] }.RunAsync(items, RunMode.AnalyzeAndEncode);
        Assert.All(items, i => Assert.True(i.Status.IsDone(), $"{i.RelativePath}: {i.Status} {i.ErrorMessage}"));

        foreach (var rel in new[] { @"Movies\Avatar\Avatar.mp4", @"Movies\Interstellar\Interstellar.mp4", @"Series\Season 1\Episode 01.mp4" })
            AssertContainer(await FfprobeService.ProbeAsync(Ffprobe, Path.Combine(dest, rel)), "mp4");
        Assert.False(File.Exists(Path.Combine(dest, "Movies", "Avatar", "Avatar.mkv")));
        Assert.False(File.Exists(Path.Combine(src, "Movies", "Avatar", "Avatar.mkv")));          // encoded + verified → deleted
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(dest, "Movies", "Avatar", "poster.jpg")));
        Assert.True(File.Exists(Path.Combine(dest, "Movies", "Avatar", "Avatar.srt")));
        Assert.Equal("information", File.ReadAllText(Path.Combine(dest, "Documents", "info.txt")));
        Assert.True(File.Exists(Path.Combine(src, "Documents", "info.txt")));                     // copies never deleted
        Assert.True(Directory.Exists(Path.Combine(dest, "Empty")));
    }
}
