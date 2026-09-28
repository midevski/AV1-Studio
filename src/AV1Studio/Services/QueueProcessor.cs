using System.IO;
using System.Security.Cryptography;
using AV1Studio.Models;
using AV1Studio.Native;
using AV1Studio.Util;

namespace AV1Studio.Services;

public enum RunMode { AnalyzeOnly, EncodeAnalyzed, AnalyzeAndEncode }

/// <summary>
/// Orchestrates the per-file pipeline:
///   Video: probe → [AB-AV1: CRF search (or cached)] → disk-space check → encode to *.partial → verify
///          → rename into place → optional source deletion.
///   Copy (non-video file of a mirrored folder): copy to *.partial → verify size (optional hash) → rename.
///          The source is never deleted.
/// Files are processed one at a time by default so that, with source deletion enabled, free space grows
/// as the library is converted. Each job uses the settings snapshot it was queued with; safety,
/// disk-space and CPU settings are the current global ones.
/// </summary>
public sealed class QueueProcessor
{
    private readonly AppSettings _s;
    private readonly ToolStatus _tools;
    private readonly AnalysisCache _cache;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _itemCts = new();
    private readonly Dictionary<Guid, ItemStatus> _userStopped = new(); // Cancelled or Skipped by the user
    private readonly Dictionary<string, AppSettings> _effective = new();
    private readonly HashSet<string> _outputsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private long _reservedBytes;
    private volatile bool _pausedForSpace;

    public event Action<QueueItem>? ItemChanged;
    public event Action<string>? Warning;

    /// <summary>Live check of the user's current "delete source" choice. Unticking it during a run
    /// takes effect immediately (the safe direction); ticking it only applies to the next run.</summary>
    public Func<bool>? DeletionStillAllowed { get; set; }

    /// <summary>Settings snapshot a job was queued with (null = use the run settings).</summary>
    public Func<QueueItem, AppSettings?>? ProfileFor { get; set; }

    /// <summary>Folder jobs (for recreating directory trees).</summary>
    public Func<IReadOnlyList<FolderJob>>? Folders { get; set; }

    /// <summary>Decision for this run when the collision policy is "Ask".</summary>
    public CollisionPolicy? CollisionOverride { get; set; }

    /// <summary>Stop dispatching new files; running ones finish normally.</summary>
    public bool PauseAfterCurrent { get; set; }
    public bool StoppedForSpace => _pausedForSpace;

    private bool MayDeleteSource => _s.DeleteSourceAfterSuccess && (DeletionStillAllowed?.Invoke() ?? true);

    public QueueProcessor(AppSettings settingsSnapshot, ToolStatus tools, AnalysisCache cache)
    {
        _s = settingsSnapshot;
        _tools = tools;
        _cache = cache;
    }

    public void Stop() => _cts.Cancel();

    /// <summary>The job's settings merged with the current global safety / storage / CPU / tool settings.</summary>
    public AppSettings Effective(QueueItem item)
    {
        var job = ProfileFor?.Invoke(item);
        if (job is null || item.ProfileId is null) return _s;
        lock (_effective)
        {
            if (_effective.TryGetValue(item.ProfileId, out var e)) return e;
            e = job.Clone();
            // global, run-level settings always win
            e.DeleteSourceAfterSuccess = _s.DeleteSourceAfterSuccess;
            e.DeleteMode = _s.DeleteMode;
            e.AbAv1Verify = _s.AbAv1Verify;
            e.FailFast = _s.FailFast;
            e.DurationToleranceSeconds = _s.DurationToleranceSeconds;
            e.VerifyStreamCounts = _s.VerifyStreamCounts;
            e.VerifyCopiesWithHash = _s.VerifyCopiesWithHash;
            e.MinFreeSpaceGB = _s.MinFreeSpaceGB;
            e.SpaceSafetyFactor = _s.SpaceSafetyFactor;
            e.InsufficientSpace = _s.InsufficientSpace;
            e.ConcurrentJobs = _s.ConcurrentJobs;
            e.SearchCpu = _s.SearchCpu;
            e.EncodeCpu = _s.EncodeCpu;
            e.AbAv1Path = _s.AbAv1Path; e.FfmpegPath = _s.FfmpegPath; e.FfprobePath = _s.FfprobePath;
            e.TempFolder = _s.TempFolder;
            _effective[item.ProfileId] = e;
            return e;
        }
    }

    public IEncodingEngine EngineFor(QueueItem item)
    {
        var s = Effective(item);
        return item.Mode == EncodeMode.Manual ? new ManualAv1Engine(s.Manual, s, _tools) : new AbAv1Engine(s, _tools);
    }

    /// <summary>Cancel (or skip) one running file; the rest of the queue continues.</summary>
    public bool StopItem(QueueItem item, ItemStatus asStatus)
    {
        lock (_itemCts)
        {
            if (!_itemCts.TryGetValue(item.Id, out var cts)) return false;
            _userStopped[item.Id] = asStatus;
            if (item.IsPaused) { item.ActiveProcess?.Job?.Resume(); item.IsPaused = false; }
            cts.Cancel();
            return true;
        }
    }

