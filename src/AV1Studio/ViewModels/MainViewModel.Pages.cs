using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using AV1Studio.Models;
using AV1Studio.Mvvm;
using AV1Studio.Services;
using AV1Studio.Util;
using AV1Studio.Views;

namespace AV1Studio.ViewModels;

/// <summary>Library, Logs and Settings pages.</summary>
public sealed partial class MainViewModel
{
    // ================================================================= LIBRARY

    public ObservableCollection<LibraryEntry> LibraryEntries { get; } = new();
    public ICollectionView LibraryView { get; }

    private bool _fAv1, _fNotEncoded, _fCompleted, _fFailed, _fHdr, _f1080, _f1440, _f4k;
    public bool FilterAv1 { get => _fAv1; set { Set(ref _fAv1, value); LibraryView.Refresh(); } }
    public bool FilterNotEncoded { get => _fNotEncoded; set { Set(ref _fNotEncoded, value); LibraryView.Refresh(); } }
    public bool FilterCompleted { get => _fCompleted; set { Set(ref _fCompleted, value); LibraryView.Refresh(); } }
    public bool FilterFailed { get => _fFailed; set { Set(ref _fFailed, value); LibraryView.Refresh(); } }
    public bool FilterHdr { get => _fHdr; set { Set(ref _fHdr, value); LibraryView.Refresh(); } }
    public bool Filter1080 { get => _f1080; set { Set(ref _f1080, value); LibraryView.Refresh(); } }
    public bool Filter1440 { get => _f1440; set { Set(ref _f1440, value); LibraryView.Refresh(); } }
    public bool Filter4K { get => _f4k; set { Set(ref _f4k, value); LibraryView.Refresh(); } }

    private string _librarySearch = "";
    public string LibrarySearch { get => _librarySearch; set { if (Set(ref _librarySearch, value)) LibraryView.Refresh(); } }

    private string _libOriginal = "—", _libEncoded = "—", _libSaved = "—", _libCount = "";
    public string LibraryOriginalText { get => _libOriginal; private set => Set(ref _libOriginal, value); }
    public string LibraryEncodedText { get => _libEncoded; private set => Set(ref _libEncoded, value); }
    public string LibrarySavedText { get => _libSaved; private set => Set(ref _libSaved, value); }
    public string LibraryCountText { get => _libCount; private set => Set(ref _libCount, value); }

    public RelayCommand ClearLibraryFiltersCommand { get; private set; } = null!;

    private void InitLibraryCommands()
    {
        ClearLibraryFiltersCommand = new RelayCommand(() =>
        {
            _fAv1 = _fNotEncoded = _fCompleted = _fFailed = _fHdr = _f1080 = _f1440 = _f4k = false;
            _librarySearch = "";
            Notify(nameof(FilterAv1), nameof(FilterNotEncoded), nameof(FilterCompleted), nameof(FilterFailed), nameof(FilterHdr),
                nameof(Filter1080), nameof(Filter1440), nameof(Filter4K), nameof(LibrarySearch));
            LibraryView.Refresh();
        });
    }

    private void RefreshLibrary()
    {
        var entries = new List<LibraryEntry>();
        var inQueue = new HashSet<Guid>(Items.Select(i => i.Id));
        foreach (var i in Items)
        {
            var v = i.Probe?.MainVideo;
            bool encoded = i.Status.IsDone();
            entries.Add(new LibraryEntry
            {
                FileName = i.FileName, Path = i.SourcePath, OriginalSize = i.SourceSize,
                EncodedSize = encoded ? i.ActualOutputSize : null,
                Codec = encoded ? $"{v?.Codec ?? "?"} → AV1" : v?.Codec ?? "",
                Height = v?.Height, Resolution = i.Resolution, DurationSeconds = i.Probe?.DurationSeconds,
                Hdr = i.Probe?.HdrFormat ?? "", Status = i.StatusText, Mode = i.ModeText, Encoder = i.EncoderText ?? "",
                IsEncoded = encoded, IsFailed = i.Status == ItemStatus.Failed,
                IsAv1 = encoded || string.Equals(v?.Codec, "av1", StringComparison.OrdinalIgnoreCase),
            });
        }
        foreach (var r in _library.Records.Where(r => !inQueue.Contains(r.Id)))
        {
            entries.Add(new LibraryEntry
            {
                FileName = Path.GetFileName(r.OutputPath ?? r.SourcePath), Path = r.OutputPath ?? r.SourcePath,
                OriginalSize = r.OriginalSize, EncodedSize = r.EncodedSize,
                Codec = $"{r.SourceCodec ?? "?"} → AV1", Height = r.Height,
                Resolution = r.Width is int w && r.Height is int h ? $"{w}×{h}" : "", DurationSeconds = r.DurationSeconds,
                Hdr = r.Hdr ?? "", Status = r.Status.Label() + (r.SourceDeleted ? " (source deleted)" : ""),
                Mode = r.Mode == EncodeMode.AbAv1 ? "AB-AV1" : "Manual", Encoder = r.Encoder ?? "",
                IsEncoded = true, IsAv1 = true,
            });
        }
        LibraryEntries.Clear();
        foreach (var e in entries) LibraryEntries.Add(e);

        var done = entries.Where(e => e.IsEncoded && e.EncodedSize != null).ToList();
        long orig = done.Sum(e => e.OriginalSize), enc = done.Sum(e => e.EncodedSize!.Value);
        LibraryOriginalText = Fmt.Bytes(orig);
        LibraryEncodedText = Fmt.Bytes(enc);
        LibrarySavedText = orig > 0 ? $"{Fmt.Bytes(orig - enc)} ({100.0 * (orig - enc) / orig:0.0}%)" : "—";
        LibraryCountText = $"{entries.Count} file(s) · {done.Count} encoded to AV1 · {entries.Count(e => !e.IsEncoded)} not encoded";
    }

