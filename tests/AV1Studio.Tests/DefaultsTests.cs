using System.Diagnostics;
using System.Text.Json;
using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Tests;

public class DefaultsTests
{
    [Fact]
    public void New_installations_use_auto_cpu_usage_at_high_priority()
    {
        var s = new AppSettings();
        s.Upgrade();
        foreach (var p in new[] { s.SearchCpu, s.EncodeCpu })
        {
            Assert.Equal(CpuUsageMode.Auto, p.Mode);
            Assert.Equal(ProcessPriority.High, p.Priority);
            var plan = ResourcePlanner.Plan(p, logicalOverride: 12);
            Assert.Equal(12 - ResourcePlanner.AutoReserve(12), plan.Threads); // a few threads kept for Windows
            Assert.Equal(ProcessPriorityClass.High, plan.Priority);
        }
        // Maximum still uses every thread
        Assert.Null(ResourcePlanner.Plan(new CpuProfile { Mode = CpuUsageMode.Maximum }, 12).AffinityMask);
    }

    [Fact]
    public void Earlier_maximum_default_becomes_auto_but_other_choices_are_kept()
    {
        const string v3 = """{"SettingsVersion":3,"SearchCpu":{"Mode":"Maximum","Priority":"High"},"EncodeCpu":{"Mode":"Low","Priority":"Idle"}}""";
        var s = JsonSerializer.Deserialize<AppSettings>(v3, JsonFile.Options)!;
        s.Upgrade();
        Assert.Equal(CpuUsageMode.Auto, s.SearchCpu.Mode);
        Assert.Equal(CpuUsageMode.Low, s.EncodeCpu.Mode);
        Assert.Equal(ProcessPriority.Idle, s.EncodeCpu.Priority);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(32)]
    public void Thread_count_follows_the_detected_cpu(int logical)
    {
        Assert.Equal(logical, ResourcePlanner.Plan(new CpuProfile { Mode = CpuUsageMode.Maximum }, logical).Threads);
        Assert.True(ResourcePlanner.Plan(new CpuProfile { Mode = CpuUsageMode.Balanced }, logical).Threads < logical);
        Assert.Equal(logical / 2, ResourcePlanner.Plan(new CpuProfile { Mode = CpuUsageMode.Low }, logical).Threads);
    }

    [Fact]
    public void Realtime_priority_is_never_used()
    {
        foreach (var prio in Enum.GetValues<ProcessPriority>())
            Assert.NotEqual(ProcessPriorityClass.RealTime, ResourcePlanner.ToClass(prio));
        foreach (var mode in Enum.GetValues<CpuUsageMode>())
            Assert.NotEqual(ProcessPriorityClass.RealTime, ResourcePlanner.ToClass(ResourcePlanner.SuggestedPriority(mode)));
    }

    [Fact]
    public void Settings_from_an_older_version_get_the_new_cpu_defaults_once()
    {
        const string old = """{"SearchCpu":{"Mode":"Auto","Priority":"BelowNormal"},"EncodeCpu":{"Mode":"Low","Priority":"Idle"},"Extensions":"mkv,avi"}""";
        var s = JsonSerializer.Deserialize<AppSettings>(old, JsonFile.Options)!;
        s.Upgrade();
        Assert.Equal(CpuUsageMode.Auto, s.EncodeCpu.Mode);
        Assert.Equal(ProcessPriority.High, s.SearchCpu.Priority);
        Assert.Contains(".mp4", MediaTypes.Parse(s.Extensions));
        Assert.Contains(".avi", MediaTypes.Parse(s.Extensions));

        // later choices of the user are kept
        s.EncodeCpu.Mode = CpuUsageMode.Low;
        var again = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonFile.Options), JsonFile.Options)!;
        again.Upgrade();
        Assert.Equal(CpuUsageMode.Low, again.EncodeCpu.Mode);
    }

    [Fact]
    public void Mp4_and_other_common_containers_are_video_by_default()
    {
        var exts = MediaTypes.Parse(new AppSettings().Extensions);
        foreach (var e in new[] { ".mp4", ".MP4", ".m4v", ".mkv", ".mov", ".webm", ".avi", ".ts", ".m2ts" })
            Assert.True(MediaTypes.IsVideoExtension("movie" + e, exts), e);
        Assert.False(MediaTypes.IsVideoExtension("poster.jpg", exts));
        Assert.Equal("mp4", OutputPlanner.ContainerExtension(ContainerFormat.SameAsSource, @"x\movie.MP4"));
    }

    [Fact]
    public void Known_harmless_mp4_messages_do_not_fail_verification()
    {
        Assert.True(VerificationService.IsBenignDemuxerMessage("[mov,mp4,m4a,3gp,3g2,mj2 @ 000001] Referenced QT chapter track not found"));
        Assert.False(VerificationService.IsBenignDemuxerMessage("[av1 @ 000001] Corrupt frame detected"));
    }
}