    public static bool IsEligible(QueueItem item, RunMode mode)
    {
        if (item.Kind == ItemKind.Copy)
            return mode != RunMode.AnalyzeOnly && item.Status is ItemStatus.Waiting or ItemStatus.Ready;
        return mode switch
        {
            // CRF analysis only exists in AB-AV1 mode.
            RunMode.AnalyzeOnly => item.Mode == EncodeMode.AbAv1 && item.Status is ItemStatus.Waiting or ItemStatus.CrfFound or ItemStatus.Ready,
            // AB-AV1: analyzed files or files with a CRF override. Manual: every waiting file.
            RunMode.EncodeAnalyzed => item.Mode == EncodeMode.Manual
                ? item.Status is ItemStatus.Waiting or ItemStatus.Ready
                : item.Status is ItemStatus.Waiting or ItemStatus.CrfFound or ItemStatus.Ready && item.EffectiveCrf != null,
            RunMode.AnalyzeAndEncode => item.Status is ItemStatus.Waiting or ItemStatus.CrfFound or ItemStatus.Ready,
            _ => false,
        };
    }

    public Task RunAsync(IReadOnlyList<QueueItem> items, RunMode mode) =>
        RunAsync(() => Task.FromResult(items), mode);

    /// <summary>Process files in queue order. The queue is re-read before each file, so reordering and
    /// files added during the run are respected. Each file is processed at most once per run.</summary>
    public async Task RunAsync(Func<Task<IReadOnlyList<QueueItem>>> queue, RunMode mode)
    {
        var ct = _cts.Token;
        if (mode != RunMode.AnalyzeOnly)
        {
            try { await RecreateFolderTreesAsync(await queue(), ct); }
            catch (OperationCanceledException) { return; }
        }

        int concurrency = Math.Clamp(_s.ConcurrentJobs, 1, 8);
        using var sem = new SemaphoreSlim(concurrency);
        var running = new List<Task>();
        var started = new HashSet<Guid>();
        bool first = true;

        while (true)
        {
            if (ct.IsCancellationRequested || PauseAfterCurrent || _pausedForSpace) break;
            try { await sem.WaitAsync(ct); }
            catch (OperationCanceledException) { break; }
            if (ct.IsCancellationRequested || PauseAfterCurrent || _pausedForSpace) { sem.Release(); break; }

            var items = await queue();
            var item = items.FirstOrDefault(i => !started.Contains(i.Id) && IsEligible(i, mode));
            if (item is null)
            {
                sem.Release();
                running.RemoveAll(t => t.IsCompleted);
                if (running.Count == 0) break;
                await Task.WhenAny(running); // a running file may finish; nothing else to start meanwhile
                continue;
            }
            started.Add(item.Id);

            if (!first && item.Kind == ItemKind.Video) Log.Info("Starting next file");
            first = false;
            running.Add(Task.Run(async () =>
            {
                try { await ProcessItemAsync(item, mode, ct); }
                finally { sem.Release(); }
            }));
            running.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(running);
    }

    /// <summary>Recreate the complete directory tree (including empty folders) of every folder job involved.</summary>
    private async Task RecreateFolderTreesAsync(IReadOnlyList<QueueItem> items, CancellationToken ct)
    {
        var folders = Folders?.Invoke() ?? [];
        var ids = items.Where(i => i.FolderJobId != null && !i.Status.IsFinal()).Select(i => i.FolderJobId!.Value).ToHashSet();
        foreach (var f in folders.Where(f => ids.Contains(f.Id) || f.TreeCreatedUtc is null))
        {
            try
            {
                int n = await Task.Run(() => FolderMirror.EnsureDirectories(f, ct), ct);
                f.DirectoryCount = n;
                f.TreeCreatedUtc = DateTime.UtcNow;
                Log.Info($"Folder structure recreated: {n} sub-folder(s) under {f.DestinationRoot}", f.Name);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Error($"Could not recreate the folder structure in {f.DestinationRoot}: {ex.Message}", f.Name); }
        }
    }

    // =====================================================================================

    private async Task ProcessItemAsync(QueueItem item, RunMode mode, CancellationToken runToken)
    {
        var name = item.FileName;
        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        lock (_itemCts) _itemCts[item.Id] = itemCts;
        var ct = itemCts.Token;
        var s = Effective(item);
        try
        {
            item.ErrorMessage = null;
            item.ErrorWhy = null;
            item.ErrorFix = null;
            item.StatusDetail = null;
            item.ResetLiveStats();

            // ---------- 1. source identity ----------
            var fi = new FileInfo(item.SourcePath);
            if (!fi.Exists) { Fail(item, "Source file not found (moved or deleted?)"); return; }
            if (fi.Length != item.SourceSize || fi.LastWriteTimeUtc != item.SourceModifiedUtc)
            {
                if (item.Search != null) Log.Info("Source changed since last analysis — re-analyzing", name);
                item.SourceSize = fi.Length;
                item.SourceModifiedUtc = fi.LastWriteTimeUtc;
                item.QuickHash = null;
                item.Probe = null;
                item.InvalidateAnalysis();
            }

            if (item.Kind == ItemKind.Copy) { await CopyItemAsync(item, s, null, ct); return; }
            if (fi.Length == 0) { await SkipOrCopyAsync(item, s, "Source file is empty (0 bytes)", ct, isError: true); return; }

            // ---------- 2. probe ----------
            if (item.Probe is null)
            {
                SetStatus(item, ItemStatus.Analyzing, "Reading file information (ffprobe)…");
                try { item.Probe = await FfprobeService.ProbeAsync(_tools.FfprobePath!, item.SourcePath, ct); }
                catch (ProbeException ex)
                {
                    if (InMirror(item, s)) { await SkipOrCopyAsync(item, s, $"Not a readable video ({ex.Message})", ct); return; }
                    Fail(item, $"Invalid or corrupted video: {ex.Message}");
                    return;
                }
            }
            var probe = item.Probe!;
            if (probe.MainVideo is null) { await SkipOrCopyAsync(item, s, "No video stream found", ct, isError: !InMirror(item, s)); return; }
            if (s.SkipAv1Sources && string.Equals(probe.MainVideo.Codec, "av1", StringComparison.OrdinalIgnoreCase))
            { await SkipOrCopyAsync(item, s, "Source is already AV1", ct); return; }
            if (s.MinFileSizeMB > 0 && item.SourceSize < s.MinFileSizeMB * 1024 * 1024)
            { await SkipOrCopyAsync(item, s, $"Smaller than {s.MinFileSizeMB:0} MB", ct); return; }

            var engine = EngineFor(item);
            // Fail fast on stream problems before spending time on a CRF search.
            var streams = engine.PlanStreams(item, probe);
            if (streams.Blocker != null) { await SkipOrCopyAsync(item, s, streams.Blocker, ct); return; }

            // ---------- Manual AV1: user-chosen parameters, no ab-av1 analysis ----------
            if (item.Mode == EncodeMode.Manual)
            {
                double q = item.CrfOverride ?? s.Manual.Quality;
                await PlanAndEncodeAsync(item, s, engine, streams, probe, q, ct);
                return;
            }

            // ---------- 3. CRF (AB-AV1) ----------
            var fingerprint = AbAv1Commands.SearchFingerprint(s, _tools, probe);
            bool haveSearch = item.Search != null && item.SearchFingerprint == fingerprint;
            if (!haveSearch && item.Search != null)
            {
                Log.Info("Analysis settings changed since the last CRF search — re-analyzing", name);
                item.InvalidateAnalysis();
            }

            if (!haveSearch)
            {
                item.QuickHash ??= await Task.Run(() => SafeQuickHash(item.SourcePath), ct);
                var cached = _cache.Find(item, fingerprint);
                if (cached != null)
                {
                    ApplySearch(item, cached.Result, cached.Attempts, fingerprint, cached.TimestampUtc);
                    Log.Info($"Reusing cached CRF analysis from {cached.TimestampUtc.ToLocalTime():g}: CRF {Fmt.Num(cached.Result.Crf)}, VMAF {Fmt.Num(cached.Result.Vmaf, "0.00")}", name);
                    haveSearch = true;
                }
            }

            if (!haveSearch && !(mode == RunMode.EncodeAnalyzed && item.CrfOverride != null))
            {
                SetStatus(item, ItemStatus.Analyzing, "Starting CRF search…");
                Log.Info($"Starting CRF search (target VMAF {Fmt.Num(s.TargetVmaf)}; CPU {ResourcePlanner.Plan(s.SearchCpu).Description})", name);
                item.AddLog($"Starting CRF search, target VMAF {Fmt.Num(s.TargetVmaf)}");
                var sr = await CrfSearchRunner.RunAsync(item, s, _tools, ct);
                if (sr.Cancelled) { Interrupted(item, item.CrfOverride != null ? ItemStatus.Ready : ItemStatus.Waiting, "CRF search interrupted"); return; }
                if (sr.NoSuitableCrf)
                {
                    await SkipOrCopyAsync(item, s, $"No CRF reaches VMAF {Fmt.Num(s.TargetVmaf)} within the size limit " +
                               $"({Fmt.Num(s.MaxEncodedPercent ?? 80)}% of source) — AV1 would not save enough space. {sr.Error}", ct);
                    return;
                }
                if (sr.Result is null) { Fail(item, $"CRF search failed: {sr.Error}"); return; }

                List<CrfAttempt> attempts;
                lock (sr.Attempts) attempts = sr.Attempts.ToList();
                ApplySearch(item, sr.Result, attempts, fingerprint, DateTime.UtcNow);
                _cache.Put(new CachedAnalysis
                {
                    SourcePath = item.SourcePath, Size = item.SourceSize, ModifiedUtc = item.SourceModifiedUtc,
                    QuickHash = item.QuickHash, Fingerprint = fingerprint, Result = sr.Result,
                    Attempts = attempts, TargetVmaf = s.TargetVmaf,
                    Parameters = item.LastCrfSearchCommand, TimestampUtc = DateTime.UtcNow,
                });
                try { _cache.Save(); } catch (Exception ex) { Log.Warn($"Could not save analysis cache: {ex.Message}"); }

                Log.Success($"CRF detected: {Fmt.Num(sr.Result.Crf)}", name);
                Log.Info($"Target VMAF: {Fmt.Num(s.TargetVmaf)} · measured VMAF {Fmt.Num(sr.Result.Vmaf, "0.00")} · " +
                         $"estimated size {Fmt.Bytes(sr.Result.PredictedSize)} ({Fmt.Percent(sr.Result.PredictedPercent)})", name);
                item.AddLog($"Optimal CRF: {Fmt.Num(sr.Result.Crf)} (VMAF {Fmt.Num(sr.Result.Vmaf, "0.00")}, est. {Fmt.Bytes(sr.Result.PredictedSize)})");
            }

            if (mode == RunMode.AnalyzeOnly)
            {
                SetStatus(item, item.CrfOverride != null ? ItemStatus.Ready : ItemStatus.CrfFound, null);
                item.Activity = null;
                return;
            }

            if (item.EffectiveCrf is not double crf) { Fail(item, "No CRF available"); return; }
            await PlanAndEncodeAsync(item, s, engine, streams, probe, crf, ct);
        }
        catch (OperationCanceledException)
        {
            if (item.PartialPath != null && !item.PendingRename) { CleanupTemporaries(item.PartialPath, "interrupted"); item.PartialPath = null; }
            Interrupted(item, item.Kind == ItemKind.Copy || item.EffectiveCrf != null || item.Mode == EncodeMode.Manual ? ItemStatus.Ready : ItemStatus.Waiting, "Interrupted");
        }
        catch (Exception ex)
        {
            Log.FileOnly($"Unexpected error while processing {item.FileName}: {ex}"); // full details for bug reports
            Fail(item, $"{ex.GetType().Name}: {ex.Message}", sourcePreserved: File.Exists(item.SourcePath));
        }
        finally
        {
            lock (_itemCts) { _itemCts.Remove(item.Id); _userStopped.Remove(item.Id); }
        }
    }

    private static bool InMirror(QueueItem item, AppSettings s) =>
        item.FolderJobId != null && s.MirrorFolderTree && !string.IsNullOrWhiteSpace(s.DestinationFolder);

    /// <summary>In a mirrored folder a video that should not be encoded is copied unchanged, so the destination
    /// stays a complete replica. Outside folder mode the file is skipped (or failed for real errors).</summary>
    private async Task SkipOrCopyAsync(QueueItem item, AppSettings s, string reason, CancellationToken ct, bool isError = false)
    {
        if (InMirror(item, s))
        {
            Log.Info($"{reason} — copying the original unchanged to keep the folder replica complete", item.FileName);
            item.AddLog($"{reason} → copied unchanged");
            await CopyItemAsync(item, s, reason, ct);
            return;
        }
        if (isError) Fail(item, reason);
        else Skip(item, reason);
    }

    /// <summary>Shared by both engines: output planning → encode → verify → rename → optional deletion.</summary>
    private async Task PlanAndEncodeAsync(QueueItem item, AppSettings s, IEncodingEngine engine, StreamPlan streams, ProbeInfo probe,
        double quality, CancellationToken ct)
    {
        // ---------- 4. output planning ----------
        SetStatus(item, ItemStatus.Ready, null);
        var output = OutputPlanner.Plan(s, item, quality, engine.ContainerExtension(item.SourcePath), engine.PresetText, CollisionOverride);
        if (output.SkipReason != null) { Skip(item, output.SkipReason); return; }
        lock (_outputsInFlight)
        {
            if (!_outputsInFlight.Add(output.FinalPath))
            {
                Skip(item, $"Another file in this run is writing the same output: {output.FinalPath}");
                return;
            }
        }
        try
        {
            if (output.ExistingOutput) await ReuseExistingAsync(item, s, output, streams, probe, ct);
            else await EncodeVerifyFinalizeAsync(item, s, output, streams, probe, quality, engine, ct);
        }
        finally
        {
            lock (_outputsInFlight) _outputsInFlight.Remove(output.FinalPath);
        }
    }

    /// <summary>Resume support: an existing destination file is accepted only if it passes full verification.
    /// It is never overwritten; if invalid, the file is skipped and reported.</summary>
    private async Task ReuseExistingAsync(QueueItem item, AppSettings s, OutputPlan output, StreamPlan streams, ProbeInfo probe, CancellationToken ct)
    {
        SetStatus(item, ItemStatus.Verifying, "Destination exists — verifying it instead of re-encoding…");
        Log.Info($"Destination already exists, verifying it: {output.FinalPath}", item.FileName);
        var ver = await VerificationService.VerifyAsync(new VerificationInput(
            0, output.FinalPath, probe, item.SourcePath, item.SourceSize, item.SourceModifiedUtc,
            streams.ExpectedAudio, streams.ExpectedSubtitles, s.VerifyStreamCounts, s.DurationToleranceSeconds,
            s.AbAv1Verify), _tools, ct);
        foreach (var st in ver.Steps) item.AddLog($"{(st.Passed ? "✔" : "✘")} {st.Name}: {st.Detail}");
        if (!ver.Passed)
        {
            Skip(item, $"The destination file already exists but failed verification ({ver.FirstFailure}); it was not overwritten. " +
                       "Remove it or choose 'Replace existing files' to re-encode.");
            return;
        }
        item.OutputPath = output.FinalPath;
        Log.Success("Existing destination file verified — reused (no re-encode)", item.FileName);
        bool deleted = false;
        if (MayDeleteSource)
        {
            try { DeleteSource(item); deleted = true; }
            catch (Exception ex) { Log.Error($"Could not delete source: {ex.Message} — source kept", item.FileName); }
        }
        Finish(item, ver.OutputSize, deleted, "existing verified output reused");
    }

    private async Task EncodeVerifyFinalizeAsync(QueueItem item, AppSettings s, OutputPlan output, StreamPlan streams, ProbeInfo probe,
        double crf, IEncodingEngine engine, CancellationToken ct)
    {
        var name = item.FileName;
        foreach (var w in streams.Warnings) Log.Warn(w, name);
        item.TrackNotes = streams.Warnings.Count > 0 ? string.Join(Environment.NewLine, streams.Warnings) : null;

        // ---------- 5. disk space ----------
        long estimate = item.Search?.PredictedSize ?? item.SourceSize;
        if (!EnsureSpace(item, s, output.FinalPath, estimate)) return;

        // ---------- 6. encode ----------
        CleanupTemporaries(output.PartialPath, "stale");
        item.OutputPath = output.FinalPath;
        item.PartialPath = output.PartialPath;
        var qName = engine.Mode == EncodeMode.Manual ? ManualCommands.Spec(s.Manual.Encoder).QualityName : "CRF";
        item.EncoderText = engine.EncoderName;
        item.PresetText = engine.PresetText;
        item.EncodeParameters = engine.Mode == EncodeMode.AbAv1
            ? $"AB-AV1 · {engine.EncoderName}, CRF {Fmt.Arg(crf)}, preset {engine.PresetText}, target VMAF {Fmt.Num(s.TargetVmaf)}"
            : $"Manual AV1 · {ManualCommands.Describe(s.Manual)} ({qName} {Fmt.Arg(crf)})";
        long srcSize = item.SourceSize;
        DateTime srcMtime = item.SourceModifiedUtc;
        SetStatus(item, ItemStatus.Encoding, null);
        Log.Info($"Starting {(engine.Mode == EncodeMode.AbAv1 ? "AB-AV1" : "Manual AV1")} encode with {engine.EncoderName} ({qName} {Fmt.Arg(crf)}, preset {engine.PresetText}) → {output.FinalPath}", name);
        Log.Info($"CPU: {ResourcePlanner.Plan(s.EncodeCpu).Description}", name);
        item.AddLog($"Encoding with {engine.EncoderName}, {qName} {Fmt.Arg(crf)} to {Path.GetFileName(output.PartialPath)}");

        Interlocked.Add(ref _reservedBytes, estimate);
        EncodeOutcome enc;
        try { enc = await engine.EncodeAsync(item, output, streams, crf, ct); }
        finally { Interlocked.Add(ref _reservedBytes, -estimate); }

        if (enc.Cancelled)
        {
            CleanupTemporaries(output.PartialPath, "interrupted");
            item.PartialPath = null;
            Interrupted(item, ItemStatus.Ready, "Encode interrupted — partial output removed, source untouched");
            return;
        }
        if (enc.ExitCode != 0)
        {
            CleanupTemporaries(output.PartialPath, "failed");
            item.PartialPath = null;
            Fail(item, $"Encode failed: {enc.Error}");
            return;
        }
        Log.Info("Encoding completed", name);

        // ---------- 7. verify ----------
        SetStatus(item, ItemStatus.Verifying, "Verifying output…");
        item.Activity = "Verifying output…";
        Log.Info("Verifying output", name);
        bool ownDecode = s.AbAv1Verify && !engine.VerifiesDecode;
        var ver = await VerificationService.VerifyAsync(new VerificationInput(
            enc.ExitCode, output.PartialPath, probe, item.SourcePath, srcSize, srcMtime,
            streams.ExpectedAudio, streams.ExpectedSubtitles, s.VerifyStreamCounts,
            s.DurationToleranceSeconds, ownDecode), _tools, ct);
        foreach (var st in ver.Steps)
            item.AddLog($"{(st.Passed ? "✔" : "✘")} {st.Name}: {st.Detail}");

        if (!ver.Passed)
        {
            Log.Error($"Verification failed — {ver.FirstFailure}", name);
            CleanupTemporaries(output.PartialPath, "unverified");
            item.PartialPath = null;
            Fail(item, $"Verification failed: {ver.FirstFailure}");
            return;
        }
        Log.Success("Verification successful", name);

        // ---------- 8. move into place (+ delete source) ----------
        if (output.ReplacesSource)
        {
            // Same path as the source: the (verified) partial replaces it. Record intent first so a
            // crash between delete and rename is recoverable on next start.
            if (!MayDeleteSource)
            {
                Fail(item, "Source deletion was disabled during the run, so the source cannot be replaced. " +
                           $"The verified output was kept as {output.PartialPath}");
                return;
            }
            item.PendingRename = true;
            Changed(item);
            try { DeleteSource(item); }
            catch (Exception ex)
            {
                item.PendingRename = false;
                Fail(item, $"Could not delete the source to replace it ({ex.Message}). " +
                           $"The verified output was kept as {output.PartialPath}");
                return;
            }
            try
            {
                MoveWithRetry(output.PartialPath, output.FinalPath);
            }
            catch (Exception ex)
            {
                // The source is gone but the verified output exists; PendingRename stays set so the
                // rename is completed automatically on the next start.
                Fail(item, $"The source was replaced, but renaming the verified output failed ({ex.Message}). " +
                           $"Your video is safe at {output.PartialPath} and will be renamed on next start.",
                    sourcePreserved: false);
                return;
            }
            item.PendingRename = false;
            item.PartialPath = null;
            Finish(item, ver.OutputSize, sourceDeleted: true);
            return;
        }

        if (File.Exists(output.FinalPath) && !output.OverwriteExisting)
        {
            // Something created the final file while we were encoding — never overwrite it.
            Fail(item, $"Output file appeared during encoding and will not be overwritten: {output.FinalPath}. " +
                       $"The verified result was kept as {output.PartialPath}");
            return;
        }
        MoveWithRetry(output.PartialPath, output.FinalPath, output.OverwriteExisting);
        item.PartialPath = null;

        // Final check of the renamed (no longer temporary) file before anything is deleted.
        var final = new FileInfo(output.FinalPath);
        if (!final.Exists || final.Length != ver.OutputSize)
        {
            Fail(item, "The final output changed after verification (missing or different size). The source was kept.");
            return;
        }

        bool deleted = false;
        if (_s.DeleteSourceAfterSuccess && !MayDeleteSource)
            Log.Info("Source kept: deletion was disabled during the run", name);
        else if (MayDeleteSource)
        {
            item.Activity = "Deleting source…";
            try { DeleteSource(item); deleted = true; }
            catch (Exception ex) { Log.Error($"Could not delete source: {ex.Message} — source kept", name); }
        }
        Finish(item, ver.OutputSize, deleted);
    }

    // =====================================================================================
    // Copy jobs (non-video files of mirrored folders, or videos kept unchanged)

    private async Task CopyItemAsync(QueueItem item, AppSettings s, string? why, CancellationToken ct)
    {
        var plan = OutputPlanner.PlanCopy(s, item, CollisionOverride);
        if (plan.ReplacesSource) { Skip(item, plan.SkipReason ?? "Destination is the source"); return; }
        if (plan.SkipReason != null || plan.ExistingOutput)
        {
            // An identical file already there counts as done (resume / re-run). Different content is never overwritten.
            if (await SameContentAsync(item.SourcePath, plan.FinalPath, s.VerifyCopiesWithHash, ct))
            {
                FinishCopy(item, new FileInfo(plan.FinalPath).Length, plan.FinalPath, "already present (identical)");
                return;
            }
            Skip(item, plan.SkipReason ?? $"A different file already exists at the destination and was not overwritten: {plan.FinalPath}");
            return;
        }

        if (!EnsureSpace(item, s, plan.FinalPath, item.SourceSize, factor: 1.0)) return;

        item.OutputPath = plan.FinalPath;
        item.PartialPath = plan.PartialPath;
        item.EncoderText = "Copy (unchanged)";
        SetStatus(item, ItemStatus.Encoding, why is null ? null : $"{why} — copying unchanged");
        item.Activity = "Copying…";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.FinalPath)!);
            CleanupTemporaries(plan.PartialPath, "stale");
            await CopyWithProgressAsync(item, item.SourcePath, plan.PartialPath, ct);

