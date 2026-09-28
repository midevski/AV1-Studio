using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Data;
using AV1Studio.Models;
using AV1Studio.Mvvm;
using AV1Studio.Native;
using AV1Studio.Services;
using AV1Studio.Util;

namespace AV1Studio.ViewModels;

public sealed record FolderSummary(string Title, string Details, double Overall, double VideosPct, double CopiesPct, string State);

/// <summary>Job snapshots, folder jobs, queue maintenance and dialogs.</summary>
public sealed partial class MainViewModel
{
    // ================================================================= dialogs (set by the window)

    public Func<AppSettings, ToolStatus, AppSettings?>? SettingsDialog { get; set; }
    public Action? ManualDialog { get; set; }
    public Action<QueueItem, EncodeMode>? PreviewDialog { get; set; }
    public Action? AboutDialog { get; set; }
    public Action? FirstRunDialog { get; set; }
    /// <summary>Returns the policy to use for this run, or null to cancel.</summary>
    public Func<int, CollisionPolicy?>? CollisionDialog { get; set; }

    // ================================================================= settings snapshots per job

    private readonly Dictionary<string, AppSettings> _profiles = new();
    private readonly List<FolderJob> _folders = new();

    /// <summary>Snapshot the current settings for newly queued jobs; identical snapshots are shared.</summary>
    internal string CaptureProfile(AppSettings? keepLocationOf = null)
    {
        var snap = Settings.Clone();
        if (keepLocationOf != null)
        {
            // where a queued file goes is decided when it is added (folder jobs are built around it)
            snap.DestinationFolder = keepLocationOf.DestinationFolder;
            snap.PreserveFolderStructure = keepLocationOf.PreserveFolderStructure;
            snap.MirrorFolderTree = keepLocationOf.MirrorFolderTree;
        }
        // not part of a job's configuration
        snap.WindowWidth = 0; snap.WindowHeight = 0; snap.FirstRunDone = true;
        var json = JsonSerializer.Serialize(snap, JsonFile.Options);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
        lock (_profiles) _profiles.TryAdd(id, snap);
        return id;
    }

    public AppSettings? ProfileOf(QueueItem i)
    {
        if (i.ProfileId is null) return null;
        lock (_profiles) return _profiles.TryGetValue(i.ProfileId, out var p) ? p : null;
    }

    /// <summary>Settings the file will be encoded with (its snapshot, or the current settings for old items).</summary>
    public AppSettings JobSettings(QueueItem i) => ProfileOf(i) ?? Settings;

    public RelayCommand ApplySettingsToSelectedCommand { get; private set; } = null!;
    public RelayCommand ClearQueueCommand { get; private set; } = null!;
    public RelayCommand ClearCompletedCommand { get; private set; } = null!;
    public RelayCommand ClearFailedCommand { get; private set; } = null!;
    public RelayCommand RetryFailedCommand { get; private set; } = null!;
    public RelayCommand OpenManualCommand { get; private set; } = null!;
    public RelayCommand PreviewSelectedCommand { get; private set; } = null!;
    public RelayCommand AboutCommand { get; private set; } = null!;
    public RelayCommand ExportDiagnosticsCommand { get; private set; } = null!;
    public RelayCommand ShowFirstRunCommand { get; private set; } = null!;

    private void InitJobCommands()
    {
        ApplySettingsToSelectedCommand = new RelayCommand(ApplySettingsToSelected, () => SelectedItems.Any(i => !i.IsBusy && !i.Status.IsDone()));
        ClearQueueCommand = new RelayCommand(ClearQueue, () => Items.Any(i => !i.IsBusy));
        ClearCompletedCommand = new RelayCommand(() => RemoveWhere(i => i.Status.IsDone(), "completed"), () => Items.Any(i => i.Status.IsDone()));
        ClearFailedCommand = new RelayCommand(() => RemoveWhere(i => i.Status == ItemStatus.Failed, "failed"), () => Items.Any(i => i.Status == ItemStatus.Failed));
        RetryFailedCommand = new RelayCommand(RetryFailed, () => Items.Any(i => i.Status is ItemStatus.Failed or ItemStatus.Cancelled));
        OpenManualCommand = new RelayCommand(() => ManualDialog?.Invoke());
        PreviewSelectedCommand = new RelayCommand(
            () => { if (SelectedItem is { } i) PreviewDialog?.Invoke(i, i.Kind == ItemKind.Copy ? SelectedMode : i.Mode); },
            () => SelectedItem?.Probe != null && SelectedItem.Kind == ItemKind.Video && !IsPreviewRunning);
        AboutCommand = new RelayCommand(() => AboutDialog?.Invoke());
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        ShowFirstRunCommand = new RelayCommand(() => FirstRunDialog?.Invoke());
    }

