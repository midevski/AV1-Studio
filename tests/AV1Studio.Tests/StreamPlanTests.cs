using AV1Studio.Models;
using AV1Studio.Services;
using static AV1Studio.Tests.CommandBuilderTests;

namespace AV1Studio.Tests;

public class StreamPlanTests
{
    [Fact]
    public void Default_copies_everything_to_mkv()
    {
        var p = AbAv1Commands.PlanStreams(new AppSettings(),
            Probe(Audio(1, 0, "truehd", "eng", 8), Audio(2, 1, "dts", "fre", 6), Sub(3, 0, "hdmv_pgs_subtitle", "eng")), "mkv", null, null);
        Assert.Null(p.AudioCodec);
        Assert.Empty(p.EncArgs);
        Assert.Equal(2, p.ExpectedAudio);
        Assert.Equal(1, p.ExpectedSubtitles);
        Assert.Null(p.Blocker);
    }

    [Fact]
    public void Language_filter_drops_other_tracks_with_negative_maps()
    {
        var s = new AppSettings { AudioLanguages = "eng", SubtitleMode = SubtitleMode.Languages, SubtitleLanguages = "fre" };
        var p = AbAv1Commands.PlanStreams(s,
            Probe(Audio(1, 0, "ac3", "eng"), Audio(2, 1, "aac", "ger"), Sub(3, 0, "subrip", "eng"), Sub(4, 1, "subrip", "fre")), "mkv", null, null);
        Assert.Equal(["map=-0:a:1", "map=-0:s:0"], p.EncArgs);
        Assert.Equal(1, p.ExpectedAudio);
        Assert.Equal(1, p.ExpectedSubtitles);
    }

    [Fact]
    public void Language_filter_never_removes_all_audio()
    {
        var s = new AppSettings { AudioLanguages = "jpn" };
        var p = AbAv1Commands.PlanStreams(s, Probe(Audio(1, 0, "ac3", "eng")), "mkv", null, null);
        Assert.Empty(p.EncArgs);
        Assert.Equal(1, p.ExpectedAudio);
        Assert.NotEmpty(p.Warnings);
    }

    [Fact]
    public void Per_file_selection_wins()
    {
        var p = AbAv1Commands.PlanStreams(new AppSettings(),
            Probe(Audio(1, 0, "ac3"), Audio(2, 1, "aac"), Audio(3, 2, "aac")), "mkv", [0, 2], []);
        Assert.Equal(["map=-0:a:1"], p.EncArgs);
        Assert.Equal(2, p.ExpectedAudio);
    }

    [Fact]
    public void Remove_modes()
    {
        var s = new AppSettings { AudioMode = AudioMode.Remove, SubtitleMode = SubtitleMode.Remove };
        var p = AbAv1Commands.PlanStreams(s, Probe(Audio(1, 0, "ac3"), Sub(2, 0, "subrip")), "mkv", null, null);
        Assert.Equal(["an", "sn"], p.EncArgs);
        Assert.Equal(0, p.ExpectedAudio);
        Assert.Equal(0, p.ExpectedSubtitles);
    }

    [Fact]
    public void Transcode_uses_acodec_and_bitrate()
    {
        var s = new AppSettings { AudioMode = AudioMode.Transcode, AudioCodec = "libopus", AudioBitrate = "160k", DownmixToStereo = true };
        var p = AbAv1Commands.PlanStreams(s, Probe(Audio(1, 0, "dts", ch: 6)), "mkv", null, null);
        Assert.Equal("libopus", p.AudioCodec);
        Assert.True(p.Downmix);
        Assert.Equal(["b:a=160k"], p.EncArgs);
    }

    [Fact]
    public void Mp4_reencodes_only_incompatible_audio_and_drops_bitmap_subs()
    {
        var p = AbAv1Commands.PlanStreams(new AppSettings(),
            Probe(Audio(1, 0, "aac"), Audio(2, 1, "truehd", ch: 8), Sub(3, 0, "subrip"), Sub(4, 1, "hdmv_pgs_subtitle"),
                  new StreamInfo { Index = 5, TypeIndex = 0, Type = "attachment", Codec = "ttf" }), "mp4", null, null);
        Assert.Contains("c:a:1=libopus", p.EncArgs);
        Assert.Contains("b:a:1=512k", p.EncArgs);
        Assert.DoesNotContain(p.EncArgs, a => a.StartsWith("c:a:0"));
        Assert.Contains("map=-0:s:1", p.EncArgs);
        Assert.Contains("c:s=mov_text", p.EncArgs);
        Assert.Contains("map=-0:t?", p.EncArgs);
        Assert.Equal(1, p.ExpectedSubtitles);
    }

    [Fact]
    public void Cover_art_first_is_blocked()
    {
        var probe = new ProbeInfo();
        probe.Streams.Add(new StreamInfo { Index = 0, TypeIndex = 0, Type = "video", Codec = "mjpeg", IsAttachedPic = true });
        probe.Streams.Add(new StreamInfo { Index = 1, TypeIndex = 1, Type = "video", Codec = "h264" });
        Assert.NotNull(AbAv1Commands.PlanStreams(new AppSettings(), probe, "mkv", null, null).Blocker);
    }
}
