using AV1Studio.Models;
using AV1Studio.Services;
using AV1Studio.ViewModels;

namespace AV1Studio.Tests;

/// <summary>A queued job keeps the configuration it was added with; changing the UI afterwards must not alter it.</summary>
[Collection("e2e")] // shares the global AppPaths.Root with the end-to-end tests
public class QueueIntegrityTests
{
    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new AggregateException(error);
    }

    [Fact]
    public void Changing_settings_after_queueing_does_not_modify_queued_jobs()
    {
        var home = Path.Combine(Path.GetTempPath(), "av1studio-integrity-" + Guid.NewGuid().ToString("N"));
        var previous = AppPaths.Root;
        AppPaths.Root = home;
        try
        {
            OnSta(() =>
            {
                var vm = new MainViewModel();
                vm.Settings.TargetVmaf = 95;
                vm.Settings.Manual.Encoder = "libsvtav1";
                vm.Settings.Manual.Quality = 30;
                vm.Settings.Manual.Preset = "6";
                var item = new QueueItem { SourcePath = @"D:\Movies\A.mkv", ProfileId = vm.CaptureProfile() };

                // the user keeps working in the UI
                vm.Settings.TargetVmaf = 90;
                vm.Settings.Manual.Quality = 40;
                vm.Settings.Manual.Preset = "10";
                vm.Settings.Manual.Encoder = "av1_nvenc";

                var job = vm.JobSettings(item);
                Assert.Equal(95, job.TargetVmaf);
                Assert.Equal(30, job.Manual.Quality);
                Assert.Equal("6", job.Manual.Preset);
                Assert.Equal("libsvtav1", job.Manual.Encoder);

                // identical settings share one snapshot, different settings get a new one
                var again = vm.CaptureProfile();
                Assert.NotEqual(item.ProfileId, again);
                Assert.Equal(again, vm.CaptureProfile());
                vm.Shutdown();
            });
        }
        finally
        {
            AppPaths.Root = previous;
            try { Directory.Delete(home, true); } catch { }
        }
    }

    [Fact]
    public void Global_safety_and_cpu_settings_apply_to_queued_jobs_but_quality_does_not_change()
    {
        var job = new AppSettings { TargetVmaf = 95, DeleteSourceAfterSuccess = false };
        job.EncodeCpu.Mode = CpuUsageMode.Maximum;
        var global = new AppSettings { TargetVmaf = 88, DeleteSourceAfterSuccess = true };
        global.EncodeCpu.Mode = CpuUsageMode.Low;

        var item = new QueueItem { SourcePath = @"D:\a.mkv", ProfileId = "p1" };
        var qp = new QueueProcessor(global, new ToolStatus(), new AnalysisCache()) { ProfileFor = _ => job };
        var e = qp.Effective(item);

        Assert.Equal(95, e.TargetVmaf);                   // job configuration kept
        Assert.True(e.DeleteSourceAfterSuccess);          // run-level safety choice of this run
        Assert.Equal(CpuUsageMode.Low, e.EncodeCpu.Mode); // current CPU limits
        Assert.NotSame(job, e);                           // the stored snapshot itself is never modified
        Assert.False(job.DeleteSourceAfterSuccess);
    }
}