    private void ApplySettingsToSelected()
    {
        var targets = SelectedItems.Where(i => !i.IsBusy && !i.Status.IsDone()).ToList();
        if (targets.Count == 0) return;
        if (ConfirmDialog?.Invoke("Apply current settings",
                $"Replace the settings of {targets.Count} queued file(s) with the current settings (and the current mode: " +
                $"{(SelectedMode == EncodeMode.AbAv1 ? "AB-AV1" : "Manual AV1")})?") != true) return;
        var id = CaptureProfile();
        foreach (var i in targets)
        {
            i.ProfileId = id;
            if (i.Kind == ItemKind.Video) i.Mode = SelectedMode;
        }
        Log.Info($"Applied the current settings to {targets.Count} queued file(s)");
        _dirty = true;
        RefreshPlanned();
    }

    /// <summary>"Clear Library Queue": removes every file that is not being processed. Files on disk are never touched.</summary>
    private void ClearQueue()
    {
        var removable = Items.Where(i => !i.IsBusy).ToList();
        if (removable.Count == 0) return;
        if (ConfirmDialog?.Invoke("Clear Library Queue",
                $"Remove {removable.Count} file(s) from the queue?\n\nOnly the queue entries are removed — your source files, encoded files and copied files are NOT deleted." +
                (removable.Count < Items.Count ? "\n\nFiles that are currently being processed stay in the queue." : "")) != true) return;
        RemoveWhere(i => !i.IsBusy, "queued", confirm: false);
    }

    private void RemoveWhere(Func<QueueItem, bool> pred, string what, bool confirm = false)
    {
        var list = Items.Where(i => !i.IsBusy && pred(i)).ToList();
        if (list.Count == 0) return;
        RemoveItems(list);
        Log.Info($"Removed {list.Count} {what} file(s) from the queue (files on disk untouched)");
    }

    /// <summary>Removes queue entries (never files on disk) and everything that still points at them.</summary>
    private void RemoveItems(IReadOnlyCollection<QueueItem> list)
    {
        var gone = list.ToHashSet();
        Items.RemoveRange(gone);
        if (SelectedItem != null && gone.Contains(SelectedItem)) SelectedItem = null;
        SelectedItems = SelectedItems.Where(i => !gone.Contains(i)).ToList();
        if (EncodeTarget != null && gone.Contains(EncodeTarget)) EncodeTarget = Items.FirstOrDefault(i => i.Kind == ItemKind.Video);
        // folder jobs without remaining items are forgotten (their files stay on disk)
        var used = Items.Where(i => i.FolderJobId != null).Select(i => i.FolderJobId!.Value).ToHashSet();
        lock (_folders) _folders.RemoveAll(f => !used.Contains(f.Id));
        _dirty = true;
        RefreshStorage();
        RefreshFolderProgress();
        CommandManagerInvalidate();
    }

    private void RetryFailed()
    {
        int n = 0;
        foreach (var i in Items.Where(i => !i.IsBusy && i.Status is ItemStatus.Failed or ItemStatus.Cancelled))
        {
            if (!File.Exists(i.SourcePath)) continue;
            i.Status = i.Search != null ? ItemStatus.Ready : ItemStatus.Waiting;
            i.StatusDetail = null; i.ErrorMessage = null; i.ErrorWhy = null; i.ErrorFix = null;
            i.ResetLiveStats();
            n++;
        }
        Log.Info($"Re-queued {n} failed/cancelled file(s)");
        _dirty = true;
    }

    // ================================================================= adding files & folders

