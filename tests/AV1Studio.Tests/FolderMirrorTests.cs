using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Tests;

/// <summary>Library folder → destination replica (todonext2 §15–§30, §40). Uses real FFmpeg when AV1STUDIO_TEST_TOOLS is set.</summary>
[Collection("e2e")]
public class FolderMirrorTests : IDisposable
{
    private static readonly string? ToolsDir = Environment.GetEnvironmentVariable("AV1STUDIO_TEST_TOOLS");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av1studio-mirror-" + Guid.NewGuid().ToString("N")[..8]);

    public FolderMirrorTests()
    {
        AppPaths.Root = Path.Combine(_root, "appdata");
        AppPaths.EnsureCreated();
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static bool Enabled => ToolsDir != null && File.Exists(Path.Combine(ToolsDir, "ffmpeg.exe"));

    private async Task Video(string path, string codec = "libx264", int seconds = 3)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var (code, _, err) = await ChildProcess.RunCaptureAsync(Path.Combine(ToolsDir!, "ffmpeg.exe"),
        [
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc=s=320x240:r=24:d={seconds}",
            "-f", "lavfi", "-i", $"sine=d={seconds}", "-c:v", codec, "-c:a", "aac", "-shortest", path,
        ], timeout: TimeSpan.FromMinutes(2));
        Assert.True(code == 0, err);
    }

    private static void Text(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Bytes(string path, int n)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var b = new byte[n];
        new Random(n).NextBytes(b);
        File.WriteAllBytes(path, b);
    }

    [Fact]
    public void Scan_classifies_files_and_lists_every_directory()
    {
        var root = Path.Combine(_root, "Lib");
        Text(Path.Combine(root, "Movie", "video.mkv"), "x");
        Text(Path.Combine(root, "Movie", "poster.jpg"), "x");
        Directory.CreateDirectory(Path.Combine(root, "Empty Folder"));
        Text(Path.Combine(root, "Movie", "old.av1studio.partial.mkv"), "ours — must be ignored");
        var scan = FolderMirror.Scan(root, new AppSettings(), CancellationToken.None);
        Assert.Contains(scan.Files, f => f.Path.EndsWith("video.mkv") && f.Kind == ItemKind.Video);
        Assert.Contains(scan.Files, f => f.Path.EndsWith("poster.jpg") && f.Kind == ItemKind.Copy);
        Assert.DoesNotContain(scan.Files, f => f.Path.Contains("av1studio.partial"));
        Assert.Contains("Empty Folder", scan.Directories);

        // duplicates via different spellings of the same path are queued once
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var s = new AppSettings { DestinationFolder = Path.Combine(_root, "Out") };
        var (_, a) = FolderMirror.CreateJob(scan, s, EncodeMode.Manual, null, seen);
        var odd = new TreeScan { Root = root };
        odd.Files.Add((Path.Combine(root, "Movie", ".", "..", "Movie", "video.mkv"), ItemKind.Video));
        var (_, b) = FolderMirror.CreateJob(odd, s, EncodeMode.Manual, null, seen);
        Assert.Equal(2, a.Count);
        Assert.Empty(b);
    }

