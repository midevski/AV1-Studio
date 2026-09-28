using System.Windows.Threading;
using AV1Studio.Models;
using AV1Studio.Services;
using AV1Studio.ViewModels;

namespace AV1Studio.Tests;

/// <summary>
/// The CRF search must run with exactly the settings shown on screen, and a CRF found with other settings must
/// never be reused. The end-to-end part needs AV1STUDIO_TEST_TOOLS; the rest runs everywhere.
/// </summary>
[Collection("e2e")] // shares AppPaths.Root
public class CrfConsistencyTests : IDisposable
{
    private static readonly string? ToolsDir = Environment.GetEnvironmentVariable("AV1STUDIO_TEST_TOOLS");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av1studio-crf-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _previousRoot = AppPaths.Root;

    private static readonly ToolStatus Tools = new()
    {
        AbAv1Path = "ab-av1.exe", CrfSearchJson = true, EncodeVerify = true, EncodeFailFast = true, HasTempDirArg = true,
        AbAv1SemVer = new Version(0, 11, 7), SvtAv1SemVer = new Version(4, 2, 0),
    };

    public CrfConsistencyTests()
    {
        AppPaths.Root = Path.Combine(_root, "appdata");
        AppPaths.EnsureCreated();
    }

    public void Dispose()
    {
        AppPaths.Root = _previousRoot;
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void OnUiThread(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var task = body();
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            if (task.IsFaulted) error = task.Exception!.GetBaseException();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new AggregateException(error);
    }

    /// <summary>Lets queued UI work (coalesced queue updates) run.</summary>
    private static async Task Settle() => await Dispatcher.Yield(DispatcherPriority.SystemIdle);

    [Fact]
    public void The_crf_search_command_is_the_terminal_command()
    {
        // VMAF 93, preset 8, samples Auto, fresh settings: only these options — nothing hidden.
        var s = new AppSettings { TargetVmaf = 93, Preset = 8 };
        var a = AbAv1Commands.CrfSearch(s, Tools, null, "video.mov", "tmp");
        Assert.Equal(["crf-search", "-i", "video.mov", "--preset", "8", "--min-vmaf", "93",
                      "--temp-dir", "tmp", "--stdout-format", "json"], a);
    }

    [Fact]
    public void Queued_files_follow_the_screen_and_a_stale_crf_is_discarded()
    {
        OnUiThread(async () =>
        {
            var vm = new MainViewModel { ConfirmDialog = (_, _) => true };
            vm.Settings.DestinationFolder = @"C:\Out A";
            vm.TargetVmaf = 90;
            var probe = new ProbeInfo { DurationSeconds = 60 };
            probe.Streams.Add(new StreamInfo { Type = "video", Codec = "h264", Width = 1920, Height = 1080, FrameRate = 60 });

            QueueItem Item(string name, ItemStatus st) => new()
            {
                SourcePath = @"C:\Videos\" + name, Mode = EncodeMode.AbAv1, Status = st, Probe = probe, ProfileId = vm.CaptureProfile(),
            };
            var analysed = Item("analysed.mov", ItemStatus.CrfFound);
            var s90 = vm.JobSettings(analysed);
            analysed.Search = new CrfSearchResult { Crf = 37.75, Vmaf = 90.1 };
            analysed.SearchFingerprint = AbAv1Commands.SearchFingerprint(s90, vm.Tools, probe);
            var waiting = Item("waiting.mov", ItemStatus.Waiting);
            var encoding = Item("encoding.mov", ItemStatus.Encoding);
            var done = Item("done.mov", ItemStatus.Completed);
            foreach (var i in new[] { analysed, waiting, encoding, done }) vm.Items.Add(i);

            // the user changes the screen: VMAF 93, preset 8, another destination
            vm.TargetVmaf = 93;
            vm.SelectedPreset = vm.PresetOptions.First(p => p.Value == 8);
            vm.DestinationFolder = @"C:\Out B";
            await Settle();

            Assert.Equal(93, vm.JobSettings(waiting).TargetVmaf);          // what is shown is what runs
            Assert.Equal(8, vm.JobSettings(waiting).Preset);
            Assert.Equal("VMAF 93", waiting.TargetText);
            Assert.Null(analysed.Search);                                    // CRF 37.75 (VMAF 90) is not reused
            Assert.Equal(ItemStatus.Waiting, analysed.Status);
            Assert.Equal(93, vm.JobSettings(analysed).TargetVmaf);
            Assert.Equal(@"C:\Out A", vm.JobSettings(waiting).DestinationFolder); // output location stays as queued
            Assert.Equal(90, vm.JobSettings(encoding).TargetVmaf);          // running and finished work keeps its settings
            Assert.Equal(90, vm.JobSettings(done).TargetVmaf);

            // the new settings get a different search fingerprint: the app's own cache cannot mix them up
            Assert.NotEqual(AbAv1Commands.SearchFingerprint(s90, vm.Tools, probe),
                            AbAv1Commands.SearchFingerprint(vm.JobSettings(analysed), vm.Tools, probe));
            vm.Shutdown();
        });
    }

    [Fact]
    public void Changing_a_value_by_hand_turns_the_profile_into_custom()
    {
        OnUiThread(async () =>
        {
            var vm = new MainViewModel();
            vm.SelectedProfile = "Balanced";
            Assert.Equal("Balanced", vm.SelectedProfile);
            Assert.Equal(93, vm.TargetVmaf);

            vm.TargetVmaf = 94;
            Assert.Equal("Custom", vm.SelectedProfile);

            vm.SelectedProfile = "High Quality";
            Assert.Equal(95, vm.TargetVmaf);
            vm.SamplesChoice = "3";
            Assert.Equal("Custom", vm.SelectedProfile);

            vm.SelectedProfile = "Archive";
            vm.SelectedPreset = vm.PresetOptions.First(p => p.Value == 8);
            Assert.Equal("Custom", vm.SelectedProfile);

            // Manual AV1 has its own profile
            vm.SelectedMode = EncodeMode.Manual;
            vm.SelectedProfile = "Small File";
            Assert.Equal("Small File", vm.SelectedProfile);
            vm.Manual.Quality += 2;
            Assert.Equal("Custom", vm.SelectedProfile);
            await Settle();
            vm.Shutdown();
        });
    }

    [Fact]
    public async Task Changing_the_vmaf_target_runs_a_new_real_crf_search()
    {
        if (ToolsDir is null) return;
        var src = Path.Combine(_root, "video.mkv");
        var (c, _, e) = await ChildProcess.RunCaptureAsync(Path.Combine(ToolsDir, "ffmpeg.exe"),
            ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=12", "-c:v", "libx264", "-crf", "8", "-preset", "ultrafast", src],
            timeout: TimeSpan.FromMinutes(2));
        Assert.True(c == 0, e);

        OnUiThread(async () =>
        {
            var vm = new MainViewModel { ConfirmDialog = (_, _) => true, InfoDialog = (t, m) => throw new Xunit.Sdk.XunitException($"{t}: {m}") };
            vm.Settings.AbAv1Path = Path.Combine(ToolsDir, "ab-av1.exe");
            vm.Settings.FfmpegPath = Path.Combine(ToolsDir, "ffmpeg.exe");
            vm.Settings.FfprobePath = Path.Combine(ToolsDir, "ffprobe.exe");
            vm.Settings.FirstRunDone = true;
            vm.Settings.MaxEncodedPercent = 99;
            await vm.InitializeAsync();
            vm.TargetVmaf = 88;
            vm.SelectedPreset = vm.PresetOptions.First(p => p.Value == 12);
            await vm.AddPathsAsync([src]);
            var item = vm.Items.Single();
            for (int k = 0; k < 600 && item.Probe is null; k++) await Task.Delay(50);

            async Task Analyse()
            {
                vm.SelectedItems = [item];
                vm.AnalyzeSelectedCommand.Execute(null);
                for (int k = 0; k < 100 && !vm.IsRunning; k++) await Task.Delay(50);
                for (int k = 0; k < 6000 && vm.IsRunning; k++) await Task.Delay(50);
            }

            await Analyse();
            Assert.Equal(ItemStatus.CrfFound, item.Status);
            double crfAt88 = item.Search!.Crf;
            Assert.Contains(item.LastCrfSearchCommand!.Split(' '), a => a == "88");

            vm.TargetVmaf = 96;
            await Settle();
            Assert.Null(item.Search);                                   // stale CRF gone
            await Analyse();
            Assert.Equal(ItemStatus.CrfFound, item.Status);
            Assert.Contains("--min-vmaf 96", item.LastCrfSearchCommand);
            Assert.True(item.Search!.Crf < crfAt88, $"higher quality target must give a lower CRF ({item.Search.Crf} vs {crfAt88})");
            vm.Shutdown();
        });
    }
}