    /// <summary>
    /// Files → one job each. Folders → with a destination folder and "mirror folder tree" on, a folder job
    /// that recreates the complete tree (videos encoded, other files copied, empty folders kept);
    /// otherwise the videos inside are queued (output next to each source or in the destination).
    /// </summary>
    private async Task AddPathsCoreAsync(List<string> inputs)
    {
        var existing = new HashSet<string>(Items.Select(i => OutputPlanner.Normalize(i.SourcePath)), StringComparer.OrdinalIgnoreCase);
        var settings = Settings.Clone();
        var mode = SelectedMode;
        var profile = CaptureProfile();
        bool mirror = settings.MirrorFolderTree && !string.IsNullOrWhiteSpace(settings.DestinationFolder);

        var (newJobs, found) = await Task.Run(() =>
        {
            var jobs = new List<FolderJob>();
            var list = new List<QueueItem>();
            var plainInputs = new List<string>();
            foreach (var input in inputs)
            {
                if (mirror && Directory.Exists(input))
                {
                    var scan = FolderMirror.Scan(input, settings, CancellationToken.None);
                    var (job, items) = FolderMirror.CreateJob(scan, settings, mode, profile, existing);
                    jobs.Add(job);
                    list.AddRange(items);
                    Log.Info($"Folder {job.Name}: {items.Count(i => i.Kind == ItemKind.Video)} video(s) to encode, " +
                             $"{items.Count(i => i.Kind == ItemKind.Copy)} other file(s) to copy, {scan.Directories.Count} sub-folder(s) to recreate → {job.DestinationRoot}");
                }
                else plainInputs.Add(input);
            }
            foreach (var (path, root) in FileScanner.Scan(plainInputs, settings, CancellationToken.None))
            {
                var norm = OutputPlanner.Normalize(path);
                if (!existing.Add(norm)) continue;
                try
                {
                    var fi = new FileInfo(norm);
                    list.Add(new QueueItem
                    {
                        SourcePath = norm, SourceRoot = root, SourceSize = fi.Length, SourceModifiedUtc = fi.LastWriteTimeUtc,
                        Mode = mode, ProfileId = profile,
                    });
                }
                catch (Exception ex) { Log.Warn($"Cannot read {path}: {ex.Message}"); }
            }
            return (jobs, list);
        });

        // the queue may have changed while scanning: drop anything queued meanwhile
        var now = new HashSet<string>(Items.Select(i => OutputPlanner.Normalize(i.SourcePath)), StringComparer.OrdinalIgnoreCase);
        found = found.Where(f => now.Add(OutputPlanner.Normalize(f.SourcePath))).ToList();
        lock (_folders) _folders.AddRange(newJobs);
        Items.AddRange(found);

        int videos = found.Count(f => f.Kind == ItemKind.Video);
        Log.Info(found.Count == 0 && newJobs.Count == 0 ? "No new files found (already queued or no videos)."
            : $"Queued {videos} video(s) in {(mode == EncodeMode.AbAv1 ? "AB-AV1" : "Manual AV1")} mode" +
              (found.Count > videos ? $" and {found.Count - videos} file(s) to copy" : "") + $" ({Fmt.Bytes(found.Sum(f => f.SourceSize))}).");
        if (newJobs.Any(j => !found.Any(f => f.FolderJobId == j.Id)))
            Log.Info("A folder without files was added: its folder structure will be recreated when the queue starts.");
        RefreshPlanned();
        EncodeTarget ??= found.FirstOrDefault(f => f.Kind == ItemKind.Video);
        _dirty = true;
        RefreshStorage();
        RefreshFolderProgress();
        foreach (var i in found.Where(f => f.Kind == ItemKind.Video)) _ = ProbeInBackgroundAsync(i);
    }

    // ================================================================= output container checks

