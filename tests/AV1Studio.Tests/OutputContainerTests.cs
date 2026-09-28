using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Tests;

public class OutputContainerTests
{
    private static readonly ToolStatus Tools = new()
    {
        AbAv1Path = "ab-av1.exe", CrfSearchJson = true, EncodeVerify = true, EncodeFailFast = true, HasTempDirArg = true,
        AbAv1SemVer = new Version(0, 11, 7), SvtAv1SemVer = new Version(4, 2, 0),
    };

    private static ProbeInfo Probe()
    {
        var p = new ProbeInfo { DurationSeconds = 60, FormatName = "matroska,webm" };
        p.Streams.Add(new StreamInfo { Index = 0, TypeIndex = 0, Type = "video", Codec = "h264", Width = 1920, Height = 1080, FrameRate = 24 });
        p.Streams.Add(new StreamInfo { Index = 1, TypeIndex = 0, Type = "audio", Codec = "ac3", Channels = 6 });
        return p;
    }

    [Fact]
    public void Default_is_mkv_and_the_choice_is_persisted()
    {
        Assert.Equal(ContainerFormat.Mkv, new AppSettings().Container);
        var s = new AppSettings { Container = ContainerFormat.Mp4 };
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(s, JsonFile.Options), JsonFile.Options)!;
        loaded.Upgrade();
        Assert.Equal(ContainerFormat.Mp4, loaded.Container);
    }

    [Theory]
    [InlineData(ContainerFormat.Mkv, @"x\Movie.mp4", "mkv")]
    [InlineData(ContainerFormat.Mp4, @"x\Movie.mkv", "mp4")]
    [InlineData(ContainerFormat.Mkv, @"x\Movie.mkv", "mkv")]
    [InlineData(ContainerFormat.Mp4, @"x\Movie.mp4", "mp4")]
    public void The_selected_container_decides_the_extension_not_the_source(ContainerFormat c, string source, string ext)
    {
        Assert.Equal(ext, OutputPlanner.ContainerExtension(c, source));
        var s = new AppSettings { Container = c, DestinationFolder = @"C:\out", Naming = NamingMode.Auto };
        var item = new QueueItem { SourcePath = source, SourceRoot = "x" };
        var plan = OutputPlanner.Plan(s, item, 30);
        Assert.Equal($"Movie.{ext}", Path.GetFileName(plan.FinalPath));
        Assert.EndsWith("." + ext, plan.PartialPath);          // temporary file carries the same container
        Assert.True(OutputPlanner.IsOurTemporaryFile(plan.PartialPath)); // and is never taken for a finished file
    }

    [Fact]
    public void Ab_av1_names_the_muxer_explicitly()
    {
        var s = new AppSettings { Container = ContainerFormat.Mp4 };
        var streams = AbAv1Commands.PlanStreams(s, Probe(), "mp4", null, null);
        var a = string.Join(" ", AbAv1Commands.Encode(s, Tools, Probe(), "in.mkv", 30, @"o\Movie.av1studio.partial.mp4", streams, null));
        Assert.Contains("--enc f=mp4 --enc movflags=+faststart", a);
        Assert.DoesNotContain("matroska", a);

        var mkv = string.Join(" ", AbAv1Commands.Encode(new AppSettings(), Tools, Probe(), "in.mp4", 30, @"o\Movie.av1studio.partial.mkv",
            AbAv1Commands.PlanStreams(new AppSettings(), Probe(), "mkv", null, null), null));
        Assert.Contains("--enc f=matroska", mkv);
        Assert.DoesNotContain("movflags", mkv);
    }

    [Theory]
    [InlineData("mkv", "matroska")]
    [InlineData("mp4", "mp4")]
    public void Manual_ffmpeg_command_uses_the_container_muxer(string ext, string muxer)
    {
        var m = new ManualSettings { Encoder = "libsvtav1", Quality = 30, Preset = "6" };
        var streams = AbAv1Commands.PlanStreams(TrackOptions.FromManual(m), Probe(), ext, null, null);
        var a = ManualCommands.Build(m, Probe(), "in.mkv", $"out.av1studio.partial.{ext}", ext, streams, 30, null, false);
        Assert.Equal(["-f", muxer, $"out.av1studio.partial.{ext}"], a.TakeLast(3).ToArray());
        Assert.Equal(ext == "mp4", a.Contains("+faststart"));
        Assert.Contains("yuv420p10le", a); // 10-bit is kept in both containers
    }

    [Fact]
    public void Mp4_output_handles_incompatible_audio_and_subtitles_explicitly()
    {
        var p = Probe();
        p.Streams.Add(new StreamInfo { Index = 2, TypeIndex = 1, Type = "audio", Codec = "truehd", Channels = 8 });
        p.Streams.Add(new StreamInfo { Index = 3, TypeIndex = 0, Type = "subtitle", Codec = "subrip", Language = "eng" });
        p.Streams.Add(new StreamInfo { Index = 4, TypeIndex = 1, Type = "subtitle", Codec = "hdmv_pgs_subtitle", Language = "fre" });
        var plan = AbAv1Commands.PlanStreams(new AppSettings(), p, "mp4", null, null);
        Assert.Contains("c:a:1=libopus", plan.EncArgs);   // TrueHD converted, AC-3 copied
        Assert.DoesNotContain("c:a:0=libopus", plan.EncArgs);
        Assert.Contains("c:s=mov_text", plan.EncArgs);     // text subtitles converted to the MP4 format
        Assert.Equal(1, plan.ExpectedSubtitles);            // image subtitle cannot be stored …
        Assert.Contains(plan.Warnings, w => w.Contains("dropped"));  // … and that is reported
        var mkv = AbAv1Commands.PlanStreams(new AppSettings(), p, "mkv", null, null);
        Assert.Equal(2, mkv.ExpectedSubtitles);
        Assert.Empty(mkv.Warnings);
    }

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "isom", "mp4", true)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "qt  ", "mp4", false)]   // QuickTime MOV is not MP4
    [InlineData("matroska,webm", null, "mp4", false)]
    [InlineData("matroska,webm", null, "mkv", true)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "isom", "mkv", false)]
    public void The_real_container_is_checked_not_the_file_name(string format, string? brand, string ext, bool ok)
    {
        var probe = new ProbeInfo { FormatName = format, MajorBrand = brand };
        Assert.Equal(ok, OutputContainers.Matches(OutputContainers.FromExtension(ext), probe, out _));
    }
}
