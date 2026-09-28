using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Tests;

public class CommandBuilderTests
{
    private static readonly ToolStatus Tools = new()
    {
        AbAv1Path = "ab-av1.exe", CrfSearchJson = true, EncodeVerify = true, EncodeFailFast = true, HasTempDirArg = true,
        AbAv1SemVer = new Version(0, 11, 7), SvtAv1SemVer = new Version(4, 2, 0),
    };

    internal static ProbeInfo Probe(params StreamInfo[] extra)
    {
        var p = new ProbeInfo { DurationSeconds = 5400 };
        p.Streams.Add(new StreamInfo { Index = 0, TypeIndex = 0, Type = "video", Codec = "h264", Width = 1920, Height = 1080 });
        p.Streams.AddRange(extra);
        return p;
    }

    internal static StreamInfo Audio(int idx, int ti, string codec, string? lang = null, int ch = 2) =>
        new() { Index = idx, TypeIndex = ti, Type = "audio", Codec = codec, Language = lang, Channels = ch };

    internal static StreamInfo Sub(int idx, int ti, string codec, string? lang = null) =>
        new() { Index = idx, TypeIndex = ti, Type = "subtitle", Codec = codec, Language = lang };

    [Fact]
    public void Defaults_pass_only_what_is_needed_and_let_abav1_decide_the_rest()
    {
        var s = new AppSettings();
        var args = AbAv1Commands.CrfSearch(s, Tools, Probe(), @"D:\Movies\A (1).mkv", @"C:\t");
        Assert.Equal(["crf-search", "-i", @"D:\Movies\A (1).mkv", "--min-vmaf", "95", "--temp-dir", @"C:\t", "--stdout-format", "json"], args);
    }

    [Fact]
    public void Preset_filters_and_search_options_are_mapped_to_documented_flags()
    {
        var s = new AppSettings
        {
            Preset = 5, PixelFormat = "yuv420p10le", MaxHeight = 720, Crop = "1920:800:0:140", Fps = "24",
            Keyint = "10s", Scd = ScdMode.On, SvtParams = "film-grain=8:tune=0\nenable-overlays=1", EncoderThreads = 8,
            TargetVmaf = 93.5, MaxEncodedPercent = 70, MinCrf = 10, MaxCrf = 50, Samples = 4, SampleDuration = "10s",
            AbAv1SampleCache = false, Thorough = true,
        };
        var a = AbAv1Commands.CrfSearch(s, Tools, Probe(), "in.mkv", "t");
        string J = string.Join(" ", a);
        Assert.Contains("--preset 5", J);
        Assert.Contains("--pix-format yuv420p10le", J);
        Assert.Contains("--vfilter crop=1920:800:0:140,scale=-2:720:flags=lanczos,fps=24", J);
        Assert.Contains("--keyint 10s --scd true", J);
        Assert.Contains("--svt film-grain=8 --svt tune=0 --svt enable-overlays=1 --svt lp=8", J);
        Assert.Contains("--min-vmaf 93.5 --max-encoded-percent 70 --min-crf 10 --max-crf 50", J);
        Assert.Contains("--thorough --samples 4 --sample-duration 10s --cache false", J); // no --sample-every with a fixed count
    }

    [Fact]
    public void Never_upscales()
    {
        var s = new AppSettings { MaxHeight = 2160 };
        Assert.Null(AbAv1Commands.BuildVideoFilter(s, Probe()));
    }

    [Fact]
    public void Svt_params_reject_values_abav1_controls_and_garbage()
    {
        var errors = new List<string>();
        var ok = AbAv1Commands.ParseSvtParams("crf=20 preset=4 keyint=10 scd=1 film-grain=8 ; rm -rf", errors);
        Assert.Equal(["film-grain=8"], ok);
        Assert.Equal(7, errors.Count); // 4 reserved keys + ";", "rm", "-rf"
    }

    [Fact]
    public void Enc_args_cannot_override_codecs_or_progress()
    {
        var errors = new List<string>();
        var ok = AbAv1Commands.ParseEncArgs("c:v=libx264\n-c:a=aac\nprogress=x\nthreads=8\n# comment", errors);
        Assert.Equal(["threads=8"], ok);
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void Encode_command_has_verify_failfast_progress_and_output()
    {
        var s = new AppSettings();
        var streams = AbAv1Commands.PlanStreams(s, Probe(Audio(1, 0, "ac3")), "mkv", null, null);
        var a = AbAv1Commands.Encode(s, Tools, Probe(), @"D:\M\a.mkv", 31.25, @"E:\o\a_AV1.partial.mkv", streams, @"C:\p.txt");
        Assert.Equal(["encode", "-i", @"D:\M\a.mkv", "--crf", "31.25", "-o", @"E:\o\a_AV1.partial.mkv",
                      "--enc", "f=matroska", "--verify", "--fail-fast", "--enc", @"progress=C:\p.txt"], a);
    }

    [Fact]
    public void Old_abav1_without_verify_or_json_gets_no_unknown_flags()
    {
        var old = new ToolStatus { AbAv1Path = "x", AbAv1SemVer = new Version(0, 10, 0) };
        var s = new AppSettings();
        var a = AbAv1Commands.Encode(s, old, Probe(), "i.mkv", 30, "o.mkv", new StreamPlan(), null);
        Assert.DoesNotContain("--verify", a);
        Assert.DoesNotContain("--fail-fast", a);
        Assert.DoesNotContain("--stdout-format", AbAv1Commands.CrfSearch(s, old, Probe(), "i.mkv", "t"));
    }

    [Fact]
    public void Old_svt_uses_integer_crf_increment()
    {
        var t = new ToolStatus { AbAv1SemVer = new Version(0, 11, 7), SvtAv1SemVer = new Version(3, 0, 0), HasTempDirArg = true };
        Assert.Contains("--crf-increment", AbAv1Commands.CrfSearch(new AppSettings(), t, Probe(), "i", "t"));
    }

    [Fact]
    public void Fingerprint_changes_with_quality_settings_but_not_with_output_settings()
    {
        var a = new AppSettings();
        var fp = AbAv1Commands.SearchFingerprint(a, Tools, Probe());
        var b = a.Clone(); b.DeleteSourceAfterSuccess = true; b.Suffix = "_x"; b.AudioMode = AudioMode.Remove;
        Assert.Equal(fp, AbAv1Commands.SearchFingerprint(b, Tools, Probe()));
        var c = a.Clone(); c.TargetVmaf = 93;
        Assert.NotEqual(fp, AbAv1Commands.SearchFingerprint(c, Tools, Probe()));
        var d = a.Clone(); d.Preset = 4;
        Assert.NotEqual(fp, AbAv1Commands.SearchFingerprint(d, Tools, Probe()));
    }
}