    private bool FilterLibrary(object o)
    {
        if (o is not LibraryEntry e) return false;
        if (_librarySearch.Length > 0 && !e.Path.Contains(_librarySearch, StringComparison.OrdinalIgnoreCase)) return false;
        bool anyStatus = _fAv1 || _fNotEncoded || _fCompleted || _fFailed;
        if (anyStatus && !((_fAv1 && e.IsAv1) || (_fNotEncoded && !e.IsEncoded && !e.IsAv1) || (_fCompleted && e.IsEncoded) || (_fFailed && e.IsFailed)))
            return false;
        bool anyRes = _f1080 || _f1440 || _f4k;
        if (anyRes && !((_f1080 && e.ResolutionClass == "1080p") || (_f1440 && e.ResolutionClass == "1440p") || (_f4k && e.ResolutionClass == "4K")))
            return false;
        if (_fHdr && !e.IsHdr) return false;
        return true;
    }

    // ================================================================= LOGS

    public ICollectionView LogView { get; }

    public IReadOnlyList<string> LogLevelFilters { get; } = ["All", "Errors", "Warnings & errors", "Commands", "Tool output"];
    private string _logLevelFilter = "All";
    public string LogLevelFilter { get => _logLevelFilter; set { if (Set(ref _logLevelFilter, value)) LogView.Refresh(); } }

    private string _logSearch = "";
    public string LogSearch { get => _logSearch; set { if (Set(ref _logSearch, value)) LogView.Refresh(); } }

    public bool ShowToolOutput
    {
        get => Settings.ShowToolOutput;
        set
        {
            Settings.ShowToolOutput = value;
            SaveSettings();
            OnPropertyChanged();
            Log.Info(value ? "Showing raw ab-av1 / FFmpeg output (new lines)" : "Hiding raw tool output (still written to the log file)");
        }
    }

    public RelayCommand CopyLogCommand { get; private set; } = null!;
    public RelayCommand ExportLogCommand { get; private set; } = null!;

    private void InitLogCommands()
    {
        CopyLogCommand = new RelayCommand(CopyLogAsync);
        ExportLogCommand = new RelayCommand(SaveLog);
    }

    /// <summary>Copy Logs: the visible entries, with the user name and profile folder replaced by placeholders.</summary>
    private async void CopyLogAsync()
    {
        try
        {
            var text = VisibleLogText();
            if (text.Length == 0) return;
            if (await ClipboardHelper.TrySetTextAsync(Diagnostics.Redact(text)))
            {
                Log.Info("Log copied to the clipboard");
                return;
            }
            if (ConfirmDialog?.Invoke("Copy failed",
                    "The clipboard is currently in use by another application.\n\nSave the log to a file instead?") == true)
                SaveLog();
        }
        catch (Exception ex) { Log.Error($"Could not copy the log: {ex}"); }
    }