    [Fact]
    public async Task Destination_is_a_complete_replica_and_resume_does_no_work_twice()
    {
        if (!Enabled) return;
        var src = Path.Combine(_root, "Media (é)");
        await Video(Path.Combine(src, "Movie", "video.mkv"));
        Bytes(Path.Combine(src, "Movie", "poster.jpg"), 5000);
        Text(Path.Combine(src, "Movie", "subtitles.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        await Video(Path.Combine(src, "Series", "Season 1", "Episode 01.mkv"));
        await Video(Path.Combine(src, "Series", "Season 1", "Episode 02.mp4"));
        await Video(Path.Combine(src, "Series", "Season 2", "Episode 01.mkv"), codec: "libsvtav1"); // already AV1
        Bytes(Path.Combine(src, "Images", "photo.png"), 12345);
        Text(Path.Combine(src, "Documents", "info.txt"), "information");
        Directory.CreateDirectory(Path.Combine(src, "Empty Folder"));
        var deep = Path.Combine(src, "a", "b", "c", "d", "e", "f", "g", "h", "i", "j");
        Text(Path.Combine(deep, "deep.nfo"), "10 levels");
        Directory.CreateDirectory(Path.Combine(deep, "empty leaf"));

        var dest = Path.Combine(_root, "AV1 Library");
        var s = new AppSettings
        {
            FfmpegPath = Path.Combine(ToolsDir!, "ffmpeg.exe"), FfprobePath = Path.Combine(ToolsDir!, "ffprobe.exe"),
            AbAv1Path = Path.Combine(ToolsDir!, "ab-av1.exe"),
            DestinationFolder = dest, DeleteSourceAfterSuccess = true,
            Manual = new ManualSettings { Encoder = "libsvtav1", Quality = 40, Preset = "12" },
        };
        var tools = await ToolLocator.DetectAsync(s);
        var scan = FolderMirror.Scan(src, s, CancellationToken.None);
        var (job, items) = FolderMirror.CreateJob(scan, s, EncodeMode.Manual, null);

        var qp = new QueueProcessor(s, tools, new AnalysisCache()) { Folders = () => [job] };
        await qp.RunAsync(items, RunMode.AnalyzeAndEncode);
        Assert.All(items, i => Assert.True(i.Status.IsDone(), $"{i.RelativePath}: {i.Status} {i.ErrorMessage} {i.StatusDetail}"));

        // every directory of the source exists in the destination — including empty and deeply nested ones
        foreach (var d in scan.Directories) Assert.True(Directory.Exists(Path.Combine(dest, d)), "missing folder " + d);
        // non-video files: identical bytes, same relative path, sources kept
        foreach (var rel in new[] { @"Movie\poster.jpg", @"Movie\subtitles.srt", @"Images\photo.png", @"Documents\info.txt", @"a\b\c\d\e\f\g\h\i\j\deep.nfo" })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(src, rel)), File.ReadAllBytes(Path.Combine(dest, rel)));
        }
        // videos: encoded to AV1 at the same relative path with the original name (mp4 stays mp4); sources deleted
        foreach (var rel in new[] { @"Movie\video.mkv", @"Series\Season 1\Episode 01.mkv", @"Series\Season 1\Episode 02.mp4" })
        {
            var p = await FfprobeService.ProbeAsync(tools.FfprobePath!, Path.Combine(dest, rel));
            Assert.Equal("av1", p.MainVideo!.Codec);
            Assert.False(File.Exists(Path.Combine(src, rel)), "source should be deleted after verification: " + rel);
        }
        // an AV1 source is copied unchanged (not re-encoded, not deleted)
        Assert.True(File.Exists(Path.Combine(dest, @"Series\Season 2\Episode 01.mkv")));
        Assert.True(File.Exists(Path.Combine(src, @"Series\Season 2\Episode 01.mkv")));
        Assert.DoesNotContain(Directory.GetFiles(dest, "*", SearchOption.AllDirectories), OutputPlanner.IsOurTemporaryFile);

        // ---- resume: re-queue what is still in the source; nothing is re-encoded or re-copied ----
        var scan2 = FolderMirror.Scan(src, s, CancellationToken.None);
        var (job2, items2) = FolderMirror.CreateJob(scan2, s, EncodeMode.Manual, null);
        var qp2 = new QueueProcessor(s, tools, new AnalysisCache()) { Folders = () => [job2] };
        await qp2.RunAsync(items2, RunMode.AnalyzeAndEncode);
        Assert.All(items2, i => Assert.True(i.Status.IsDone(), $"{i.RelativePath}: {i.Status}"));
        Assert.All(items2, i => Assert.Null(i.LastEncodeCommand)); // no encoder was started
    }

    [Fact]
    public async Task Folder_with_only_non_video_files_completes()
    {
        if (!Enabled) return;
        var src = Path.Combine(_root, "Docs");
        Text(Path.Combine(src, "a.txt"), "a");
        Text(Path.Combine(src, "sub", "b.pdf"), "b");
        Directory.CreateDirectory(Path.Combine(src, "nothing here"));
        var dest = Path.Combine(_root, "Docs out");
        var s = new AppSettings
        {
            FfmpegPath = Path.Combine(ToolsDir!, "ffmpeg.exe"), FfprobePath = Path.Combine(ToolsDir!, "ffprobe.exe"),
            DestinationFolder = dest, DeleteSourceAfterSuccess = true, VerifyCopiesWithHash = true,
        };
        var tools = await ToolLocator.DetectAsync(s);
        var scan = FolderMirror.Scan(src, s, CancellationToken.None);
        var (job, items) = FolderMirror.CreateJob(scan, s, EncodeMode.AbAv1, null);
        await new QueueProcessor(s, tools, new AnalysisCache()) { Folders = () => [job] }.RunAsync(items, RunMode.AnalyzeAndEncode);

        Assert.All(items, i => Assert.Equal(ItemStatus.Completed, i.Status));
        Assert.Equal("b", File.ReadAllText(Path.Combine(dest, "sub", "b.pdf")));
        Assert.True(Directory.Exists(Path.Combine(dest, "nothing here")));
        Assert.True(File.Exists(Path.Combine(src, "a.txt"))); // copied files are never deleted
    }
}