    /// <summary>MP4 output needs an FFmpeg that can write AV1 into MP4 (never silently switched to MKV), and
    /// the user is told before starting which tracks MP4 cannot store.</summary>
    private bool CheckOutputContainers(List<QueueItem> targets)
    {
        var videos = targets.Where(i => i.Kind == ItemKind.Video).ToList();
        var mp4Jobs = videos.Where(i => OutputPlanner.ContainerExtension(JobSettings(i), i.SourcePath) == "mp4").ToList();
        if (mp4Jobs.Count > 0 && !Tools.CanWriteAv1Mp4)
        {
            InfoDialog?.Invoke("MP4 output unavailable",
                $"{mp4Jobs.Count} file(s) are set to MP4 output, but this FFmpeg build cannot write AV1 video into MP4 files.\n\n" +
                "Choose MKV as the output container, or install a current FFmpeg build (Settings › Tools).");
            return false;
        }

        // Tracks that the chosen container cannot store (e.g. image subtitles or fonts in MP4).
        var losses = new List<string>();
        foreach (var i in mp4Jobs.Where(i => i.Probe != null))
        {
            var s = JobSettings(i);
            var opts = i.Mode == EncodeMode.Manual ? TrackOptions.FromManual(s.Manual) : TrackOptions.FromAbAv1(s);
            var plan = AbAv1Commands.PlanStreams(opts, i.Probe!, "mp4", i.AudioSelection, i.SubtitleSelection);
            foreach (var w in plan.Warnings.Where(w => w.Contains("dropped", StringComparison.OrdinalIgnoreCase)))
                losses.Add($"• {i.FileName}: {w}");
        }
        if (losses.Count == 0) return true;
        return ConfirmDialog?.Invoke("Some tracks cannot be stored in MP4",
            string.Join("\n", losses.Take(12)) + (losses.Count > 12 ? $"\n… and {losses.Count - 12} more" : "") +
            "\n\nThese tracks will not be in the MP4 files. Choose MKV as the output container to keep them.\n\nContinue with MP4?") ?? false;
    }

    // ================================================================= folder progress

    private List<FolderSummary> _folderSummaries = new();
    public List<FolderSummary> FolderSummaries { get => _folderSummaries; private set { Set(ref _folderSummaries, value); Notify(nameof(HasFolders)); } }
    public bool HasFolders => FolderSummaries.Count > 0;

    private string _currentOperation = "";
    public string CurrentOperation { get => _currentOperation; private set => Set(ref _currentOperation, value); }

    private void RefreshFolderProgress()
    {
        List<FolderJob> folders;
        lock (_folders) folders = _folders.ToList();
        var list = new List<FolderSummary>();
        if (folders.Count == 0 && FolderSummaries.Count == 0) { UpdateCurrentOperation(); return; }
        var byFolder = Items.Where(i => i.FolderJobId != null).ToLookup(i => i.FolderJobId!.Value); // one pass over the queue
        foreach (var f in folders)
        {
            var items = byFolder[f.Id].ToList();
            var vids = items.Where(i => i.Kind == ItemKind.Video).ToList();
            var copies = items.Where(i => i.Kind == ItemKind.Copy).ToList();
            double Frac(QueueItem i) => i.Status.IsFinal() || i.Status == ItemStatus.Failed ? 1 : i.Status is ItemStatus.Encoding or ItemStatus.Verifying ? i.Progress / 100 : 0;
            double vp = vids.Count == 0 ? 1 : vids.Sum(Frac) / vids.Count;
            double cp = copies.Count == 0 ? 1 : copies.Sum(Frac) / copies.Count;
            double overall = items.Count == 0 ? (f.TreeCreatedUtc != null ? 1 : 0) : items.Sum(Frac) / items.Count;
            int failed = items.Count(i => i.Status == ItemStatus.Failed);
            int cancelled = items.Count(i => i.Status == ItemStatus.Cancelled);
            bool allFinal = items.All(i => i.Status.IsFinal() || i.Status == ItemStatus.Failed);
            bool nothingDone = items.Count > 0 && items.All(i => i.StatusDetail?.Contains("already present") == true || i.StatusDetail?.Contains("reused") == true);
            string state = items.Count == 0 && f.TreeCreatedUtc != null ? "Completed — nothing to process (folders recreated)"
                : !allFinal ? (items.Any(i => i.IsBusy) ? "Processing" : "Queued")
                : cancelled > 0 ? "Cancelled"
                : failed > 0 ? $"Completed with errors ({failed} failed)"
                : nothingDone ? "Completed — nothing to process"
                : "Completed";
            string details = $"Videos {vids.Count(i => i.Status.IsDone())}/{vids.Count} · Files copied {copies.Count(i => i.Status.IsDone())}/{copies.Count}" +
                             (failed > 0 ? $" · {failed} failed" : "") + $" · → {f.DestinationRoot}";
            list.Add(new FolderSummary($"📁 {f.Name}", details, overall * 100, vp * 100, cp * 100, state));
        }
        FolderSummaries = list;
        UpdateCurrentOperation();
    }

