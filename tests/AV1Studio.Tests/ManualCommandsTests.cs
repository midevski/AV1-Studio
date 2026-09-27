using AV1Studio.Models;
using AV1Studio.Services;
using static AV1Studio.Tests.CommandBuilderTests;

namespace AV1Studio.Tests;

public class ManualCommandsTests
{
    private static ProbeInfo HdrProbe()
    {
        var p = Probe(Audio(1, 0, "truehd", "eng", 8), Sub(2, 0, "hdmv_pgs_subtitle", "eng"));
        var v = p.MainVideo!;
        v.Height = 2160; v.Width = 3840; v.FrameRate = 24000 / 1001.0;
        v.ColorPrimaries = "bt2020"; v.ColorTransfer = "smpte2084"; v.ColorSpace = "bt2020nc"; v.ColorRange = "tv";
        v.MasteringDisplay = "G(0.265,0.69)B(0.15,0.06)R(0.68,0.32)WP(0.3127,0.329)L(1000,0.0001)";
        v.ContentLight = "1000,400";
        return p;
    }

    private static List<string> Build(ManualSettings m, ProbeInfo p, string ext = "mkv") =>
        ManualCommands.Build(m, p, @"D:\in (1).mkv", @"E:\out.partial.mkv", ext,
            AbAv1Commands.PlanStreams(TrackOptions.FromManual(m), p, ext, null, null), m.Quality, @"C:\p.txt", failFast: true);

    [Fact]
    public void Svt_defaults_copy_everything_and_never_touch_resolution_or_fps()
    {
        var m = new ManualSettings();
        var a = string.Join(" ", Build(m, Probe(Audio(1, 0, "ac3"))));
        Assert.Contains("-map 0 -c copy -c:v:0 libsvtav1", a);
        Assert.Contains("-pix_fmt:v:0 yuv420p10le -crf:v:0 30 -preset:v:0 5", a);
        Assert.Contains("-svtav1-params:v:0 scd=1", a);
        Assert.DoesNotContain("-filter:v", a);
        Assert.DoesNotContain("-c:a", a);                  // audio copied by "-c copy"
        Assert.Contains("-n", Build(m, Probe()));           // never overwrite
        Assert.Contains("-xerror", a);
        Assert.EndsWith(@"-progress C:\p.txt -f matroska E:\out.partial.mkv", a);
        Assert.DoesNotContain("-g:v:0", a);                // fps unknown → "10s" can't be converted: encoder default

        var p24 = Probe();
        p24.MainVideo!.FrameRate = 24;
        Assert.Contains("-g:v:0 240", string.Join(" ", Build(m, p24))); // 10 s × 24 fps
    }

    [Fact]
    public void Hdr10_metadata_and_colour_tags_are_preserved_for_svt()
    {
        var a = string.Join(" ", Build(new ManualSettings(), HdrProbe()));
        Assert.Contains("-color_primaries:v:0 bt2020 -color_trc:v:0 smpte2084 -colorspace:v:0 bt2020nc -color_range:v:0 tv", a);
        Assert.Contains("enable-hdr=1", a);
        Assert.Contains("mastering-display=G(0.265,0.69)B(0.15,0.06)R(0.68,0.32)WP(0.3127,0.329)L(1000,0.0001)", a);
        Assert.Contains("content-light=1000,400", a);
        Assert.Empty(ManualCommands.Warnings(new ManualSettings(), HdrProbe()));
    }

    [Fact]
    public void Destructive_hdr_choices_warn()
    {
        Assert.NotEmpty(ManualCommands.Warnings(new ManualSettings { BitDepth = BitDepth.Bit8 }, HdrProbe()));
        var tm = new ManualSettings { Hdr = HdrHandling.ToneMapToSdr };
        Assert.NotEmpty(ManualCommands.Warnings(tm, HdrProbe()));
        var a = string.Join(" ", Build(tm, HdrProbe()));
        Assert.Contains("tonemap=hable", a);
        Assert.Contains("-color_trc:v:0 bt709", a);
        Assert.DoesNotContain("mastering-display", a);
        Assert.NotEmpty(ManualCommands.Warnings(new ManualSettings { Encoder = "av1_nvenc", Preset = "p5" }, HdrProbe()));
    }

    [Fact]
    public void Nvenc_qsv_amf_use_their_own_quality_controls()
    {
        var n = string.Join(" ", Build(new ManualSettings { Encoder = "av1_nvenc", Preset = "p6", Quality = 28 }, Probe()));
        Assert.Contains("-c:v:0 av1_nvenc -pix_fmt:v:0 p010le -rc:v:0 vbr -cq:v:0 28 -b:v:0 0 -preset:v:0 p6 -tune:v:0 hq", n);
        Assert.DoesNotContain("svtav1-params", n);
        var q = string.Join(" ", Build(new ManualSettings { Encoder = "av1_qsv", Preset = "slow", Quality = 25 }, Probe()));
        Assert.Contains("-global_quality:v:0 25 -preset:v:0 slow", q);
        var amf = string.Join(" ", Build(new ManualSettings { Encoder = "av1_amf", Preset = "quality", Quality = 100, BitDepth = BitDepth.Bit8 }, Probe()));
        Assert.Contains("-pix_fmt:v:0 nv12 -rc:v:0 cqp -qp_i:v:0 100 -qp_p:v:0 100 -quality:v:0 quality", amf);
    }

