using System.Windows.Threading;
using AV1Studio.Models;
using AV1Studio.Services;
using AV1Studio.ViewModels;
using AV1Studio.Views;

namespace AV1Studio.Tests;

[Collection("e2e")] // shares AppPaths.Root
public class SamplesAndCacheTests : IDisposable
{
    private static readonly ToolStatus Tools = new()
    {
        AbAv1Path = "ab-av1.exe", CrfSearchJson = true, EncodeVerify = true, EncodeFailFast = true, HasTempDirArg = true,
        AbAv1SemVer = new Version(0, 11, 7), SvtAv1SemVer = new Version(4, 2, 0),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "av1studio-cache-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _previousRoot = AppPaths.Root;

    public SamplesAndCacheTests()
    {
        AppPaths.Root = Path.Combine(_root, "appdata");
        AppPaths.EnsureCreated();
        AbAv1Cache.FolderOverride = Path.Combine(_root, "ab-av1");
    }

    public void Dispose()
    {
        AbAv1Cache.FolderOverride = null;
        AppPaths.Root = _previousRoot;
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Auto_samples_pass_no_samples_flag()
    {
        var s = new AppSettings { SampleEvery = "12m", MinSamples = 2 };
        var a = string.Join(" ", AbAv1Commands.CrfSearch(s, Tools, null, "in.mkv", "t"));
        Assert.Null(new AppSettings().Samples); // Auto is the default
        Assert.DoesNotContain("--samples ", a);
        Assert.Contains("--sample-every 12m --min-samples 2", a);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void A_fixed_sample_count_is_passed_alone(int n)
    {
        var s = new AppSettings { Samples = n, SampleEvery = "12m", MinSamples = 2 };
        var a = AbAv1Commands.CrfSearch(s, Tools, null, "in.mkv", "t");
        int i = a.IndexOf("--samples");
        Assert.Equal(n.ToString(), a[i + 1]);
        Assert.DoesNotContain("--sample-every", a); // overridden by --samples: not passed
        Assert.DoesNotContain("--min-samples", a);
        Assert.Single(a, x => x == "--samples");
        // a different sample count is a different search (no stale cached result is reused)
        Assert.NotEqual(AbAv1Commands.SearchFingerprint(s, Tools, null), AbAv1Commands.SearchFingerprint(new AppSettings(), Tools, null));
    }

    [Fact]
    public void Sample_choices_map_to_the_setting()
    {
        Assert.Equal("Auto", SampleOptions.Choices[0]);
        Assert.Contains("4", SampleOptions.Choices);
        Assert.Null(SampleOptions.FromText("Auto"));
        Assert.Equal(4, SampleOptions.FromText("4"));
        Assert.Equal("Auto", SampleOptions.ToText(null));
        Assert.Equal("7", SampleOptions.ToText(7));
    }

    [Fact]
    public void Clear_cache_deletes_ab_av1_cache_and_reanalyses_queued_files()
    {
        var cacheDb = Path.Combine(AbAv1Cache.Folder, "sample-encode-cache", "db");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheDb)!);
        File.WriteAllBytes(cacheDb, new byte[4096]);
        var video = Path.Combine(_root, "Movie.mkv");
        File.WriteAllBytes(video, [1, 2, 3]);

        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var vm = new MainViewModel();
                var analysed = new QueueItem
                {
                    SourcePath = video, Mode = EncodeMode.AbAv1, Status = ItemStatus.Ready,
                    Search = new CrfSearchResult { Crf = 30, Vmaf = 95 },
                };
                var done = new QueueItem { SourcePath = video + "2", Mode = EncodeMode.AbAv1, Status = ItemStatus.Completed };
                vm.Items.Add(analysed);
                vm.Items.Add(done);

                var summary = vm.ClearAnalysisCache();

                Assert.False(Directory.Exists(AbAv1Cache.Folder));   // ab-av1's sample cache is gone
                Assert.Contains("sample cache", summary);
                Assert.Null(analysed.Search);                         // the queued file searches again
                Assert.Equal(ItemStatus.Waiting, analysed.Status);
                Assert.Equal(ItemStatus.Completed, done.Status);      // finished work is not touched
                vm.Shutdown();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new AggregateException(error);
    }
}
