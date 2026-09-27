using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using AV1Studio.Models;
using AV1Studio.Mvvm;
using AV1Studio.Native;
using AV1Studio.Services;
using AV1Studio.Util;

namespace AV1Studio.ViewModels;

public sealed record PresetOption(int? Value, string Label);

public sealed partial class MainViewModel : ObservableObject
{
    private AppSettings _settings = null!;
    public AppSettings Settings
    {
        get => _settings;
        private set { _settings = value; OnPropertyChanged(); }
    }
    public ObservableCollection<QueueItem> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    private AnalysisCache _cache = new();
    private LibraryStore _library = new();
    private QueueProcessor? _processor;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statsTimer;
    private bool _dirty;
    private readonly SemaphoreSlim _probeGate = new(2);

    public Func<string, string, bool>? ConfirmDialog { get; set; }
    public Action<string, string>? InfoDialog { get; set; }
    public Func<string, string, string?>? SaveFileDialog { get; set; }
    public Func<QueueItem, bool>? TracksDialog { get; set; }

    public MainViewModel()
    {
        Settings = JsonFile.Load<AppSettings>(AppPaths.Settings) ?? new AppSettings();
        Settings.Manual ??= new ManualSettings();
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        // Folder jobs are shown grouped by their (relative) folder, mirroring the library structure.
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(QueueItem.GroupKey)));
        LogView = CollectionViewSource.GetDefaultView(LogEntries);
        LogView.Filter = FilterLog;
        LibraryView = CollectionViewSource.GetDefaultView(LibraryEntries);
        LibraryView.Filter = FilterLibrary;
        AttachManual();

        Log.Entry += OnLogEntry;

        _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => SaveIfDirty(), Dispatcher.CurrentDispatcher);
        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => RefreshStorage(), Dispatcher.CurrentDispatcher);

        PresetOptions =
        [
            new PresetOption(null, "ab-av1 default (preset 8)"),
            .. Enumerable.Range(1, 13).Select(i => new PresetOption(i, $"Preset {i} — {PresetDescription(i)}")),
        ];

        AddFilesCommand = new RelayCommand(AddFiles, () => !IsScanning);
        AddFolderCommand = new RelayCommand(AddFolder, () => !IsScanning);
        ScanSourceCommand = new RelayCommand(() => _ = AddPathsAsync([Settings.SourceFolder]),
            () => !IsScanning && Directory.Exists(Settings.SourceFolder));
        BrowseSourceCommand = new RelayCommand(BrowseSource);
        BrowseDestinationCommand = new RelayCommand(BrowseDestination);
        ClearDestinationCommand = new RelayCommand(() => DestinationFolder = "");

        AnalyzeAllCommand = new RelayCommand(() => _ = StartAsync(RunMode.AnalyzeOnly, null), CanStart);
        EncodeAnalyzedCommand = new RelayCommand(() => _ = StartAsync(RunMode.EncodeAnalyzed, null), CanStart);
        AnalyzeAndEncodeCommand = new RelayCommand(() => _ = StartAsync(RunMode.AnalyzeAndEncode, null), CanStart);
        AnalyzeSelectedCommand = new RelayCommand(() => _ = StartAsync(RunMode.AnalyzeOnly, SelectedItems.ToList()), () => CanStart() && SelectedItems.Count > 0);
        EncodeSelectedCommand = new RelayCommand(() => _ = StartAsync(RunMode.AnalyzeAndEncode, SelectedItems.ToList()), () => CanStart() && SelectedItems.Count > 0);
        PauseCommand = new RelayCommand(TogglePause, () => IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);

        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => SelectedItems.Count > 0);
        RetrySelectedCommand = new RelayCommand(RetrySelected, () => SelectedItems.Count > 0);
        ReanalyzeSelectedCommand = new RelayCommand(ReanalyzeSelected, () => SelectedItems.Count > 0 && !IsRunning);
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => Items.Any(i => i.Status.IsFinal()));
        ChooseTracksCommand = new RelayCommand(ChooseTracks, () => SelectedItem?.Probe != null && !SelectedItem.IsBusy);
        OpenSourceFolderCommand = new RelayCommand(() => Reveal(SelectedItem?.SourcePath), () => SelectedItem != null);
        OpenOutputFolderCommand = new RelayCommand(() => Reveal(SelectedItem?.OutputPath), () => SelectedItem?.OutputPath != null);

        OpenSettingsCommand = new RelayCommand(OpenSettings, () => !IsRunning);
        RedetectToolsCommand = new RelayCommand(() => _ = DetectToolsAsync(), () => !IsRunning);
        OpenLogFolderCommand = new RelayCommand(() => Reveal(Log.CurrentFile));
        CopyTextCommand = new RelayCommand(p => { if (p is string s && s.Length > 0) Clipboard.SetText(s); });
        ClearLogCommand = new RelayCommand(() => LogEntries.Clear());
        CleanupPartialsCommand = new RelayCommand(CleanupPartials, () => !IsRunning);
        ToggleHardwareCommand = new RelayCommand(() => HardwareEncoding = !HardwareEncoding, () => CanToggleHardware);
        InitEncodeCommands();
        InitQueueCommands();
        InitLibraryCommands();
        InitLogCommands();
        InitJobCommands();
    }

    // =================================================================== startup / shutdown

    private bool _initialized;

    public async Task InitializeAsync()
    {
        if (_initialized) return; // idempotent: never load the queue twice
        _initialized = true;
        AppPaths.EnsureCreated();
        Log.Info($"{AppInfo.NameAndVersion} started");
        _ = Task.Run(() => Log.Cleanup(Settings.LogRetentionDays));
        SystemInfo = await Task.Run(SystemInfo.Detect);
        Log.Info($"Windows: {SystemInfo.WindowsVersion}");
        Log.Info($"CPU: {SystemInfo.Cpu} ({SystemInfo.CpuVendor}, {SystemInfo.Architecture}, {SystemInfo.LogicalCores} threads) · RAM {Fmt.Bytes((long)SystemInfo.RamBytes)}");
        foreach (var g in SystemInfo.Gpus) Log.Info($"GPU: {g.Name} ({g.Vendor}, {(g.IsIntegrated ? "integrated" : "discrete")})");
        Log.Info($"CPU usage — CRF search: {ResourcePlanner.Plan(Settings.SearchCpu).Description}; encoding: {ResourcePlanner.Plan(Settings.EncodeCpu).Description}");

        _cache = await Task.Run(AnalysisCache.Load);
        _library = await Task.Run(LibraryStore.Load);
        var state = await Task.Run(() => JsonFile.Load<QueueState>(AppPaths.State));
        if (state != null)
        {
            lock (_profiles) foreach (var (k, v) in state.Profiles) _profiles[k] = v;
            lock (_folders) _folders.AddRange(state.Folders);
            var seen = new HashSet<Guid>();
            foreach (var i in state.Items)
                if (seen.Add(i.Id)) Items.Add(i); // never load the same job twice
        }
        RecoverInterrupted();

        await DetectToolsAsync();
        _saveTimer.Start();
        _statsTimer.Start();
        RefreshStorage();
        RefreshPlanned();
        RefreshLibrary();
        EncodeTarget ??= Items.FirstOrDefault(i => !i.Status.IsFinal());

        foreach (var i in Items.Where(i => i.Probe is null && !i.Status.IsFinal())) _ = ProbeInBackgroundAsync(i);
    }

    /// <summary>Crash/close recovery: anything that was running is re-queued, verified-but-unrenamed
    /// outputs are completed, and leftover partial files are offered for deletion.</summary>
    private void RecoverInterrupted()
    {
        var partials = new List<string>();
        foreach (var i in Items)
        {
            if (i.PendingRename && i.PartialPath != null && i.OutputPath != null)
            {
                if (!File.Exists(i.SourcePath) && File.Exists(i.PartialPath))
                {
                    try
                    {
                        File.Move(i.PartialPath, i.OutputPath);
                        i.PendingRename = false; i.PartialPath = null; i.SourceDeleted = true;
                        i.ActualOutputSize = new FileInfo(i.OutputPath).Length;
                        i.Status = ItemStatus.Deleted;
                        Log.Success("Recovered: verified output renamed into place after interruption", i.FileName);
                    }
                    catch (Exception ex) { Log.Error($"Recovery rename failed: {ex.Message}", i.FileName); }
                    continue;
                }
                i.PendingRename = false;
            }

            if (i.Status.IsActive())
            {
                Log.Warn($"Previous session was interrupted during {i.Status}", i.FileName);
                i.Status = i.Search != null || i.CrfOverride != null || i.Mode == EncodeMode.Manual ? ItemStatus.Ready : ItemStatus.Waiting;
                i.StatusDetail = "Interrupted in previous session — will restart";
            }
            if (i.PartialPath != null)
            {
                foreach (var f in new[] { i.PartialPath, OutputPlanner.AbAv1TempFile(i.PartialPath) })
                    if (File.Exists(f)) partials.Add(f);
                i.PartialPath = null;
            }
        }
        _dirty = true;

        if (partials.Count > 0)
        {
            var list = string.Join("\n", partials.Take(15)) + (partials.Count > 15 ? $"\n… and {partials.Count - 15} more" : "");
            bool delete = ConfirmDialog?.Invoke("Interrupted encodes found",
                $"{partials.Count} partial output file(s) from an interrupted session were found:\n\n{list}\n\n" +
                "Source files are intact. Delete these partial files now? (The files will be re-encoded when you start the queue.)\n\n" +
                "Choose No to keep them for inspection.") ?? false;
            foreach (var f in partials)
            {
                if (!delete) { Log.Info($"Partial file kept: {f}"); continue; }
                try { File.Delete(f); Log.Info($"Deleted partial file {f}"); }
                catch (Exception ex) { Log.Warn($"Could not delete {f}: {ex.Message}"); }
            }
        }
    }

    public bool ConfirmClose()
    {
        if (!IsRunning) return true;
        return ConfirmDialog?.Invoke("Encoding in progress",
            "A job is running. Closing stops it; the partial output is discarded and the source stays untouched.\n\nClose anyway?") ?? false;
    }

    public void Shutdown()
    {
        _processor?.Stop();
        SaveNow();
        _library.Save();
        try { JsonFile.Save(AppPaths.Settings, Settings); } catch { }
        Log.Info("AV1 Studio closed");
        Log.Flush();
    }

    // =================================================================== tools

    private ToolStatus _tools = new();
    public ToolStatus Tools { get => _tools; private set { Set(ref _tools, value); Notify(nameof(ToolsSummary), nameof(ToolsReady), nameof(DependencyRows)); } }
    public string ToolsSummary => Tools.Summary;
    public bool ToolsReady => Tools.Ready;

    /// <summary>One ✓/✕ line per dependency, so nothing required is ever missing silently.</summary>
    public IReadOnlyList<DependencyRow> DependencyRows => DependencyRow.From(Tools);

    private SystemInfo _systemInfo = new();
    public SystemInfo SystemInfo { get => _systemInfo; private set => Set(ref _systemInfo, value); }

    public async Task DetectToolsAsync()
    {
        Log.Info("Detecting ab-av1 / FFmpeg / FFprobe…");
        var t = await ToolLocator.DetectAsync(Settings);
        Tools = t;
        if (t.AbAv1Path != null) Log.Info($"ab-av1 {t.AbAv1Version} — {t.AbAv1Path}");
        if (t.FfmpegPath != null) Log.Info($"FFmpeg {t.FfmpegVersion} — {t.FfmpegPath} (libsvtav1: {(t.FfmpegHasSvtAv1 ? "yes" : "NO")}, libvmaf: {(t.FfmpegHasLibVmaf ? "yes" : "NO")}, SVT-AV1 {t.SvtAv1Version ?? "?"})");
        if (t.FfprobePath != null) Log.Info($"FFprobe — {t.FfprobePath}");
        Log.Info(t.HardwareAv1Encoders.Count > 0
            ? $"Hardware AV1 encoders available: {string.Join(", ", t.HardwareAv1Encoders)}"
            : "No hardware AV1 encoder available on this machine (SVT-AV1 on the CPU will be used)");
        foreach (var n in t.Notes) Log.Warn(n);
        foreach (var p in t.Problems) Log.Error(p);
        foreach (var d in t.Av1Decoders) Log.Info($"AV1 decode: {d.Label} ✓");
        Log.Info($"Manual AV1 encoders: {(t.ManualEncoders.Count > 0 ? string.Join(", ", t.ManualEncoders) : "none")}");
        if (!t.Ready)
            Log.Warn("Required tools are missing. Open Settings › Tools to select or download them.");
        Capabilities = HardwareCapabilities.Describe(SystemInfo, t);
        NotifyToolsInfo();
        HardwareRecommendation = HardwareCapabilities.Recommendation(t);
        EnsureManualEncoderAvailable();
        if (Settings.HardwareEncoding && !t.HardwareAvailable(Settings.HardwareEncoder))
            Log.Warn($"Hardware encoding is ON but {Settings.HardwareEncoder} is not available on this machine — turn it off or nothing can be encoded.");
        NotifyHardware();
        NotifyManual();
        CommandManagerInvalidate();
    }

    // =================================================================== quick settings

    public string SourceFolder
    {
        get => Settings.SourceFolder;
        set { Settings.SourceFolder = value; OnPropertyChanged(); SaveSettings(); }
    }

    public string DestinationFolder
    {
        get => Settings.DestinationFolder;
        set { Settings.DestinationFolder = value; OnPropertyChanged(); OnPropertyChanged(nameof(DestinationDisplay)); SaveSettings(); }
    }

    public string DestinationDisplay => string.IsNullOrWhiteSpace(Settings.DestinationFolder) ? "(next to each source file)" : Settings.DestinationFolder;

    public IReadOnlyList<string> QualityPresetNames { get; } = AppSettings.QualityPresets.Select(p => p.Name).ToList();

    public string QualityPreset
    {
        get => Settings.QualityPreset;
        set
        {
            Settings.QualityPreset = value;
            var p = AppSettings.QualityPresets.FirstOrDefault(x => x.Name == value);
            if (p.Name != null && !double.IsNaN(p.Vmaf)) { Settings.TargetVmaf = p.Vmaf; OnPropertyChanged(nameof(TargetVmaf)); }
            OnPropertyChanged(); OnPropertyChanged(nameof(QualityPresetDescription));
            SaveSettings();
        }
    }

    public string QualityPresetDescription =>
        AppSettings.QualityPresets.FirstOrDefault(x => x.Name == Settings.QualityPreset).Description ?? "";

    public IReadOnlyList<double> VmafChoices { get; } = [90, 91, 92, 93, 94, 95, 96, 97, 98, 99];

    public double TargetVmaf
    {
        get => Settings.TargetVmaf;
        set
        {
            if (value is < 50 or > 100 || double.IsNaN(value)) return;
            Settings.TargetVmaf = value;
            var match = AppSettings.QualityPresets.FirstOrDefault(p => p.Vmaf == value);
            Settings.QualityPreset = match.Name ?? "Custom";
            OnPropertyChanged(); OnPropertyChanged(nameof(QualityPreset)); OnPropertyChanged(nameof(QualityPresetDescription));
            SaveSettings();
        }
    }

    public IReadOnlyList<PresetOption> PresetOptions { get; }

    public PresetOption SelectedPreset
    {
        get => PresetOptions.First(p => p.Value == Settings.Preset);
        set { Settings.Preset = value?.Value; OnPropertyChanged(); SaveSettings(); }
    }

    public static string PresetDescription(int p) => p switch
    {
        1 => "Extremely slow / maximum efficiency",
        2 => "Very slow / archival",
        3 => "Slower / very high efficiency",
        4 => "Slow / high efficiency",
        5 => "Balanced (slow side)",
        6 => "Balanced",
        7 => "Faster",
        8 => "Fast (ab-av1 default)",
        9 => "Faster / lower efficiency",
        10 => "Very fast",
        11 => "Very fast / lower quality per bit",
        12 => "Real-time",
        _ => "Fastest / lowest efficiency",
    };

    // ---------- hardware acceleration (GPU AV1 encoder) ----------

    /// <summary>A GPU AV1 encoder passed the test encode on this machine.</summary>
    public bool HardwareAvailable => Tools.HardwareAv1Encoders.Count > 0;

    public bool CanToggleHardware => !IsRunning && (HardwareAvailable || Settings.HardwareEncoding);

    public bool HardwareEncoding
    {
        get => Settings.HardwareEncoding;
        set
        {
            if (value == Settings.HardwareEncoding) return;
            if (value)
            {
                if (!HardwareAvailable)
                {
                    InfoDialog?.Invoke("Hardware encoding unavailable",
                        "No GPU AV1 encoder works on this machine.\n\nAB-AV1 supports NVIDIA NVENC AV1 (RTX 40 series or newer) and " +
                        "Intel Quick Sync AV1 (Arc, Core Ultra). Older GPUs can often DECODE AV1 but have no AV1 encoder. " +
                        "Settings › CPU & Hardware shows what was detected and why.");
                    OnPropertyChanged();
                    return;
                }
                if (!Tools.HardwareAvailable(Settings.HardwareEncoder))
                    Settings.HardwareEncoder = Tools.HardwareAv1Encoders[0];
                bool ok = ConfirmDialog?.Invoke("Turn on hardware encoding?",
                    $"Encode with {AbAv1Commands.EncoderDescription(new AppSettings { HardwareEncoding = true, HardwareEncoder = Settings.HardwareEncoder })}?\n\n" +
                    "• Much faster than SVT-AV1 on the CPU.\n" +
                    "• ab-av1 still searches the CRF (quality level) that reaches your target VMAF, so quality is kept.\n" +
                    "• But GPU encoders are less efficient: expect noticeably LARGER files than SVT-AV1 at the same VMAF.\n" +
                    "• Files already analyzed with SVT-AV1 are re-analyzed for the GPU encoder.\n\n" +
                    "You can switch back to SVT-AV1 (CPU) at any time.") ?? false;
                if (!ok) { OnPropertyChanged(); return; }
            }
            Settings.HardwareEncoding = value;
            SaveSettings();
            Log.Info($"Hardware encoding {(value ? "ON" : "OFF")} — AV1 encoder: {AbAv1Commands.EncoderDescription(Settings)}");
            NotifyHardware();
        }
    }

    public string HardwareToggleText => Settings.HardwareEncoding ? "GPU encoding: ON" : "GPU encoding: OFF";

    public string HardwareToggleTooltip =>
        !HardwareAvailable && !Settings.HardwareEncoding
            ? "No GPU AV1 encoder was found on this machine (needs NVIDIA RTX 40+ or Intel Arc / Core Ultra). SVT-AV1 on the CPU is used."
            : Settings.HardwareEncoding
                ? $"Encoding with {AbAv1Commands.EncoderDescription(Settings)}. Click to switch back to SVT-AV1 (CPU, smallest files). (Ctrl+H)"
                : $"Encoding with SVT-AV1 on the CPU (smallest files). Click to use the GPU: {string.Join(", ", Tools.HardwareAv1Encoders)} — faster, larger files. (Ctrl+H)";

    public string EncoderText => "AV1 encoder: " + AbAv1Commands.EncoderDescription(Settings);

    public IReadOnlyList<PresetOption> HardwarePresetOptions => AbAv1Commands.HardwarePresets(Settings.HardwareEncoder)
        .Select((p, i) => new PresetOption(i, p.Label)).ToList();

    public PresetOption? SelectedHardwarePreset
    {
        get
        {
            var list = AbAv1Commands.HardwarePresets(Settings.HardwareEncoder);
            int idx = Math.Max(0, list.ToList().FindIndex(p => p.Value == (Settings.HardwarePreset ?? "")));
            return HardwarePresetOptions[idx];
        }
        set
        {
            if (value?.Value is not int i) return;
            Settings.HardwarePreset = AbAv1Commands.HardwarePresets(Settings.HardwareEncoder)[i].Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPreviewCommands));
            SaveSettings();
        }
    }

    private void NotifyHardware() => Notify(nameof(HardwareEncoding), nameof(HardwareAvailable), nameof(CanToggleHardware),
        nameof(HardwareToggleText), nameof(HardwareToggleTooltip), nameof(EncoderText), nameof(HardwarePresetOptions),
        nameof(SelectedHardwarePreset), nameof(SelectedPreviewCommands));

    public bool DeleteSource
    {
        get => Settings.DeleteSourceAfterSuccess;
        set
        {
            if (value && !Settings.DeleteSourceAfterSuccess)
            {
                bool ok = ConfirmDialog?.Invoke("Delete source files?",
                    "When enabled, each source file is deleted ONLY after its AV1 output passed every verification step:\n\n" +
                    "  • ab-av1/FFmpeg exit code 0\n  • output exists and is not empty\n  • FFprobe can read it\n" +
                    "  • it contains an AV1 video stream\n  • duration matches the source\n  • expected audio/subtitle tracks present\n" +
                    (Settings.AbAv1Verify ? "  • full decode without errors\n" : "") +
                    "\nIf anything fails, the source is kept.\n\nEnable source deletion?") ?? false;
                if (!ok) { OnPropertyChanged(); return; }
            }
            Settings.DeleteSourceAfterSuccess = value;
            OnPropertyChanged();
            SaveSettings();
            Log.Info(value ? "Source deletion after verified encode: ENABLED" : "Source deletion: disabled");
        }
    }

    public int ConcurrentJobs
    {
        get => Settings.ConcurrentJobs;
        set
        {
            value = Math.Clamp(value, 1, 8);
            if (value > 1 && Settings.ConcurrentJobs == 1)
                InfoDialog?.Invoke("Multiple concurrent jobs",
                    "Running several encodes at once rarely speeds things up: SVT-AV1 already uses all CPU cores. " +
                    "It multiplies RAM usage, disk bandwidth and the temporary disk space needed, and with source deletion " +
                    "enabled, space is freed later.\n\n1 is strongly recommended.");
            Settings.ConcurrentJobs = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public IReadOnlyList<int> ConcurrencyChoices { get; } = [1, 2, 3, 4];

    private void SaveSettings()
    {
        try { JsonFile.Save(AppPaths.Settings, Settings); }
        catch (Exception ex) { Log.Warn($"Could not save settings: {ex.Message}"); }
    }

    // =================================================================== selection / filter

    private QueueItem? _selectedItem;
    public QueueItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value)) return;
            Notify(nameof(HasSelection), nameof(SelectedPreviewCommands));
            if (value != null) EncodeTarget = value; // the Encode page follows the queue selection
        }
    }

    public bool HasSelection => SelectedItem != null;
    public List<QueueItem> SelectedItems { get; set; } = new();

    private string _filterText = "";
    public string FilterText { get => _filterText; set { if (Set(ref _filterText, value)) ItemsView.Refresh(); } }

    public IReadOnlyList<string> StatusFilters { get; } =
        ["All", .. Enum.GetValues<ItemStatus>().Select(s => s.Label())];

    private string _statusFilter = "All";
    public string StatusFilter { get => _statusFilter; set { if (Set(ref _statusFilter, value)) ItemsView.Refresh(); } }

    private bool FilterItem(object o)
    {
        if (o is not QueueItem i) return false;
        if (_statusFilter != "All" && i.Status.Label() != _statusFilter) return false;
        return string.IsNullOrWhiteSpace(_filterText) || i.SourcePath.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Commands that WOULD run for the selected queue file with the current settings (mode-aware).</summary>
    public string SelectedPreviewCommands
    {
        get
        {
            var i = SelectedItem;
            if (i is null) return "";
            if (i.Mode == EncodeMode.Manual)
            {
                var saved = _encodeTarget;
                _encodeTarget = i;
                var mode = Settings.DefaultMode;
                Settings.DefaultMode = EncodeMode.Manual;
                try { return CommandPreview; }
                finally { Settings.DefaultMode = mode; _encodeTarget = saved; }
            }
            return AbAv1PreviewFor(i);
        }
    }

    private string AbAv1PreviewFor(QueueItem i)
    {
        {
            if (Tools.AbAv1Path is null) return "ab-av1 not found — see Settings › Tools.";
            try
            {
                var temp = Path.Combine(string.IsNullOrWhiteSpace(Settings.TempFolder) ? AppPaths.Temp : Settings.TempFolder, "search-…");
                var search = CommandLine.Format(Tools.AbAv1Path, AbAv1Commands.CrfSearch(Settings, Tools, i.Probe, i.SourcePath, temp));
                if (i.Probe is null) return search + "\n\n(encode command available after the file has been probed)";
                var crf = i.EffectiveCrf;
                var plan = OutputPlanner.Plan(Settings, i, crf);
                var ext = OutputPlanner.ContainerExtension(Settings, i.SourcePath);
                var streams = AbAv1Commands.PlanStreams(Settings, i.Probe, ext, i.AudioSelection, i.SubtitleSelection);
                var enc = CommandLine.Format(Tools.AbAv1Path, AbAv1Commands.Encode(Settings, Tools, i.Probe, i.SourcePath,
                    crf ?? double.NaN, plan.PartialPath, streams, null)).Replace("--crf NaN", "--crf <detected>");
                var notes = streams.Warnings.Concat(streams.Blocker is null ? [] : [streams.Blocker])
                    .Concat(plan.SkipReason is null ? [] : [plan.SkipReason]);
                return $"# CRF search\n{search}\n\n# Encode (writes {Path.GetFileName(plan.PartialPath)}, renamed to {Path.GetFileName(plan.FinalPath)} after verification)\n{enc}" +
                       (notes.Any() ? "\n\n# Notes\n" + string.Join("\n", notes.Select(n => "• " + n)) : "");
            }
            catch (Exception ex) { return "Cannot build preview: " + ex.Message; }
        }
    }

    // =================================================================== adding files

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; private set { Set(ref _isScanning, value); CommandManagerInvalidate(); } }

    private void AddFiles()
    {
        var exts = string.Join(";", FileScanner.ParseExtensions(Settings.Extensions).Select(e => "*" + e));
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Add video files",
            Filter = $"Video files|{exts}|All files|*.*",
        };
        if (dlg.ShowDialog() == true) _ = AddPathsAsync(dlg.FileNames);
    }

    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add a folder (scanned recursively)", Multiselect = true };
        if (dlg.ShowDialog() == true) _ = AddPathsAsync(dlg.FolderNames);
    }

    private void BrowseSource()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Source folder" };
        if (Directory.Exists(Settings.SourceFolder)) dlg.InitialDirectory = Settings.SourceFolder;
        if (dlg.ShowDialog() == true) SourceFolder = dlg.FolderName;
    }

    private void BrowseDestination()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Destination folder" };
        if (Directory.Exists(Settings.DestinationFolder)) dlg.InitialDirectory = Settings.DestinationFolder;
        if (dlg.ShowDialog() == true) DestinationFolder = dlg.FolderName;
    }

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var inputs = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (inputs.Count == 0) return;
        IsScanning = true;
        try
        {
            await AddPathsCoreAsync(inputs);
        }
        catch (Exception ex) { Log.Error($"Scan failed: {ex.Message}"); }
        finally { IsScanning = false; }
    }

    /// <summary>Fill in duration/resolution/codec columns. ffprobe is light; at most 2 run at a time.</summary>
    private async Task ProbeInBackgroundAsync(QueueItem item)
    {
        if (Tools.FfprobePath is null) return;
        await _probeGate.WaitAsync();
        try
        {
            if (item.Probe != null || item.IsBusy) return;
            item.Probe = await FfprobeService.ProbeAsync(Tools.FfprobePath, item.SourcePath);
            _dirty = true;
            if (ReferenceEquals(item, EncodeTarget)) Ui.Post(NotifyTarget);
        }
        catch (Exception ex)
        {
            item.StatusDetail = "ffprobe: " + ex.Message;
        }
        finally { _probeGate.Release(); }
    }

    // =================================================================== running

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) { Notify(nameof(RunStateText), nameof(CanToggleHardware)); CommandManagerInvalidate(); } }
    }

    private bool _pauseRequested;
    public bool PauseRequested { get => _pauseRequested; private set { if (Set(ref _pauseRequested, value)) Notify(nameof(RunStateText)); } }

    private string _runLabel = "";
    public string RunStateText => !IsRunning ? "Idle"
        : IsSuspended ? $"{_runLabel} — PAUSED"
        : PauseRequested ? $"{_runLabel} — pausing after current file" : _runLabel;

    private bool CanStart() => !IsRunning && !IsScanning && Items.Count > 0;

    private async Task StartAsync(RunMode mode, List<QueueItem>? subset)
    {
        bool needsAbAv1 = Items.Any(i => i.Kind == ItemKind.Video && i.Mode == EncodeMode.AbAv1 && QueueProcessor.IsEligible(i, mode));
        bool ready = Tools.FfmpegPath != null && Tools.FfprobePath != null
                     && (!needsAbAv1 || (Tools.AbAv1Path != null && Tools.FfmpegHasLibVmaf))
                     && (Tools.ManualEncoders.Count > 0 || !Items.Any(i => i.Kind == ItemKind.Video));
        if (!ready)
        {
            InfoDialog?.Invoke("Required tools are missing",
                "AV1 Studio needs FFmpeg and FFprobe" + (needsAbAv1 ? ", plus ab-av1 and an FFmpeg build with libvmaf for AB-AV1 mode" : "") + ".\n\n" +
                string.Join("\n", Tools.Problems) + "\n\nOpen Settings › Tools to download them or select their location.");
            return;
        }
        var targets = (subset ?? Items.ToList()).Where(i => QueueProcessor.IsEligible(i, mode)).ToList();
        bool anyAbAv1 = targets.Any(i => i.Kind == ItemKind.Video && i.Mode == EncodeMode.AbAv1), anyManual = targets.Any(i => i.Kind == ItemKind.Video && i.Mode == EncodeMode.Manual);
        if (targets.Any(i => i.Kind == ItemKind.Video && i.Mode == EncodeMode.AbAv1 && JobSettings(i).HardwareEncoding && !Tools.HardwareAvailable(JobSettings(i).HardwareEncoder)))
        {
            InfoDialog?.Invoke("Hardware encoder unavailable",
                $"AB-AV1 hardware encoding is ON, but {Settings.HardwareEncoder} does not work on this machine.\n\n" +
                "Turn \"GPU encoding\" OFF (Encode page, AB-AV1) to use SVT-AV1 on the CPU.");
            return;
        }
        var errors = new List<string>();
        foreach (var prof in targets.Where(i => i.Kind == ItemKind.Video).Select(JobSettings).Distinct())
        {
            if (targets.Any(i => i.Kind == ItemKind.Video && i.Mode == EncodeMode.AbAv1 && JobSettings(i) == prof))
                errors.AddRange(AbAv1Commands.Validate(prof));
            if (targets.Any(i => i.Kind == ItemKind.Video && i.Mode == EncodeMode.Manual && JobSettings(i) == prof))
                errors.AddRange(ManualCommands.Validate(prof.Manual, Tools).Select(e => "Manual AV1: " + e));
        }
        errors = errors.Distinct().ToList();
        if (errors.Count > 0) { InfoDialog?.Invoke("Invalid settings", string.Join("\n", errors)); return; }
        if (anyManual && !ConfirmManualWarnings(targets.Where(i => i.Kind == ItemKind.Video && i.Mode == EncodeMode.Manual))) return;
        if (targets.Count == 0)
        {
            InfoDialog?.Invoke("Nothing to do", mode switch
            {
                RunMode.EncodeAnalyzed => "No analyzed files are waiting to be encoded. Run \"Analyze\" first, or use \"Analyze + Encode All\".",
                _ => "No files are waiting. Failed/skipped/completed files can be re-queued with \"Retry\" (right-click).",
            });
            return;
        }

        if (mode != RunMode.AnalyzeOnly && !(targets.Any(t => t.FolderJobId != null) ? PreflightFolderSpace(targets) : PreflightSpace(targets))) return;
        CollisionPolicy? runCollision = null;
        if (mode != RunMode.AnalyzeOnly)
        {
            if (!ResolveCollisions(targets, out var collisionPolicy)) return;
            runCollision = collisionPolicy;
        }

        var snapshot = Settings.Clone();
        _processor = new QueueProcessor(snapshot, Tools, _cache)
        {
            DeletionStillAllowed = () => Settings.DeleteSourceAfterSuccess,
            ProfileFor = ProfileOf,
            Folders = () => { lock (_folders) return _folders.ToList(); },
            CollisionOverride = runCollision,
        };
        _processor.ItemChanged += it =>
        {
            _dirty = true;
            if (it.Status.IsDone() && _library.Upsert(it)) { _libraryDirty = true; }
        };
        _processor.Warning += msg => Ui.Post(() => InfoDialog?.Invoke("Insufficient disk space", msg));

        _runLabel = mode switch
        {
            RunMode.AnalyzeOnly => $"Analyzing {targets.Count} file(s)",
            RunMode.EncodeAnalyzed => $"Encoding {targets.Count} file(s)",
            _ => $"Encoding {targets.Count} file(s)",
        };
        IsRunning = true;
        PauseRequested = false;
        if (anyAbAv1)
            Log.Info($"AB-AV1: encoder {AbAv1Commands.EncoderDescription(snapshot)}, target VMAF {Fmt.Num(snapshot.TargetVmaf)}, " +
                     $"preset {(snapshot.HardwareEncoding ? (snapshot.HardwarePreset is { Length: > 0 } hp ? hp : "encoder default") : snapshot.Preset?.ToString() ?? "ab-av1 default")}");
        if (anyManual) Log.Info($"Manual AV1: {ManualCommands.Describe(snapshot.Manual)}, filters: {ManualCommands.FilterSummary(snapshot.Manual)}");
        Log.Info($"{_runLabel} — {snapshot.ConcurrentJobs} concurrent, delete source: {(snapshot.DeleteSourceAfterSuccess ? "YES (after verification)" : "no")}");
        var sw = Stopwatch.StartNew();
        try
        {
            if (subset != null) await _processor.RunAsync(targets, mode);
            else await _processor.RunAsync(() => Ui.InvokeAsync<IReadOnlyList<QueueItem>>(() => Items.ToList()), mode);
        }
        catch (Exception ex) { Log.Error($"Queue error: {ex.Message}"); }
        finally
        {
            IsRunning = false;
            PauseRequested = false;
            IsSuspended = false;
            _processor = null;
            SaveNow();
            RefreshStorage();
            RefreshLibrary();
            Log.Info($"Queue finished in {Fmt.Duration(sw.Elapsed)}. " + StatusCountsText());
        }
    }

    /// <summary>Warn before starting if the first file clearly can't fit.</summary>
    private bool PreflightSpace(List<QueueItem> targets)
    {
        var first = targets[0];
        var dir = OutputPlanner.OutputDirectory(Settings, first);
        var disk = Win32.DiskSpace(dir);
        if (disk is null) return true;
        long est = EstimateOutput(first);
        long need = (long)(est * Settings.SpaceSafetyFactor) + (long)(Settings.MinFreeSpaceGB * 1024 * 1024 * 1024);
        if ((long)disk.Value.Free >= need) return true;
        return ConfirmDialog?.Invoke("Low disk space",
            $"The destination has {Fmt.Bytes((long)disk.Value.Free)} free, but the next file ({first.FileName}) is estimated to need " +
            $"{Fmt.Bytes(need)} including the safety margin.\n\nThe queue will pause when a file does not fit. Start anyway?") ?? false;
    }

    private void TogglePause()
    {
        if (_processor is null) return;
        PauseRequested = !PauseRequested;
        _processor.PauseAfterCurrent = PauseRequested;
        Log.Info(PauseRequested ? "Will pause after the current file" : "Pause cancelled");
    }

    private void Stop()
    {
        if (_processor is null) return;
        if (ConfirmDialog?.Invoke("Stop now?", "Stop all running jobs immediately? Partial outputs are deleted; source files are never touched.") != true) return;
        Log.Warn("Stopping — killing running ab-av1/FFmpeg processes");
        _processor.Stop();
        foreach (var i in Items.Where(i => i.IsPaused)) i.IsPaused = false;
        IsSuspended = false;
    }

    // =================================================================== item actions

    private void RemoveSelected()
    {
        var sel = SelectedItems.Where(i => !i.IsBusy).ToList();
        foreach (var i in sel) Items.Remove(i);
        _dirty = true;
        RefreshStorage();
    }

    private void RetrySelected()
    {
        foreach (var i in SelectedItems.Where(i => !i.IsBusy && i.Status is ItemStatus.Failed or ItemStatus.Skipped or ItemStatus.Completed or ItemStatus.Cancelled))
        {
            if (i.Status == ItemStatus.Completed && File.Exists(i.OutputPath ?? "")) continue; // don't redo finished work silently
            if (!File.Exists(i.SourcePath)) continue;
            i.Status = i.Search != null ? ItemStatus.Ready : ItemStatus.Waiting;
            i.StatusDetail = null;
            i.ErrorMessage = null;
            i.ErrorWhy = null;
            i.ErrorFix = null;
            i.ResetLiveStats();
        }
        _dirty = true;
    }

    private void ReanalyzeSelected()
    {
        foreach (var i in SelectedItems.Where(i => !i.IsBusy && !i.Status.IsFinal() && i.Mode == EncodeMode.AbAv1))
        {
            i.InvalidateAnalysis();
            _cache.Remove(i.SourcePath);
            i.Status = ItemStatus.Waiting;
            i.StatusDetail = "Will re-run CRF search";
        }
        _dirty = true;
    }

    private void ClearFinished()
    {
        foreach (var i in Items.Where(i => i.Status.IsFinal()).ToList()) Items.Remove(i);
        _dirty = true;
        RefreshStorage();
    }

    private void ChooseTracks()
    {
        if (SelectedItem is null) return;
        if (TracksDialog?.Invoke(SelectedItem) == true) { _dirty = true; OnPropertyChanged(nameof(SelectedPreviewCommands)); }
    }

    private void OpenSettings()
    {
        var edited = SettingsDialog?.Invoke(Settings.Clone(), Tools);
        if (edited is null)
        {
            // Tools may have been downloaded even if the dialog was cancelled.
            if (!Tools.Ready) _ = DetectToolsAsync();
            return;
        }
        ApplySettings(edited);
    }

    private void CleanupPartials()
    {
        var files = new List<string>();
        foreach (var dir in Items.Select(i => OutputPlanner.OutputDirectory(Settings, i)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                files.AddRange(Directory.EnumerateFiles(dir).Where(OutputPlanner.IsOurTemporaryFile));
            }
            catch { }
        }
        if (files.Count == 0) { InfoDialog?.Invoke("Clean up", "No partial or temporary output files found."); return; }
        if (ConfirmDialog?.Invoke("Delete partial files?", $"Delete {files.Count} partial/temporary output file(s)?\n\n" +
                string.Join("\n", files.Take(20)) + (files.Count > 20 ? "\n…" : "")) != true) return;
        foreach (var f in files)
        {
            try { File.Delete(f); Log.Info($"Deleted {f}"); } catch (Exception ex) { Log.Warn($"{f}: {ex.Message}"); }
        }
    }

    private static void Reveal(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", ["/select,", path]);
            else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start("explorer.exe", [Path.GetDirectoryName(path)!]);
        }
        catch (Exception ex) { Log.Warn($"Cannot open Explorer: {ex.Message}"); }
    }

    // =================================================================== storage summary

    private string _libraryText = "", _estimateText = "", _savingsText = "", _freeText = "", _nextText = "", _afterText = "", _countsText = "";
    public string LibraryText { get => _libraryText; private set => Set(ref _libraryText, value); }
    public string EstimateText { get => _estimateText; private set => Set(ref _estimateText, value); }
    public string SavingsText { get => _savingsText; private set => Set(ref _savingsText, value); }
    public string FreeSpaceText { get => _freeText; private set => Set(ref _freeText, value); }
    public string NextFileSpaceText { get => _nextText; private set => Set(ref _nextText, value); }
    public string AfterCurrentText { get => _afterText; private set => Set(ref _afterText, value); }
    public string CountsText { get => _countsText; private set => Set(ref _countsText, value); }

    private void RefreshStorage()
    {
        var active = Items.Where(i => i.Status is not (ItemStatus.Skipped or ItemStatus.Failed)).ToList();
        long original = active.Sum(i => i.SourceSize);
        var analyzed = active.Where(i => i.ActualOutputSize != null || i.Search?.PredictedSize != null).ToList();
        long analyzedSrc = analyzed.Sum(i => i.SourceSize);
        long analyzedOut = analyzed.Sum(i => i.ActualOutputSize ?? i.Search!.PredictedSize!.Value);
        double ratio = analyzedSrc > 0 ? (double)analyzedOut / analyzedSrc : double.NaN;
        long estimated = double.IsNaN(ratio) ? 0 : analyzedOut + (long)((original - analyzedSrc) * ratio);

        LibraryText = $"{Fmt.Bytes(original)} in {active.Count} file(s)";
        EstimateText = double.IsNaN(ratio) ? "— (analyze files to estimate)"
            : $"{Fmt.Bytes(estimated)}" + (analyzed.Count < active.Count ? $"  (extrapolated from {analyzed.Count}/{active.Count})" : "");
        SavingsText = double.IsNaN(ratio) || original == 0 ? "—"
            : $"{Fmt.Bytes(original - estimated)}  ({100.0 * (original - estimated) / original:0.0}%)";

        var current = Items.FirstOrDefault(i => i.Status is ItemStatus.Encoding or ItemStatus.Verifying);
        var next = current ?? Items.FirstOrDefault(i => i.Status is ItemStatus.Ready or ItemStatus.CrfFound or ItemStatus.Waiting);
        string? dir = next != null ? OutputPlanner.OutputDirectory(Settings, next)
            : !string.IsNullOrWhiteSpace(Settings.DestinationFolder) ? Settings.DestinationFolder : Settings.SourceFolder;
        var disk = string.IsNullOrWhiteSpace(dir) ? null : Win32.DiskSpace(dir);
        FreeSpaceText = disk is { } d ? $"{Fmt.Bytes((long)d.Free)} of {Fmt.Bytes((long)d.Total)} ({Path.GetPathRoot(dir)})" : "—";

        if (next != null)
        {
            long est = EstimateOutput(next);
            long need = (long)(est * Settings.SpaceSafetyFactor);
            NextFileSpaceText = $"{Fmt.Bytes(need)} for {next.FileName}" + (next.Search?.PredictedSize is null && !(next.Mode == EncodeMode.Manual && _previewEstimate.ContainsKey(next.Id)) ? " (no estimate yet: source size used)" : "");
            if (disk is { } dd)
            {
                long afterEncode = (long)dd.Free - (current?.CurrentOutputSize is long c ? est - c : est);
                bool sameVolume = string.Equals(Path.GetPathRoot(dir), Path.GetPathRoot(next.SourcePath), StringComparison.OrdinalIgnoreCase);
                long afterDelete = afterEncode + (Settings.DeleteSourceAfterSuccess && Settings.DeleteMode == DeleteMode.Permanent && sameVolume ? next.SourceSize : 0);
                AfterCurrentText = Settings.DeleteSourceAfterSuccess && sameVolume
                    ? $"{Fmt.Bytes(afterDelete)} (after source deletion)"
                    : $"{Fmt.Bytes(afterEncode)}";
            }
            else AfterCurrentText = "—";
        }
        else { NextFileSpaceText = "—"; AfterCurrentText = "—"; }

        CountsText = StatusCountsText();
        NotifyTargetStorage();
        RefreshFolderProgress();
        if (_libraryDirty) { _libraryDirty = false; Task.Run(_library.Save); RefreshLibrary(); }
    }

    private bool _libraryDirty;

    /// <summary>Best available output-size estimate: ab-av1 prediction, a Manual preview extrapolation, else the source size.</summary>
    private long EstimateOutput(QueueItem i) =>
        i.Search?.PredictedSize
        ?? (i.Mode == EncodeMode.Manual && _previewEstimate.TryGetValue(i.Id, out var e) ? e : i.SourceSize);

    private string StatusCountsText()
    {
        var groups = Items.GroupBy(i => i.Status).OrderBy(g => g.Key).Select(g => $"{g.Key.Label()}: {g.Count()}");
        return string.Join(" · ", groups);
    }

    // =================================================================== persistence

    private void SaveIfDirty()
    {
        if (_dirty) SaveNow();
        if (_settingsDirty) { _settingsDirty = false; SaveSettings(); }
    }

    private void SaveNow()
    {
        _dirty = false;
        try
        {
            // Serialize on the UI thread (consistent snapshot), write on a worker thread.
            var items = Items.ToList();
            var used = items.Where(i => i.ProfileId != null).Select(i => i.ProfileId!).ToHashSet();
            Dictionary<string, AppSettings> profiles;
            lock (_profiles) profiles = _profiles.Where(kv => used.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            List<FolderJob> folders;
            lock (_folders) folders = _folders.ToList();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new QueueState { Items = items, Profiles = profiles, Folders = folders }, JsonFile.Options);
            var write = Task.Run(() =>
            {
                try { JsonFile.SaveBytes(AppPaths.State, bytes); }
                catch (Exception ex) { Log.Warn($"Could not save queue state: {ex.Message}"); }
            });
            if (!IsRunning) write.Wait(3000); // on shutdown / after a run, make sure it hit the disk
        }
        catch (Exception ex) { Log.Warn($"Could not save queue state: {ex.Message}"); }
    }

    // =================================================================== log

    private void OnLogEntry(LogEntry e)
    {
        if (e.Level == LogLevel.Tool && !Settings.ShowToolOutput) return; // always in the log file anyway
        Ui.Enqueue(() =>
        {
            LogEntries.Add(e);
            if (LogEntries.Count > 8000) LogEntries.RemoveAt(0);
            if (e.Level != LogLevel.Tool) LastLogLine = e.Display;
        });
    }

    private string _lastLogLine = "";
    public string LastLogLine { get => _lastLogLine; private set => Set(ref _lastLogLine, value); }

    private static void CommandManagerInvalidate() =>
        Ui.Post(System.Windows.Input.CommandManager.InvalidateRequerySuggested);

    // =================================================================== commands

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand ScanSourceCommand { get; }
    public RelayCommand BrowseSourceCommand { get; }
    public RelayCommand BrowseDestinationCommand { get; }
    public RelayCommand ClearDestinationCommand { get; }
    public RelayCommand AnalyzeAllCommand { get; }
    public RelayCommand EncodeAnalyzedCommand { get; }
    public RelayCommand AnalyzeAndEncodeCommand { get; }
    public RelayCommand AnalyzeSelectedCommand { get; }
    public RelayCommand EncodeSelectedCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand RetrySelectedCommand { get; }
    public RelayCommand ReanalyzeSelectedCommand { get; }
    public RelayCommand ClearFinishedCommand { get; }
    public RelayCommand ChooseTracksCommand { get; }
    public RelayCommand OpenSourceFolderCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand RedetectToolsCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand CopyTextCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CleanupPartialsCommand { get; }
    public RelayCommand ToggleHardwareCommand { get; }
}