    [Fact]
    public void Filters_are_off_by_default_and_ordered_when_enabled()
    {
        var m = new ManualSettings();
        Assert.Equal("No processing", ManualCommands.FilterSummary(m));
        m.Deinterlace = Deinterlace.Auto; m.Crop = "1920:800:0:140"; m.Denoise = Denoise.Light; m.Resolution = ResolutionMode.P1080;
        m.Fps = "24"; m.Grayscale = true;
        var vf = ManualCommands.VideoFilter(m, HdrProbe());
        Assert.Equal("bwdif=mode=send_frame:deint=interlaced,crop=1920:800:0:140,hqdn3d=2:1.5:3:2.25,scale=-2:1080:flags=lanczos,fps=24,hue=s=0", vf);
        // never upscales with the preset heights
        Assert.Null(ManualCommands.VideoFilter(new ManualSettings { Resolution = ResolutionMode.P2160 }, Probe()));
    }

    [Fact]
    public void Tracks_container_and_subtitle_flags()
    {
        var m = new ManualSettings
        {
            AudioMode = AudioMode.Transcode, AudioCodec = "aac", AudioBitrate = "192k", DownmixToStereo = true,
            SubtitleMode = SubtitleMode.CopyAll, DefaultSubtitleLanguage = "fre", Container = ContainerFormat.Mkv,
        };
        var p = Probe(Audio(1, 0, "dts", "eng", 6), Sub(2, 0, "subrip", "eng"), Sub(3, 1, "subrip", "fre"));
        var a = string.Join(" ", Build(m, p));
        Assert.Contains("-c:a aac -ac 2 -b:a 192k", a);
        Assert.Contains("-disposition:s:0 0 -disposition:s:1 default", a);
        Assert.Contains("-dn", a);

        var pcmMp4 = AbAv1Commands.PlanStreams(TrackOptions.FromManual(new ManualSettings { AudioMode = AudioMode.Transcode, AudioCodec = "pcm_s16le" }),
            p, "mp4", null, null);
        Assert.Equal("aac", pcmMp4.AudioCodec);
        Assert.NotEmpty(pcmMp4.Warnings);

        var forced = new ManualSettings { ForcedSubtitlesOnly = true };
        var fp = Probe(Sub(1, 0, "subrip", "eng"), new StreamInfo { Index = 2, TypeIndex = 1, Type = "subtitle", Codec = "subrip", IsForced = true });
        var plan = AbAv1Commands.PlanStreams(TrackOptions.FromManual(forced), fp, "mkv", null, null);
        Assert.Equal(1, plan.ExpectedSubtitles);
        Assert.Contains("map=-0:s:0", plan.EncArgs);
    }

    [Fact]
    public void Manual_maps_main_video_even_when_cover_art_comes_first()
    {
        var p = new ProbeInfo();
        p.Streams.Add(new StreamInfo { Index = 0, TypeIndex = 0, Type = "video", Codec = "mjpeg", IsAttachedPic = true });
        p.Streams.Add(new StreamInfo { Index = 1, TypeIndex = 1, Type = "video", Codec = "h264", Height = 1080 });
        var plan = AbAv1Commands.PlanStreams(TrackOptions.FromManual(new ManualSettings()), p, "mkv", null, null);
        Assert.Null(plan.Blocker);
        Assert.Contains("-c:v:1 libsvtav1", string.Join(" ", ManualCommands.Build(new ManualSettings(), p, "i", "o", "mkv", plan, 30, null, false)));
    }

    [Fact]
    public void Validation_rejects_unavailable_encoders_and_managed_options()
    {
        var tools = new ToolStatus();
        tools.ManualEncoders.Add("libsvtav1");
        Assert.Empty(ManualCommands.Validate(new ManualSettings(), tools));
        Assert.NotEmpty(ManualCommands.Validate(new ManualSettings { Encoder = "av1_nvenc", Preset = "p5" }, tools));
        Assert.NotEmpty(ManualCommands.Validate(new ManualSettings { Quality = 70 }, tools));
        Assert.NotEmpty(ManualCommands.Validate(new ManualSettings { Preset = "p5" }, tools));
        Assert.NotEmpty(ManualCommands.Validate(new ManualSettings { ExtraFfmpegArgs = "c:v=libx264" }, tools));
        Assert.Empty(ManualCommands.Validate(new ManualSettings { ExtraFfmpegArgs = "metadata=comment=AV1" }, tools));
    }

    [Fact]
    public void Error_explanations_are_human_readable()
    {
        var e = ErrorExplainer.Explain("Encode failed: [av1_nvenc] OpenEncodeSessionEx failed: unsupported device (2): (no details)");
        Assert.Contains("hardware encoder", e.Why);
        Assert.Contains("SVT-AV1", e.Fix);
        Assert.Contains("space", ErrorExplainer.Explain("No space left on device").Why);
        Assert.NotEmpty(ErrorExplainer.Explain("something unexpected").Fix);
    }
}
