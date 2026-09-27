using AV1Studio.Services;
using AV1Studio.Util;

namespace AV1Studio.Tests;

public class ParserTests
{
    [Fact]
    public void Parses_documented_crf_search_json()
    {
        // Lines copied from ab-av1 stdout-format-json.md
        var a = CrfSearchParser.ParseStdoutLine(
            "{\"crf\":29.75,\"from_cache\":false,\"predicted_encode_percent\":13.785497397240093,\"predicted_encode_seconds\":13.0,\"predicted_encode_size\":68549279,\"type\":\"sample-encode-done\",\"vmaf\":95.16242980957031}");
        var att = Assert.IsType<AttemptEvent>(a).Attempt;
        Assert.Equal(29.75, att.Crf);
        Assert.Equal(68549279, att.PredictedSize);
        Assert.Equal(95.16, att.Vmaf!.Value, 2);

        var d = CrfSearchParser.ParseStdoutLine(
            "{\"crf\":29.75,\"from_cache\":true,\"predicted_encode_percent\":13.78,\"predicted_encode_seconds\":13.0,\"predicted_encode_size\":68549279,\"type\":\"crf-search-done\",\"vmaf\":95.16}");
        var done = Assert.IsType<DoneEvent>(d).Result;
        Assert.True(done.FromCache);
        Assert.Equal(29.75, done.Crf);

        Assert.IsType<SearchErrorEvent>(CrfSearchParser.ParseStdoutLine("{\"message\":\"Failed to find a suitable crf\",\"type\":\"crf-search-error\"}"));
        Assert.Null(CrfSearchParser.ParseStdoutLine("{\"type\":\"some-future-message\",\"x\":1}"));
        Assert.Null(CrfSearchParser.ParseStdoutLine("garbage"));
    }

    [Fact]
    public void Parses_legacy_human_result_and_old_json_without_type()
    {
        var d = CrfSearchParser.ParseStdoutLine("crf 31 VMAF 95.20 predicted video stream size 5.8 GiB (30%) taking 2 hours");
        Assert.Equal(31, Assert.IsType<DoneEvent>(d).Result.Crf);
        var a = CrfSearchParser.ParseStdoutLine("{\"crf\":30,\"vmaf\":96.1,\"predicted_encode_size\":1,\"predicted_encode_percent\":1}");
        Assert.IsType<AttemptEvent>(a);
    }

    [Fact]
    public void Stderr_activity()
    {
        Assert.Equal("Testing CRF 37.5 · sample 1/2",
            AbAv1StderrParser.Activity("[2026-09-27T14:48:04Z INFO  ab_av1::command::sample_encode] encoding sample 1/2 crf 37.5"));
        Assert.Equal("Failed to find a suitable crf", AbAv1StderrParser.ErrorMessage("Error: Failed to find a suitable crf"));
    }

    [Fact]
    public void Ffmpeg_progress_blocks()
    {
        var p = new FfmpegProgressParser();
        FfmpegProgress? last = null;
        foreach (var l in "frame=960\nfps=363.63\nbitrate=4066.1kbits/s\ntotal_size=20330545\nout_time_us=40000000\nout_time_ms=40000000\nspeed=15.2x\nprogress=end".Split('\n'))
            last = p.Feed(l) ?? last;
        Assert.NotNull(last);
        Assert.True(last!.End);
        Assert.Equal(960, last.Frame);
        Assert.Equal(40, last.OutTimeSeconds);
        Assert.Equal(15.2, last.Speed);
        Assert.Equal(20330545, last.TotalSize);
        Assert.Equal("4066.1kbits/s", last.Bitrate);
    }

    [Fact]
    public void Ffprobe_json()
    {
        const string json = """
        {"streams":[
          {"index":0,"codec_name":"h264","codec_type":"video","width":1920,"height":1080,"pix_fmt":"yuv420p","avg_frame_rate":"24000/1001","disposition":{"default":1,"attached_pic":0}},
          {"index":1,"codec_name":"ac3","codec_type":"audio","channels":6,"tags":{"language":"eng","title":"Surround"}},
          {"index":2,"codec_name":"subrip","codec_type":"subtitle","tags":{"language":"fre"},"disposition":{"forced":1}},
          {"index":3,"codec_name":"ttf","codec_type":"attachment"}],
         "chapters":[{"id":0},{"id":1}],
         "format":{"duration":"5400.123","size":"1000","format_name":"matroska,webm","tags":{"title":"Movie"}}}
        """;
        var p = FfprobeService.Parse(json);
        Assert.Equal(5400.123, p.DurationSeconds);
        Assert.Equal(1080, p.MainVideo!.Height);
        Assert.Equal(23.976, p.MainVideo.FrameRate!.Value, 3);
        Assert.Equal("eng", p.Audio.Single().Language);
        Assert.True(p.Subtitles.Single().IsForced);
        Assert.Single(p.Attachments);
        Assert.Equal(2, p.ChapterCount);
        Assert.Equal("Movie", p.Title);
    }

    [Fact]
    public void Display_quoting_follows_windows_rules()
    {
        Assert.Equal("simple", CommandLine.Quote("simple"));
        Assert.Equal("\"a b\"", CommandLine.Quote("a b"));
        Assert.Equal("\"C:\\dir with space\\\\\"", CommandLine.Quote("C:\\dir with space\\"));
        Assert.Equal("\"say \\\"hi\\\"\"", CommandLine.Quote("say \"hi\""));
        Assert.Equal("\"L'été (1)\"", CommandLine.Quote("L'été (1)"));
        Assert.Equal("\"\"", CommandLine.Quote(""));
    }
}
