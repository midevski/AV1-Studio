using System.Diagnostics;
using System.IO;
using System.Text;
using AV1Studio.Models;
using AV1Studio.Native;
using AV1Studio.Util;

namespace AV1Studio.Services;

public sealed class CrfSearchOutcome
{
    public CrfSearchResult? Result { get; set; }
    /// <summary>Every tested CRF, in order. Filled on the tool-output thread; the on-screen list
    /// (QueueItem.Attempts) is updated separately on the UI thread and must not be read from here.</summary>
    public List<CrfAttempt> Attempts { get; } = new();
    /// <summary>ab-av1 reported no CRF satisfies min VMAF + max encoded percent.</summary>
    public bool NoSuitableCrf { get; set; }
    public string? Error { get; set; }
    public bool Cancelled { get; set; }
    public int ExitCode { get; set; }
}

public sealed class EncodeOutcome
{
    public int ExitCode { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public string? CommandLine { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>Bounded ring of recent stderr lines for error reporting.</summary>
internal sealed class Tail(int capacity)
{
    private readonly Queue<string> _q = new();
    public void Add(string s) { lock (_q) { _q.Enqueue(s); while (_q.Count > capacity) _q.Dequeue(); } }
    public string Text { get { lock (_q) return string.Join(Environment.NewLine, _q); } }
    public string? LastMatching(Func<string, bool> pred) { lock (_q) return _q.LastOrDefault(pred); }
}

public static class ItemLogExtensions
{
    public static void AddLog(this QueueItem item, string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss} {line}";
        Ui.Post(() =>
        {
            item.ItemLog.Add(text);
            while (item.ItemLog.Count > 400) item.ItemLog.RemoveAt(0);
        });
    }
}

public static class CrfSearchRunner
{
    public static async Task<CrfSearchOutcome> RunAsync(QueueItem item, AppSettings s, ToolStatus tools, CancellationToken ct)
    {
        var baseTemp = string.IsNullOrWhiteSpace(s.TempFolder) ? AppPaths.Temp : s.TempFolder.Trim();
        var tempDir = Path.Combine(baseTemp, "search-" + item.Id.ToString("N")[..12]);
        Directory.CreateDirectory(tempDir);

        var args = AbAv1Commands.CrfSearch(s, tools, item.Probe, item.SourcePath, tempDir);
        var outcome = new CrfSearchOutcome();
        var tail = new Tail(40);
        var sw = Stopwatch.StartNew();

        Ui.Post(() => item.Attempts.Clear());

        using var child = ChildProcess.Start(tools.AbAv1Path!, args,
            onStdout: line =>
            {
                Log.Tool($"[ab-av1 crf-search] {line}", item.FileName);
                switch (CrfSearchParser.ParseStdoutLine(line))
                {
                    case AttemptEvent a:
                        lock (outcome.Attempts) outcome.Attempts.Add(a.Attempt);
                        Ui.Post(() => item.Attempts.Add(a.Attempt));
                        var msg = $"Tested CRF {Fmt.Num(a.Attempt.Crf)} → VMAF {Fmt.Num(a.Attempt.Vmaf, "0.00")}, " +
                                  $"predicted {Fmt.Bytes(a.Attempt.PredictedSize)} ({Fmt.Percent(a.Attempt.PredictedPercent)})" +
                                  (a.Attempt.FromCache ? " [cached]" : "");
                        item.AddLog(msg);
                        Log.Info(msg, item.FileName);
                        item.Activity = $"Last: CRF {Fmt.Num(a.Attempt.Crf)} → VMAF {Fmt.Num(a.Attempt.Vmaf, "0.00")}";
                        break;
                    case DoneEvent d:
                        outcome.Result = d.Result;
                        break;
                    case SearchErrorEvent e:
                        outcome.NoSuitableCrf = true;
                        outcome.Error = e.Message;
                        break;
                }
            },
            onStderr: line =>
            {
                tail.Add(line);
                Log.Tool($"[ab-av1 crf-search] {line}", item.FileName);
                if (AbAv1StderrParser.Activity(line) is string act) item.Activity = act;
            },
            workingDirectory: tempDir,
            environment: ToolLocator.ChildEnvironment(tools),
            resources: ResourcePlanner.Plan(s.SearchCpu));

        item.LastCrfSearchCommand = child.CommandLineText;
        item.ActiveProcess = child;
        Log.Command(child.CommandLineText, item.FileName);

        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = MonitorAsync(child, item, sw, monitorCts.Token);
        try
        {
            outcome.ExitCode = await child.WaitAsync(ct);
        }
        finally
        {
            item.ActiveProcess = null;
            monitorCts.Cancel();
            try { await monitor; } catch { }
            TryDeleteDirectory(tempDir);
        }

        outcome.Cancelled = ct.IsCancellationRequested;
        if (outcome.Cancelled) return outcome;

        if (outcome.ExitCode == 0 && outcome.Result is null)
            outcome.Error = "ab-av1 exited successfully but reported no result.";
        else if (outcome.ExitCode != 0 && outcome.Result != null)
            outcome.Result = null; // never trust a result from a failed process

        if (outcome.ExitCode != 0 && outcome.Error is null)
        {
            var err = tail.LastMatching(l => l.TrimStart().StartsWith("Error:"));
            outcome.Error = err != null ? AbAv1StderrParser.ErrorMessage(err) : $"ab-av1 crf-search failed (exit {outcome.ExitCode})";
            if (err is null) Log.Error("ab-av1 output:\n" + tail.Text, item.FileName);
        }
        if (outcome.Error?.Contains("suitable crf", StringComparison.OrdinalIgnoreCase) == true)
            outcome.NoSuitableCrf = true;
        return outcome;
    }

    internal static async Task MonitorAsync(ChildProcess child, QueueItem item, Stopwatch sw, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                if (item.IsPaused) continue;
                item.Elapsed = sw.Elapsed;
                item.CpuPercent = child.Job?.SampleCpuPercent();
                item.RamBytes = child.Job?.SampleMemoryBytes();
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { Log.FileOnly($"Could not remove temp dir {dir}: {ex.Message}"); }
    }
}

public static class EncodeRunner
{
    public static async Task<EncodeOutcome> RunAsync(QueueItem item, AppSettings s, ToolStatus tools,
        OutputPlan output, StreamPlan streams, double crf, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output.PartialPath)!);
        var progressFile = Path.Combine(AppPaths.Progress, item.Id.ToString("N") + ".txt");
        TryDelete(progressFile);

        var args = AbAv1Commands.Encode(s, tools, item.Probe, item.SourcePath, crf, output.PartialPath, streams, progressFile);
        var tail = new Tail(60);
        var sw = Stopwatch.StartNew();
        var outcome = new EncodeOutcome();
        double duration = item.Probe?.DurationSeconds ?? 0;

        using var child = ChildProcess.Start(tools.AbAv1Path!, args,
            onStdout: l => Log.Tool($"[ab-av1 encode] {l}", item.FileName),
            onStderr: l => { tail.Add(l); Log.Tool($"[ab-av1 encode] {l}", item.FileName); },
            workingDirectory: AppPaths.Temp,
            environment: ToolLocator.ChildEnvironment(tools),
            resources: ResourcePlanner.Plan(s.EncodeCpu));

        item.LastEncodeCommand = child.CommandLineText;
        item.ActiveProcess = child;
        Log.Command(child.CommandLineText, item.FileName);

        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = Task.Run(() => MonitorAsync(child, item, progressFile, duration, sw, s.AbAv1Verify && tools.EncodeVerify, monitorCts.Token));
        try
        {
            outcome.ExitCode = await child.WaitAsync(ct);
        }
        finally
        {
            item.ActiveProcess = null;
            monitorCts.Cancel();
            try { await monitor; } catch { }
            TryDelete(progressFile);
        }

        outcome.Cancelled = ct.IsCancellationRequested;
        if (!outcome.Cancelled && outcome.ExitCode != 0)
        {
            var err = tail.LastMatching(l => l.TrimStart().StartsWith("Error:"));
            outcome.Error = err != null ? AbAv1StderrParser.ErrorMessage(err) : $"ab-av1 encode failed (exit {outcome.ExitCode})";
            Log.Error("ab-av1 encode output (last lines):\n" + tail.Text, item.FileName);
        }
        return outcome;
    }

