using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Tests;

/// <summary>
/// MP4 sources through the complete pipeline (probe → encode → verify → delete source) in both modes.
/// Covers typical MP4 contents: text subtitles, chapters, timecode data tracks and embedded cover art.
/// Needs AV1STUDIO_TEST_TOOLS (see EndToEndTests); otherwise a no-op.
/// </summary>
[Collection("e2e")]
public class Mp4Tests : IDisposable
{
    private static readonly string? ToolsDir = Environment.GetEnvironmentVariable("AV1STUDIO_TEST_TOOLS");
    private static bool Enabled => ToolsDir != null && File.Exists(Path.Combine(ToolsDir, "ab-av1.exe"));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av1studio-mp4-" + Guid.NewGuid().ToString("N")[..8]);

    public Mp4Tests()
    {
        AppPaths.Root = Path.Combine(_root, "appdata");
        AppPaths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Ffmpeg => Path.Combine(ToolsDir!, "ffmpeg.exe");

    private async Task<string> MakeMp4Async(string variant)
    {
        var dir = Path.Combine(_root, "Source", "Movies");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, $"movie {variant}.mp4");
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=s=640x360:r=24:d=12",
            "-f", "lavfi", "-i", "sine=f=440:d=12",
        };
        var maps = new List<string> { "-map", "0:v", "-map", "1:a" };
        var codecs = new List<string> { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-pix_fmt", "yuv420p", "-c:a", "aac" };
        switch (variant)
        {
            case "subs":
                var srt = Path.Combine(_root, "s.srt");
                File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:04,000\nHello\n");
                var meta = Path.Combine(_root, "m.txt");
                File.WriteAllText(meta, ";FFMETADATA1\ntitle=Test\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=6000\ntitle=A\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=6000\nEND=12000\ntitle=B\n");
                args.AddRange(["-i", srt, "-i", meta]);
                maps.AddRange(["-map", "2:s", "-map_metadata", "3", "-map_chapters", "3"]);
                codecs.AddRange(["-c:s", "mov_text", "-metadata:s:s:0", "language=eng"]);
                break;
            case "timecode":
                codecs.AddRange(["-timecode", "01:00:00:00"]); // adds a tmcd data track
                break;
            case "cover":
                var png = Path.Combine(_root, "cover.png");
                var (c, _, e) = await ChildProcess.RunCaptureAsync(Ffmpeg,
                    ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=200x200:d=1", "-frames:v", "1", png]);
                Assert.True(c == 0, e);
                args.AddRange(["-i", png]);
                maps.AddRange(["-map", "2:v"]);
                codecs.AddRange(["-c:v:1", "png", "-disposition:v:1", "attached_pic"]);
                break;
        }
        args.AddRange(maps);
        args.AddRange(codecs);
        args.Add(output);
        var (code, _, err) = await ChildProcess.RunCaptureAsync(Ffmpeg, args, timeout: TimeSpan.FromMinutes(3));
        Assert.True(code == 0, err);
        return output;
    }

    private AppSettings Settings() => new()
    {
        AbAv1Path = Path.Combine(ToolsDir!, "ab-av1.exe"),
        FfmpegPath = Ffmpeg,
        FfprobePath = Path.Combine(ToolsDir!, "ffprobe.exe"),
        DestinationFolder = Path.Combine(_root, "Destination"),
        DeleteSourceAfterSuccess = true,
        Container = ContainerFormat.Mp4,
        Preset = 12, Samples = 1, SampleDuration = "2s", MaxEncodedPercent = 99, TargetVmaf = 85,
        Manual = new ManualSettings { Encoder = "libsvtav1", Quality = 40, Preset = "12" },
    };

    [Theory]
    [InlineData("plain", EncodeMode.AbAv1, "mp4")]
    [InlineData("plain", EncodeMode.AbAv1, "mkv")]
    [InlineData("plain", EncodeMode.Manual, "mp4")]
    [InlineData("plain", EncodeMode.Manual, "mkv")]
    [InlineData("subs", EncodeMode.AbAv1, "mp4")]
    [InlineData("subs", EncodeMode.AbAv1, "mkv")]
    [InlineData("subs", EncodeMode.Manual, "mp4")]
    [InlineData("subs", EncodeMode.Manual, "mkv")]
    [InlineData("timecode", EncodeMode.AbAv1, "mp4")]
    [InlineData("timecode", EncodeMode.AbAv1, "mkv")]
    [InlineData("timecode", EncodeMode.Manual, "mp4")]
    [InlineData("timecode", EncodeMode.Manual, "mkv")]
    [InlineData("cover", EncodeMode.AbAv1, "mp4")]
    [InlineData("cover", EncodeMode.AbAv1, "mkv")]
    [InlineData("cover", EncodeMode.Manual, "mp4")]
    [InlineData("cover", EncodeMode.Manual, "mkv")]
    public async Task Mp4_is_encoded_verified_and_the_source_deleted(string variant, EncodeMode mode, string output)
    {
        if (!Enabled) return;
        var src = await MakeMp4Async(variant);
        var s = Settings();
        s.Container = output == "mp4" ? ContainerFormat.Mp4 : ContainerFormat.Mkv;
        var tools = await ToolLocator.DetectAsync(s);
        Assert.True(tools.Ready, string.Join("; ", tools.Problems));

        var fi = new FileInfo(src);
        var item = new QueueItem
        {
            SourcePath = src, SourceRoot = Path.Combine(_root, "Source"), SourceSize = fi.Length, SourceModifiedUtc = fi.LastWriteTimeUtc, Mode = mode,
        };
        await new QueueProcessor(s, tools, new AnalysisCache()).RunAsync([item], RunMode.AnalyzeAndEncode);

        Assert.True(item.Status == ItemStatus.Deleted, $"{item.Status}: {item.ErrorMessage} {item.ErrorWhy} {item.StatusDetail}");
        Assert.False(File.Exists(src));
        var expected = Path.Combine(s.DestinationFolder, "Movies", $"movie {variant}.{output}"); // selected container, same name, same folder
        Assert.Equal(expected, item.OutputPath);
        var probe = await FfprobeService.ProbeAsync(tools.FfprobePath!, expected);
        Assert.Equal("av1", probe.MainVideo!.Codec);
        Assert.True(OutputContainers.Matches(OutputContainers.FromExtension(output), probe, out var actual), actual);
        Assert.Single(probe.Audio);
        Assert.Equal(12, probe.DurationSeconds!.Value, 0);
        if (variant == "subs")
        {
            Assert.Equal("eng", probe.Subtitles.Single().Language);
            Assert.Equal(2, probe.ChapterCount);
        }
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(expected)!), OutputPlanner.IsOurTemporaryFile);
    }
}
