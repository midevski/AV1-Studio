using System.Diagnostics;
using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Tests;

/// <summary>The priority chosen in Settings › Performance must reach every process of the tree (ab-av1 and the
/// FFmpeg processes it starts), from the first instruction on. Needs AV1STUDIO_TEST_TOOLS; otherwise a no-op.</summary>
[Collection("e2e")]
public class ProcessPriorityTests : IDisposable
{
    private static readonly string? ToolsDir = Environment.GetEnvironmentVariable("AV1STUDIO_TEST_TOOLS");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av1studio-prio-" + Guid.NewGuid().ToString("N")[..8]);

    public ProcessPriorityTests()
    {
        AppPaths.Root = Path.Combine(_root, "appdata");
        AppPaths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData(ProcessPriority.High, ProcessPriorityClass.High)]
    [InlineData(ProcessPriority.BelowNormal, ProcessPriorityClass.BelowNormal)]
    public async Task Whole_process_tree_runs_at_the_chosen_priority_and_is_killed_on_stop(ProcessPriority chosen, ProcessPriorityClass expected)
    {
        if (ToolsDir is null) return;
        var ffmpeg = Path.Combine(ToolsDir, "ffmpeg.exe");
        var src = Path.Combine(_root, "in.mkv");
        var (c, _, e) = await ChildProcess.RunCaptureAsync(ffmpeg,
            ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=1280x720:r=24:d=60", "-c:v", "libx264", "-preset", "ultrafast", src],
            timeout: TimeSpan.FromMinutes(2));
        Assert.True(c == 0, e);

        var tools = new ToolStatus { FfmpegPath = ffmpeg, FfprobePath = Path.Combine(ToolsDir, "ffprobe.exe") };
        var plan = ResourcePlanner.Plan(new CpuProfile { Mode = CpuUsageMode.Maximum, Priority = chosen });
        using var child = ChildProcess.Start(Path.Combine(ToolsDir, "ab-av1.exe"),
            ["encode", "-i", src, "--crf", "30", "--preset", "8", "-o", Path.Combine(_root, "out.mkv")],
            null, null, _root, ToolLocator.ChildEnvironment(tools), plan);

        // wait until ab-av1 has started its FFmpeg encoder
        var sw = Stopwatch.StartNew();
        List<Process> tree = [];
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            tree = child.Job!.ProcessIds().Select(id => { try { return Process.GetProcessById(id); } catch { return null; } })
                .OfType<Process>().ToList();
            if (tree.Any(p => SafeName(p) == "ffmpeg")) break;
            await Task.Delay(100);
        }
        Assert.Contains(tree, p => SafeName(p) == "ffmpeg");
        foreach (var p in tree)
        {
            try { Assert.Equal(expected, p.PriorityClass); }
            catch (InvalidOperationException) { /* exited meanwhile */ }
        }

        if (expected == ProcessPriorityClass.High)
        {
            // something lowers the encoder's priority mid-encode (Windows efficiency mode, another program…):
            // it must be restored within about a second, for as long as the encode runs
            var ffmpegProcess = tree.First(p => SafeName(p) == "ffmpeg");
            ffmpegProcess.PriorityClass = ProcessPriorityClass.Normal;
            await Task.Delay(2500);
            ffmpegProcess.Refresh();
            if (!ffmpegProcess.HasExited) Assert.Equal(ProcessPriorityClass.High, ffmpegProcess.PriorityClass);
            // AV1 Studio itself keeps up with High-priority encoders, so the window stays responsive
            Assert.Equal(ProcessPriorityClass.High, Process.GetCurrentProcess().PriorityClass);
        }

        child.Kill();
        await child.WaitAsync();
        await Task.Delay(500);
        foreach (var p in tree) { p.Refresh(); Assert.True(p.HasExited, $"{SafeName(p)} survived Stop"); }
        child.Dispose();
        Assert.NotEqual(ProcessPriorityClass.High, Process.GetCurrentProcess().PriorityClass); // back to normal afterwards
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName.ToLowerInvariant(); } catch { return ""; }
    }
}