    private static Task MonitorAsync(ChildProcess child, QueueItem item, string progressFile, double duration,
        Stopwatch sw, bool abAv1Verifies, CancellationToken ct) =>
        ProgressMonitor.RunAsync(child, item, progressFile, duration, sw, measureGpu: false, onEnd: () =>
        {
            if (!abAv1Verifies) return;
            item.Status = ItemStatus.Verifying;
            item.Activity = "ab-av1 verify: full decode + duration check…";
            item.AddLog("Encoding finished — ab-av1 is verifying the output (full decode)");
            Log.Info("Encoding finished, ab-av1 decode verification running", item.FileName);
        }, ct);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>Tails FFmpeg's -progress file once per second and updates the item's live statistics.
/// Shared by the AB-AV1 and Manual engines. Values are only shown when actually measured.</summary>
public static class ProgressMonitor
{
    public static async Task RunAsync(ChildProcess child, QueueItem item, string progressFile, double duration,
        Stopwatch sw, bool measureGpu, Action? onEnd, CancellationToken ct)
    {
        var parser = new FfmpegProgressParser();
        using var gpu = measureGpu ? GpuUsageSampler.TryCreate() : null;
        double? lastT = null; long? lastSize = null;
        long position = 0;
        var pending = new StringBuilder();
        int tick = 0;
        bool announcedVerify = false;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            tick++;
            if (item.IsPaused) continue; // suspended: keep last values
            item.Elapsed = sw.Elapsed;
            if (tick % 2 == 0)
            {
                item.CpuPercent = child.Job?.SampleCpuPercent();
                item.RamBytes = child.Job?.SampleMemoryBytes();
                if (gpu != null && child.Job != null) item.GpuPercent = gpu.Sample(child.Job.ProcessIds());
            }

            FfmpegProgress? latest = null;
            try
            {
                if (!File.Exists(progressFile)) continue;
                using var fs = new FileStream(progressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < position) position = 0;
                fs.Seek(position, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                pending.Append(await sr.ReadToEndAsync(ct));
                position = fs.Length;
            }
            catch (IOException) { continue; }
            catch (OperationCanceledException) { break; }

            var text = pending.ToString();
            int lastNl = text.LastIndexOf('\n');
            if (lastNl < 0) continue;
            foreach (var line in text[..lastNl].Split('\n'))
                if (parser.Feed(line.TrimEnd('\r')) is FfmpegProgress p) latest = p;
            pending.Clear().Append(text[(lastNl + 1)..]);

            if (latest is null) continue;
            item.CurrentFps = latest.Fps;
            item.Speed = latest.Speed;
            item.Bitrate = latest.Bitrate;
            item.CurrentOutputSize = latest.TotalSize;
            var elapsed = sw.Elapsed.TotalSeconds;
            if (latest.Frame is long f && elapsed > 0) item.AverageFps = f / elapsed;

            // Encoders buffer video at the start (lookahead) while copied audio already advances the output time, so
            // the first reports contain little more than the file header: bitrate is shown once real data is written.
            const long minMeaningfulBytes = 256 * 1024;
            if (latest.OutTimeSeconds is double ot && ot > 0 && latest.TotalSize is long ts && ts >= minMeaningfulBytes)
            {
                item.AverageBitrateKbps = ts * 8 / ot / 1000;
                if (lastT is double lt && lastSize is long ls && ot - lt > 0.5)
                    item.CurrentBitrateKbps = (ts - ls) * 8 / (ot - lt) / 1000;
                if (lastT is null || ot - lastT > 0.5) { lastT = ot; lastSize = ts; }
            }

            if (duration > 0 && latest.OutTimeSeconds is double t)
            {
                double frac = Math.Clamp(t / duration, 0, 1);
                item.Progress = frac * 100;
                if (frac > 0.01 && latest.TotalSize is long size) item.ProjectedSize = (long)(size / frac);
                double remaining = duration - t;
                if (latest.Speed is double sp && sp > 0) item.Eta = TimeSpan.FromSeconds(remaining / sp);
                else if (frac > 0.01) item.Eta = TimeSpan.FromSeconds(elapsed * (1 - frac) / frac);
                item.Activity = $"Encoding {frac * 100:0.0}%";
            }

            if (latest.End && !announcedVerify)
            {
                announcedVerify = true;
                item.Progress = 100;
                item.Eta = null;
                onEnd?.Invoke();
            }
        }
    }
}