    /// <summary>Save Logs: today's complete log file (or the visible entries), ready to attach to a bug report.</summary>
    private void SaveLog()
    {
        var path = SaveFileDialog?.Invoke("Save log", $"av1-studio-log_{DateTime.Now:yyyyMMdd_HHmm}.txt");
        if (path is null) return;
        try
        {
            Log.Flush();
            string text;
            if (File.Exists(Log.CurrentFile))
            {
                using var fs = new FileStream(Log.CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd();
            }
            else text = VisibleLogText();
            var header = $"{AppInfo.NameAndVersion} log — user name and profile folder replaced by placeholders{Environment.NewLine}{Environment.NewLine}";
            File.WriteAllText(path, header + Diagnostics.Redact(text), Encoding.UTF8);
            Log.Info($"Log saved to {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save the log: {ex.Message}");
            InfoDialog?.Invoke("Save failed", $"The log could not be saved: {ex.Message}");
        }
    }

    private string VisibleLogText() =>
        string.Join(Environment.NewLine, LogView.Cast<LogEntry>().Select(e => $"{e.Time:HH:mm:ss} {e.Level,-7} {e.Display}"));

    private bool FilterLog(object o)
    {
        if (o is not LogEntry e) return false;
        bool levelOk = _logLevelFilter switch
        {
            "Errors" => e.Level == LogLevel.Error,
            "Warnings & errors" => e.Level is LogLevel.Error or LogLevel.Warning,
            "Commands" => e.Level == LogLevel.Command,
            "Tool output" => e.Level == LogLevel.Tool,
            _ => true,
        };
        return levelOk && (_logSearch.Length == 0 || e.Display.Contains(_logSearch, StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================= SETTINGS / HARDWARE

    private List<DeviceCapability> _capabilities = new();
    public List<DeviceCapability> Capabilities { get => _capabilities; private set => Set(ref _capabilities, value); }

    private string _hwRecommendation = "";
    public string HardwareRecommendation { get => _hwRecommendation; private set => Set(ref _hwRecommendation, value); }

    public string DecoderSummary => Tools.Av1Decoders.Count == 0 ? "No AV1 decoder test result yet"
        : string.Join(" · ", Tools.Av1Decoders.Select(d => d.Label + (d.Hardware ? " (hardware)" : "")));

    public string HardwareEncoderSummary
    {
        get
        {
            var hw = Tools.EncoderAvailability.Where(e => e.IsHardware && e.Available).Select(e => e.Name).ToList();
            return hw.Count > 0 ? string.Join(", ", hw) + " (detected automatically)" : "None detected — software encoding is used";
        }
    }

    public string EncoderSummary => Tools.ManualEncoders.Count == 0 ? "No AV1 encoder available"
        : string.Join(" · ", Tools.ManualEncoders.Select(e => ManualCommands.Spec(e).Name));

    public string PixelFormatSummary => string.Join(Environment.NewLine,
        Tools.EncoderPixelFormats.Select(kv => $"{kv.Key}: {kv.Value}"));

    /// <summary>Apply settings edited on the Settings page (working copy). Manual AV1 settings live on the Encode page.</summary>
    public void ApplySettings(AppSettings edited)
    {
        bool toolsChanged = edited.AbAv1Path != Settings.AbAv1Path || edited.FfmpegPath != Settings.FfmpegPath || edited.FfprobePath != Settings.FfprobePath;
        edited.Manual = Settings.Manual;           // edited on the Encode page, never replaced here
        edited.DefaultMode = Settings.DefaultMode;
        edited.ManualLevel = Settings.ManualLevel;
        Settings = edited;
        SaveSettings();
        Notify(nameof(SourceFolder), nameof(DestinationFolder), nameof(DestinationDisplay), nameof(QualityPreset),
            nameof(QualityPresetDescription), nameof(TargetVmaf), nameof(SelectedPreset), nameof(DeleteSource), nameof(ConcurrentJobs),
            nameof(SelectedPreviewCommands), nameof(AbAv1AudioSummary), nameof(AbAv1SubtitleSummary), nameof(AbAv1AnalysisSummary),
            nameof(OutputSummary), nameof(ShowToolOutput), nameof(SelectedProfile), nameof(SelectedProfileDescription),
            nameof(CpuSummary), nameof(OutputContainer), nameof(SamplesChoice));
        NotifyHardware();
        NotifyManual();
        NotifyTarget();
        Log.Info("Settings saved");
        if (toolsChanged || !Tools.Ready) _ = DetectToolsAsync();
    }


    public string CpuSummary =>
        $"CRF search: {ResourcePlanner.Plan(Settings.SearchCpu).Description}{Environment.NewLine}Encoding: {ResourcePlanner.Plan(Settings.EncodeCpu).Description}";

    /// <summary>
    /// Forgets every CRF search result: AV1 Studio's own result cache, ab-av1's sample-encode cache
    /// (%LOCALAPPDATA%\ab-av1) and the CRF already found for queued files, so the next run searches again.
    /// Returns a short summary for the user.
    /// </summary>
    public string ClearAnalysisCache()
    {
        int results = _cache.Entries.Count;
        _cache.Entries.Clear();
        try { _cache.Save(); } catch (Exception ex) { Log.Warn($"Could not save the analysis cache: {ex.Message}"); }

        bool abOk = AbAv1Cache.Clear(out long freed, out string? error);

        int requeued = 0;
        foreach (var i in Items.Where(i => i.Kind == ItemKind.Video && i.Mode == EncodeMode.AbAv1 && !i.IsBusy && !i.Status.IsFinal()
                                           && (i.Search != null || i.Status == ItemStatus.CrfFound || i.Status == ItemStatus.Ready)))
        {
            i.InvalidateAnalysis();
            i.Status = ItemStatus.Waiting;
            i.StatusDetail = "Will run a new CRF search";
            requeued++;
        }
        _dirty = true;
        RefreshPlanned();
        RefreshStorage();

        var summary = $"Cleared {results} saved CRF result(s)" +
                      (abOk ? $" and ab-av1's sample cache ({Fmt.Bytes(freed)})" : "") +
                      (requeued > 0 ? $"; {requeued} queued file(s) will be analysed again." : ".");
        Log.Info(summary);
        return abOk ? summary : summary + "\n\n" + error;
    }

    public void NotifyToolsInfo() => Notify(nameof(DecoderSummary), nameof(EncoderSummary), nameof(PixelFormatSummary), nameof(HardwareEncoderSummary), nameof(OutputContainerChoices));
}
