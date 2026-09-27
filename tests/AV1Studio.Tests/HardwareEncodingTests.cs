using AV1Studio.Models;
using AV1Studio.Services;
using static AV1Studio.Tests.CommandBuilderTests;

namespace AV1Studio.Tests;

public class HardwareEncodingTests
{
    private static readonly ToolStatus Tools = new()
    {
        AbAv1Path = "ab-av1.exe", CrfSearchJson = true, EncodeVerify = true, EncodeFailFast = true, HasTempDirArg = true,
        AbAv1SemVer = new Version(0, 11, 7), SvtAv1SemVer = new Version(3, 0, 0), // old SVT: integer-step workaround must not leak into GPU runs
    };

    [Fact]
    public void Off_by_default_uses_svt_av1_without_encoder_flag()
    {
        var s = new AppSettings();
        Assert.False(s.HardwareEncoding);
        var a = AbAv1Commands.CrfSearch(s, Tools, Probe(), "in.mkv", "t");
        Assert.DoesNotContain("-e", a);
        Assert.Equal("SVT-AV1 (CPU)", AbAv1Commands.EncoderDescription(s));
    }

    [Fact]
    public void On_uses_gpu_encoder_and_drops_svt_only_options()
    {
        var s = new AppSettings
        {
            HardwareEncoding = true, HardwareEncoder = "av1_nvenc", HardwarePreset = "p5",
            Preset = 4, SvtParams = "film-grain=8", EncoderThreads = 8, Scd = ScdMode.On, Keyint = "10s",
        };
        var a = string.Join(" ", AbAv1Commands.CrfSearch(s, Tools, Probe(), "in.mkv", "t"));
        Assert.Contains("-e av1_nvenc --preset p5 --pix-format yuv420p10le", a);
        Assert.Contains("--keyint 10s", a);
        Assert.DoesNotContain("--svt", a);
        Assert.DoesNotContain("--scd", a);
        Assert.DoesNotContain("--preset 4", a);
        Assert.DoesNotContain("--crf-increment", a);
    }

    [Fact]
    public void Empty_gpu_preset_lets_the_encoder_decide_and_explicit_pixfmt_is_kept()
    {
        var s = new AppSettings { HardwareEncoding = true, HardwareEncoder = "av1_qsv", PixelFormat = "yuv420p" };
        var a = AbAv1Commands.SharedEncodeArgs(s, Probe());
        Assert.Equal(["-e", "av1_qsv", "--pix-format", "yuv420p"], a);
    }

    [Fact]
    public void Switching_encoder_invalidates_cached_crf()
    {
        var cpu = new AppSettings();
        var gpu = new AppSettings { HardwareEncoding = true };
        Assert.NotEqual(AbAv1Commands.SearchFingerprint(cpu, Tools, Probe()), AbAv1Commands.SearchFingerprint(gpu, Tools, Probe()));
    }

    [Theory]
    [InlineData("av1_nvenc", "p7", true)]
    [InlineData("av1_nvenc", "", true)]
    [InlineData("av1_nvenc", "p9", false)]
    [InlineData("av1_nvenc", "slow", false)]
    [InlineData("av1_qsv", "veryslow", true)]
    [InlineData("av1_qsv", "p5", false)]
    [InlineData("av1_amf", "", false)]
    public void Validation(string encoder, string preset, bool valid)
    {
        var s = new AppSettings { HardwareEncoding = true, HardwareEncoder = encoder, HardwarePreset = preset };
        Assert.Equal(valid, AbAv1Commands.Validate(s).Count == 0);
    }
}
