using AV1Studio.Services;

namespace AV1Studio.Tests;

public class HardwareCapabilitiesTests
{
    private static SystemInfo Sys(params string[] gpus) => new()
    {
        Cpu = "Example 8-Core Processor", LogicalCores = 16, RamBytes = 32UL << 30,
        Gpus = gpus.Select(SystemInfo.Classify).ToList(),
    };

    [Fact]
    public void Gpu_that_decodes_av1_but_cannot_encode_it_is_reported_separately()
    {
        var t = new ToolStatus();
        t.ManualEncoders.Add("libsvtav1");                                   // av1_nvenc test encode failed
        t.Av1Decoders.Add(("libdav1d", "dav1d (CPU software decoder)", false));
        t.Av1Decoders.Add(("av1_cuvid", "NVIDIA NVDEC", true));             // hardware decode test passed

        var caps = HardwareCapabilities.Describe(Sys("NVIDIA GeForce RTX 3000-series GPU"), t);
        var gpu = caps.Single(c => c.Kind == "NVIDIA GPU");
        Assert.True(gpu.DecodeOk);
        Assert.StartsWith("✓", gpu.DecodeText);
        Assert.False(gpu.EncodeOk);
        Assert.StartsWith("✕", gpu.EncodeText);

        var cpu = caps.Single(c => c.Kind == "CPU");
        Assert.True(cpu.EncodeOk);
        Assert.Contains("SVT-AV1", HardwareCapabilities.Recommendation(t));
        Assert.Contains("No hardware AV1 encoder", HardwareCapabilities.Recommendation(t));
    }

    [Fact]
    public void Capability_comes_from_tests_not_model_names()
    {
        // A GPU whose NVENC test FAILED (e.g. old driver) must not be reported as able to encode.
        var t = new ToolStatus();
        t.ManualEncoders.Add("libsvtav1");
        var gpu = HardwareCapabilities.Describe(Sys("NVIDIA GeForce RTX 4000-series GPU"), t).Single(c => c.Kind == "NVIDIA GPU");
        Assert.False(gpu.EncodeOk);

        t.ManualEncoders.Add("av1_nvenc");
        t.EncoderAvailability.Add(new EncoderAvailability("av1_nvenc", "NVIDIA NVENC AV1", true, true, "Available"));
        gpu = HardwareCapabilities.Describe(Sys("NVIDIA GeForce RTX 4000-series GPU"), t).Single(c => c.Kind == "NVIDIA GPU");
        Assert.True(gpu.EncodeOk);
        Assert.Contains("NVIDIA NVENC AV1", HardwareCapabilities.Recommendation(t));
    }
}