    private void UpdateCurrentOperation()
    {
        var cur = Items.FirstOrDefault(i => i.IsBusy);
        CurrentOperation = cur is null ? "" : $"{cur.StatusText}: {cur.RelativePath}";
    }

    // ================================================================= collisions ("Ask")

    /// <summary>With the "Ask" policy, count existing destinations before the run and let the user decide.</summary>
    private bool ResolveCollisions(List<QueueItem> targets, out CollisionPolicy? policy)
    {
        policy = null;
        if (Settings.Collision != CollisionPolicy.Ask) return true;
        int existing = 0;
        foreach (var i in targets)
        {
            var s = JobSettings(i);
            try
            {
                var plan = i.Kind == ItemKind.Copy ? OutputPlanner.PlanCopy(s, i, CollisionPolicy.ReuseIfValid)
                    : OutputPlanner.Plan(s, i, i.EffectiveCrf, i.Mode == EncodeMode.Manual
                        ? OutputPlanner.ContainerExtension(s.Container, i.SourcePath) : null, null, CollisionPolicy.ReuseIfValid);
                if (plan.ExistingOutput) existing++;
            }
            catch { }
        }
        if (existing == 0) return true;
        policy = CollisionDialog?.Invoke(existing);
        return policy != null;
    }

    // ================================================================= storage preflight for folders

    private bool PreflightFolderSpace(List<QueueItem> targets)
    {
        var groups = targets.GroupBy(i => Path.GetPathRoot(OutputPlanner.OutputDirectory(JobSettings(i), i)) ?? "");
        foreach (var g in groups)
        {
            var disk = Win32.DiskSpace(g.Key);
            if (disk is null) continue;
            long copies = g.Where(i => i.Kind == ItemKind.Copy).Sum(i => i.SourceSize);
            var videos = g.Where(i => i.Kind == ItemKind.Video).Select(EstimateOutput).ToList();
            // With source deletion, videos are processed one at a time and space is freed as you go.
            long videoNeed = Settings.DeleteSourceAfterSuccess && Settings.ConcurrentJobs == 1 ? (videos.Count > 0 ? videos.Max() : 0) : videos.Sum();
            long need = copies + (long)(videoNeed * Settings.SpaceSafetyFactor);
            if ((long)disk.Value.Free >= need) continue;
            if (ConfirmDialog?.Invoke("Disk space may be insufficient",
                    $"Destination drive {g.Key}: {Fmt.Bytes((long)disk.Value.Free)} free.\n\n" +
                    $"Estimated requirement: {Fmt.Bytes(need)} ({Fmt.Bytes(copies)} of files to copy + " +
                    $"{Fmt.Bytes((long)(videoNeed * Settings.SpaceSafetyFactor))} for encoded videos" +
                    (Settings.DeleteSourceAfterSuccess && Settings.ConcurrentJobs == 1 ? ", one video at a time with source deletion" : "") + ").\n\n" +
                    "Estimates use ab-av1 predictions where available, otherwise the source size. The queue pauses automatically if a file does not fit. Start anyway?") != true)
                return false;
        }
        return true;
    }

    // ================================================================= diagnostics

    public string DiagnosticsText => Diagnostics.Build(SystemInfo, Tools, Settings);

    /// <summary>The first-run check was completed (or dismissed); never shown automatically again.</summary>
    public void MarkFirstRunDone()
    {
        if (Settings.FirstRunDone) return;
        Settings.FirstRunDone = true;
        SaveSettings();
        Log.Info("First-run system check completed");
    }

    private void ExportDiagnostics()
    {
        var path = SaveFileDialog?.Invoke("Save diagnostic report", $"av1-studio-diagnostics_{DateTime.Now:yyyyMMdd_HHmm}.txt");
        if (path is null) return;
        try { File.WriteAllText(path, DiagnosticsText, Encoding.UTF8); Log.Info("Diagnostic report saved"); }
        catch (Exception ex) { Log.Error($"Could not save the diagnostic report: {ex.Message}"); }
    }

    // ================================================================= profiles (quick selector)

    public IReadOnlyList<string> ProfileNames { get; } = [.. EncodingProfiles.All.Select(p => p.Name), EncodingProfiles.Custom];