            SetStatus(item, ItemStatus.Verifying, "Verifying copy…");
            var src = new FileInfo(item.SourcePath);
            var tmp = new FileInfo(plan.PartialPath);
            if (!tmp.Exists) { Fail(item, "Copy verification failed: the copied file does not exist."); return; }
            if (tmp.Length != src.Length)
            {
                CleanupTemporaries(plan.PartialPath, "unverified");
                item.PartialPath = null;
                Fail(item, $"Copy verification failed: size {tmp.Length:N0} bytes instead of {src.Length:N0}.");
                return;
            }
            if (s.VerifyCopiesWithHash && !await SameContentAsync(item.SourcePath, plan.PartialPath, true, ct))
            {
                CleanupTemporaries(plan.PartialPath, "unverified");
                item.PartialPath = null;
                Fail(item, "Copy verification failed: SHA-256 hashes differ.");
                return;
            }
            File.SetLastWriteTimeUtc(plan.PartialPath, src.LastWriteTimeUtc);
            MoveWithRetry(plan.PartialPath, plan.FinalPath, plan.OverwriteExisting);
            item.PartialPath = null;
            var fin = new FileInfo(plan.FinalPath);
            if (!fin.Exists || fin.Length != src.Length) { Fail(item, "Copy verification failed after renaming."); return; }
            FinishCopy(item, fin.Length, plan.FinalPath, why is null ? null : $"copied unchanged: {why}");
        }
        catch (OperationCanceledException)
        {
            CleanupTemporaries(plan.PartialPath, "interrupted");
            item.PartialPath = null;
            Interrupted(item, ItemStatus.Ready, "Copy interrupted — partial copy removed, source untouched");
        }
    }

    private static async Task CopyWithProgressAsync(QueueItem item, string from, string to, CancellationToken ct)
    {
        const int buf = 1 << 20;
        await using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, buf, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, buf, FileOptions.Asynchronous);
        long total = src.Length, done = 0;
        var buffer = new byte[buf];
        var last = DateTime.UtcNow;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if ((DateTime.UtcNow - last).TotalMilliseconds > 500)
            {
                last = DateTime.UtcNow;
                item.Progress = total > 0 ? 100.0 * done / total : 100;
                item.CurrentOutputSize = done;
            }
        }
        await dst.FlushAsync(ct);
        item.Progress = 100;
    }

    private static async Task<bool> SameContentAsync(string a, string b, bool hash, CancellationToken ct)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
        if (!hash) return true;
        return (await HashAsync(a, ct)).SequenceEqual(await HashAsync(b, ct));
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(fs, ct);
    }

    private void FinishCopy(QueueItem item, long size, string path, string? note)
    {
        item.OutputPath = path;
        item.ActualOutputSize = size;
        item.CompletedAtUtc = DateTime.UtcNow;
        item.Progress = 100;
        item.Activity = null;
        SetStatus(item, ItemStatus.Completed, note is null ? $"Copied ({Fmt.Bytes(size)})" : $"Copied — {note}");
        Log.Info($"Copied {item.RelativePath} ({Fmt.Bytes(size)}){(note is null ? "" : " — " + note)}", item.FileName);
    }

    // =====================================================================================

    /// <summary>Returns false (and pauses or skips) when the destination lacks space.</summary>
    private bool EnsureSpace(QueueItem item, AppSettings s, string finalPath, long estimate, double? factor = null)
    {
        long required = (long)(estimate * Math.Max(1.0, factor ?? s.SpaceSafetyFactor)) + (long)(s.MinFreeSpaceGB * 1024 * 1024 * 1024);
        var disk = Win32.DiskSpace(Path.GetDirectoryName(finalPath)!);
        long reservedByOthers = Interlocked.Read(ref _reservedBytes);
        if (disk is { } d && (long)d.Free - reservedByOthers < required)
        {
            var msg = $"Insufficient disk space for {item.FileName}: {Fmt.Bytes((long)d.Free - reservedByOthers)} free, " +
                      $"{Fmt.Bytes(required)} required.";
            if (s.InsufficientSpace == SpaceAction.SkipFile) { Skip(item, msg); return false; }
            _pausedForSpace = true;
            item.StatusDetail = "Waiting for disk space";
            Log.Warn(msg + " Queue paused.", item.FileName);
            Warning?.Invoke(msg + "\n\nThe queue has been paused. Free some space and start again.");
            Changed(item);
            return false;
        }
        return true;
    }

    private void DeleteSource(QueueItem item)
    {
        if (item.Kind == ItemKind.Copy) throw new InvalidOperationException("Copied files are never deleted");
        if (_s.DeleteMode == DeleteMode.RecycleBin) Win32.MoveToRecycleBin(item.SourcePath);
        else File.Delete(item.SourcePath);
        if (File.Exists(item.SourcePath)) throw new IOException("source still exists after delete");
        item.SourceDeleted = true;
        Log.Success(_s.DeleteMode == DeleteMode.RecycleBin ? "Source moved to Recycle Bin" : "Source deleted", item.FileName);
        item.AddLog("Source deleted");
    }

    private void Finish(QueueItem item, long outputSize, bool sourceDeleted, string? note = null)
    {
        item.ActualOutputSize = outputSize;
        item.CompletedAtUtc = DateTime.UtcNow;
        item.Progress = 100;
        item.Eta = null;
        item.Activity = null;
        var ratio = item.SourceSize > 0 ? $" ({100.0 * outputSize / item.SourceSize:0.0}% of source)" : "";
        SetStatus(item, sourceDeleted ? ItemStatus.Deleted : ItemStatus.Completed,
            $"{Fmt.Bytes(outputSize)}{ratio}" + (note is null ? "" : $" — {note}") +
            (item.TrackNotes is null ? "" : " — some tracks were converted or not kept (see Tracks)"));
        Log.Success($"Completed: {Fmt.Bytes(item.SourceSize)} → {Fmt.Bytes(outputSize)}{ratio}", item.FileName);
        var dur = item.Probe?.DurationSeconds;
        if (note is null)
            Log.Info($"Statistics: encode time {Fmt.Duration(item.Elapsed)}" +
                     (item.AverageFps is double af ? $", average {af:0.0} fps" : "") +
                     (dur is double d && d > 0 ? $", average bitrate {outputSize * 8 / d / 1000:0} kb/s" : "") +
                     (item.SourceSize > 0 ? $", saved {Fmt.Bytes(item.SourceSize - outputSize)}" : ""), item.FileName);
    }

    private static void ApplySearch(QueueItem item, CrfSearchResult r, List<CrfAttempt> attempts, string fingerprint, DateTime when)
    {
        item.Search = r;
        item.SearchFingerprint = fingerprint;
        item.SearchedAtUtc = when;
        Ui.Post(() =>
        {
            if (!ReferenceEquals(attempts, item.Attempts))
            {
                item.Attempts.Clear();
                foreach (var a in attempts) item.Attempts.Add(a);
            }
        });
    }

    private static void CleanupTemporaries(string partialPath, string reason)
    {
        foreach (var f in new[] { partialPath, OutputPlanner.AbAv1TempFile(partialPath) })
        {
            try
            {
                if (File.Exists(f)) { File.Delete(f); Log.Info($"Removed {reason} temporary file {Path.GetFileName(f)}"); }
            }
            catch (Exception ex) { Log.Warn($"Could not remove {f}: {ex.Message}"); }
        }
    }

    private static string? SafeQuickHash(string path)
    {
        try { return FileIdentity.QuickHash(path); } catch { return null; }
    }

    private void SetStatus(QueueItem item, ItemStatus st, string? detail)
    {
        item.Status = st;
        item.StatusDetail = detail;
        Changed(item);
    }

    private void Changed(QueueItem item)
    {
        item.UpdatedAtUtc = DateTime.UtcNow;
        ItemChanged?.Invoke(item);
    }

    private void Fail(QueueItem item, string message, bool sourcePreserved = true)
    {
        item.ErrorMessage = message;
        item.Activity = null;
        item.Eta = null;
        var state = sourcePreserved ? "Source: PRESERVED" : "Source: replaced by verified output";
        var explanation = ErrorExplainer.Explain(message);
        item.ErrorWhy = explanation.Why;
        item.ErrorFix = explanation.Fix;
        item.ErrorDetail = message;
        SetStatus(item, ItemStatus.Failed, $"FAILED — {state}");
        Log.Error($"FAILED: {message} — {state}", item.FileName);
        item.AddLog($"FAILED: {message}");
        item.AddLog(state);
    }

    /// <summary>Renames can briefly fail while antivirus/indexers scan the new file.</summary>
    private static void MoveWithRetry(string from, string to, bool overwrite = false)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(from, to, overwrite); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(1000 * attempt); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(1000 * attempt); }
        }
    }

    private void Skip(QueueItem item, string reason)
    {
        item.Activity = null;
        SetStatus(item, ItemStatus.Skipped, reason);
        Log.Warn($"Skipped: {reason}", item.FileName);
        item.AddLog($"Skipped: {reason}");
    }

    private void Interrupted(QueueItem item, ItemStatus back, string reason)
    {
        item.ResetLiveStats();
        lock (_itemCts)
        {
            if (_userStopped.TryGetValue(item.Id, out var userStatus))
            {
                back = userStatus;
                reason = (userStatus == ItemStatus.Skipped ? "Skipped by user" : "Cancelled by user") + " — source untouched, partial output removed";
            }
        }
        SetStatus(item, back, reason);
        Log.Warn(reason, item.FileName);
        item.AddLog(reason);
    }
}