    public string SelectedProfile
    {
        get => IsManualMode ? (EncodingProfiles.Find(Manual.ProfileName)?.Name ?? EncodingProfiles.Custom)
                            : (EncodingProfiles.Find(Settings.QualityPreset)?.Name ?? EncodingProfiles.Custom);
        set
        {
            if (value is null || value == SelectedProfile) return;
            var p = EncodingProfiles.Find(value);
            if (p is null)
            {
                MarkCustomProfile(IsManualMode); // "Custom" chosen: keep the current values
                return;
            }
            _applyingProfile = true;
            try
            {
                if (IsManualMode) EncodingProfiles.ApplyManual(Manual, p);
                else
                {
                    EncodingProfiles.ApplyAbAv1(Settings, p);
                    Notify(nameof(TargetVmaf), nameof(SelectedPreset), nameof(QualityPresetDescription));
                    SaveSettings();
                }
            }
            finally { _applyingProfile = false; }
            Log.Info($"Profile \"{p.Name}\" applied to {(IsManualMode ? "Manual AV1" : "AB-AV1")} settings (you can still change every value)");
            Notify(nameof(SelectedProfile), nameof(SelectedProfileDescription));
            RefreshPlanned();
        }
    }

    private bool _applyingProfile;

    /// <summary>A value was changed by hand: the profile no longer describes the settings, so it shows "Custom".</summary>
    private void MarkCustomProfile(bool manual = false)
    {
        if (_applyingProfile) return;
        if (manual)
        {
            if (Manual.ProfileName == EncodingProfiles.Custom) return;
            Manual.ProfileName = EncodingProfiles.Custom;
        }
        else
        {
            if (Settings.QualityPreset == EncodingProfiles.Custom) return;
            Settings.QualityPreset = EncodingProfiles.Custom;
        }
        Notify(nameof(SelectedProfile), nameof(SelectedProfileDescription));
    }

    // ================================================================= queued files follow the screen

    private bool _jobSyncScheduled;

    /// <summary>Coalesces many setting changes (typing, profiles) into one queue update.</summary>
    private void ScheduleJobSync()
    {
        if (_jobSyncScheduled) return;
        _jobSyncScheduled = true;
        Ui.Enqueue(() =>
        {
            _jobSyncScheduled = false;
            SyncQueuedJobs();
        });
    }

    /// <summary>
    /// Files that have not started yet always use the settings shown on screen. If a change affects the CRF
    /// search (VMAF, preset, samples, filters…), a CRF found with the old settings is discarded and the file is
    /// analyzed again. Files being processed or finished keep the settings they ran with; each queued file keeps
    /// its output location.
    /// </summary>
    internal void SyncQueuedJobs()
    {
        var pending = Items.Where(i => !i.IsBusy && i.Status is ItemStatus.Waiting or ItemStatus.CrfFound or ItemStatus.Ready).ToList();
        if (pending.Count == 0) return;
        var newIds = new Dictionary<string, string>();
        int changed = 0, reanalyze = 0;
        foreach (var i in pending)
        {
            var key = i.ProfileId ?? "";
            if (!newIds.TryGetValue(key, out var id))
            {
                id = CaptureProfile(ProfileOf(i));
                newIds[key] = id;
            }
            if (i.ProfileId == id) continue;
            i.ProfileId = id;
            changed++;
            if (i.Kind == ItemKind.Video && i.Mode == EncodeMode.AbAv1 && i.Search != null && i.Probe != null
                && AbAv1Commands.SearchFingerprint(JobSettings(i), Tools, i.Probe) != i.SearchFingerprint)
            {
                i.InvalidateAnalysis();
                i.Status = ItemStatus.Waiting;
                i.StatusDetail = "Settings changed — the CRF will be searched again";
                reanalyze++;
            }
        }
        if (changed == 0) return;
        if (reanalyze > 0) Log.Info($"Settings changed: {reanalyze} analyzed file(s) will run a new CRF search");
        _dirty = true;
        RefreshPlanned();
        RefreshStorage();
    }

    public string SelectedProfileDescription =>
        (EncodingProfiles.Find(SelectedProfile)?.Description ?? "Your own values.") +
        "\n\nProfiles are starting points, not universally optimal settings: results depend on the source.";
}
