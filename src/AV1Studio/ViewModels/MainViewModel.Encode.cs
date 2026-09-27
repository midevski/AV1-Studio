using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AV1Studio.Models;
using AV1Studio.Mvvm;
using AV1Studio.Native;
using AV1Studio.Services;
using AV1Studio.Util;

namespace AV1Studio.ViewModels;

public sealed record ChoiceOption<T>(T Value, string Label);

public sealed partial class MainViewModel
{
    // ============================================================= mode

    public EncodeMode SelectedMode
    {
        get => Settings.DefaultMode;
        set
        {
            if (Settings.DefaultMode == value) return;
            Settings.DefaultMode = value;
            SaveSettings();
            Notify(nameof(SelectedMode), nameof(IsManualMode), nameof(IsAbAv1Mode), nameof(CommandPreview), nameof(CommandPreviewTitle),
                nameof(ModeHint), nameof(SelectedProfile), nameof(SelectedProfileDescription));
            Log.Info($"Mode for new files: {(value == EncodeMode.AbAv1 ? "AB-AV1 (automatic quality)" : "Manual AV1 (full control)")}");
            NotifyTargetStorage();
        }
    }

    public bool IsManualMode { get => SelectedMode == EncodeMode.Manual; set { if (value) SelectedMode = EncodeMode.Manual; } }
    public bool IsAbAv1Mode { get => SelectedMode == EncodeMode.AbAv1; set { if (value) SelectedMode = EncodeMode.AbAv1; } }

    public string ModeHint => IsAbAv1Mode
        ? "Files you add now will be encoded with AB-AV1: target quality (VMAF) → automatic CRF → encode."
        : "Files you add now will be encoded with Manual AV1: your encoder, quality and settings, directly through FFmpeg.";

    // ============================================================= Manual AV1 settings

    public ManualSettings Manual => Settings.Manual;
    private ManualSettings? _attachedManual;

    private void AttachManual()
    {
        if (_attachedManual != null) _attachedManual.PropertyChanged -= OnManualChanged;
        _attachedManual = Settings.Manual;
        _attachedManual.PropertyChanged += OnManualChanged;
        OnPropertyChanged(nameof(Manual));
    }

    private bool _settingsDirty;

    private void OnManualChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManualSettings.Encoder)) ApplyEncoderDefaults();
        _settingsDirty = true; // saved by the 2 s timer
        NotifyManual();
    }

    private void ApplyEncoderDefaults()
    {
        var spec = ManualCommands.Spec(Manual.Encoder);
        if (!spec.Presets.Any(p => p.Value == Manual.Preset)) Manual.Preset = spec.DefaultPreset;
        if (Manual.Quality < spec.QualityMin || Manual.Quality > spec.QualityMax) Manual.Quality = spec.DefaultQuality;
    }

    /// <summary>Only offer encoders that actually work here; fall back to SVT-AV1.</summary>
    private void EnsureManualEncoderAvailable()
    {
        if (Tools.ManualEncoders.Count == 0 || Tools.ManualEncoders.Contains(Manual.Encoder)) return;
        Log.Warn($"Manual AV1 encoder {Manual.Encoder} is not available on this PC — switching to {Tools.ManualEncoders[0]}");
        Manual.Encoder = Tools.ManualEncoders[0];
    }

    private void NotifyManual()
    {
        Notify(nameof(ManualEncoderChoices), nameof(QualityName), nameof(QualityMin), nameof(QualityMax), nameof(QualityHelp),
            nameof(QualityRangeText), nameof(PresetChoices), nameof(IsSvtEncoder), nameof(IsHardwareManualEncoder), nameof(FilterSummary),
            nameof(ManualWarningsText), nameof(HasManualWarnings), nameof(CommandPreview), nameof(ManualSummary), nameof(EncoderKindText),
            nameof(SelectedProfile), nameof(SelectedProfileDescription), nameof(UnavailableEncodersText), nameof(ShowHardwareControls), nameof(IsNvenc));
        RefreshPlanned();
    }

    public IReadOnlyList<ChoiceOption<string>> ManualEncoderChoices =>
        Tools.ManualEncoders.Select(id => new ChoiceOption<string>(id, ManualCommands.Spec(id).Name)).ToList();

    private EncoderSpec ManualSpec => ManualCommands.Spec(Manual.Encoder);
    public string QualityName => ManualSpec.QualityName;
    public double QualityMin => ManualSpec.QualityMin;
    public double QualityMax => ManualSpec.QualityMax;
    public string QualityHelp => ManualSpec.QualityHelp;
    public string QualityRangeText => $"{ManualSpec.QualityName} {ManualSpec.QualityMin:0}–{ManualSpec.QualityMax:0} · lower = higher quality & larger file";
    public IReadOnlyList<ChoiceOption<string>> PresetChoices => ManualSpec.Presets.Select(p => new ChoiceOption<string>(p.Value, p.Label)).ToList();
    public bool IsSvtEncoder => Manual.Encoder == "libsvtav1";
    public bool ShowHardwareControls => Manual.Encoder is "av1_nvenc" or "av1_qsv";
    public bool IsNvenc => Manual.Encoder == "av1_nvenc";

    /// <summary>Encoders that are not usable here, each with the reason (so nothing is hidden without explanation).</summary>
    public string UnavailableEncodersText => string.Join(Environment.NewLine,
        Tools.EncoderAvailability.Where(e => !e.Available).Select(e => $"✕ {e.Name} — unavailable: {e.Reason}"));
    public bool IsHardwareManualEncoder => ManualSpec.IsHardware;
    public string EncoderKindText => ManualSpec.IsHardware
        ? "Hardware encoder: runs on the GPU's dedicated media engine — much faster and light on the CPU; controls and efficiency depend on the GPU generation."
        : "Software encoder: runs on the CPU — slower and CPU-intensive, with extensive controls and typically strong compression efficiency.";
    public string FilterSummary => ManualCommands.FilterSummary(Manual);
    public string ManualSummary => ManualCommands.Describe(Manual);

    public SettingsLevel ManualLevel
    {
        get => Settings.ManualLevel;
        set { Settings.ManualLevel = value; SaveSettings(); Notify(nameof(ManualLevel), nameof(ShowAdvanced), nameof(ShowExpert)); }
    }
    public bool ShowAdvanced => ManualLevel >= SettingsLevel.Advanced;
    public bool ShowExpert => ManualLevel == SettingsLevel.Expert;

    public IReadOnlyList<ChoiceOption<ResolutionMode>> ResolutionChoices { get; } =
    [
        new(ResolutionMode.Source, "Source (keep)"), new(ResolutionMode.P1080, "1080p (downscale only)"),
        new(ResolutionMode.P1440, "1440p (downscale only)"), new(ResolutionMode.P2160, "4K (downscale only)"), new(ResolutionMode.Custom, "Custom…"),
    ];

    public IReadOnlyList<ChoiceOption<Deinterlace>> DeinterlaceChoices { get; } =
        [new(Deinterlace.Off, "Off"), new(Deinterlace.Auto, "Only interlaced frames"), new(Deinterlace.Always, "All frames")];

    public IReadOnlyList<ChoiceOption<Denoise>> DenoiseChoices { get; } =
        [new(Denoise.Off, "Off"), new(Denoise.Light, "Light"), new(Denoise.Medium, "Medium"), new(Denoise.Strong, "Strong")];

    public IReadOnlyList<ChoiceOption<int?>> TuneChoices { get; } =
        [new(null, "Encoder default"), new(0, "Visual quality (VQ)"), new(1, "PSNR")];

    public IReadOnlyList<ChoiceOption<ContainerFormat>> ContainerChoices { get; } =
        [new(ContainerFormat.Mkv, "MKV (recommended)"), new(ContainerFormat.Mp4, "MP4"), new(ContainerFormat.SameAsSource, "Same as source (MKV/MP4)")];

    public IReadOnlyList<ChoiceOption<string>> AudioCodecChoices { get; } =
    [
        new("libopus", "Opus"), new("aac", "AAC"), new("ac3", "AC-3 (Dolby Digital)"), new("eac3", "E-AC-3 (Dolby Digital Plus)"),
        new("flac", "FLAC (lossless)"), new("pcm_s16le", "PCM 16-bit (MKV only)"), new("pcm_s24le", "PCM 24-bit (MKV only)"),
    ];

    public IReadOnlyList<ChoiceOption<string>> PrimariesChoices { get; } =
        [new("", "Keep source"), new("bt709", "BT.709 (HD SDR)"), new("bt2020", "BT.2020 (UHD / HDR)"), new("smpte432", "Display P3"), new("bt470bg", "BT.470BG (PAL)"), new("smpte170m", "SMPTE 170M (NTSC)")];
    public IReadOnlyList<ChoiceOption<string>> TransferChoices { get; } =
        [new("", "Keep source"), new("bt709", "BT.709 (SDR)"), new("smpte2084", "PQ / SMPTE 2084 (HDR10)"), new("arib-std-b67", "HLG"), new("bt2020-10", "BT.2020 10-bit (SDR)")];
    public IReadOnlyList<ChoiceOption<string>> MatrixChoices { get; } =
        [new("", "Keep source"), new("bt709", "BT.709"), new("bt2020nc", "BT.2020 non-constant"), new("bt470bg", "BT.470BG"), new("smpte170m", "SMPTE 170M")];
    public IReadOnlyList<ChoiceOption<string>> RangeChoices { get; } =
        [new("", "Keep source"), new("tv", "Limited (TV, 16–235)"), new("pc", "Full (PC, 0–255)")];

    // ============================================================= target file (preview / command / HDR info)

    private QueueItem? _encodeTarget;
    /// <summary>The file used for the Encode page's HDR info, command preview and preview encode.</summary>
    public QueueItem? EncodeTarget
    {
        get => _encodeTarget;
        set { if (Set(ref _encodeTarget, value)) NotifyTarget(); }
    }

    private void NotifyTarget()
    {
        Notify(nameof(EncodeTarget), nameof(HasTarget), nameof(ManualWarningsText), nameof(HasManualWarnings), nameof(CommandPreview),
            nameof(CommandPreviewTitle), nameof(TargetHdrBadge));
        NotifyTargetStorage();
        CommandManagerInvalidate();
    }

    public bool HasTarget => EncodeTarget != null;
    public string TargetHdrBadge => EncodeTarget?.Probe is { } p ? p.HdrFormat : "";

    public string ManualWarningsText =>
        EncodeTarget?.Probe is { } p ? string.Join(Environment.NewLine, ManualCommands.Warnings(Manual, p).Select(w => "⚠ " + w)) : "";
    public bool HasManualWarnings => ManualWarningsText.Length > 0;

    public string CommandPreviewTitle => IsManualMode ? "FFmpeg command (Manual AV1)" : "ab-av1 commands (AB-AV1)";

    /// <summary>The exact command(s) that would run for the target file with the current settings.</summary>
    public string CommandPreview
    {
        get
        {
            var i = EncodeTarget;
            if (i is null) return "Add a file to see the exact command.";
            if (!IsManualMode) return AbAv1PreviewFor(i);
            if (Tools.FfmpegPath is null) return "FFmpeg not found — see Settings › Tools.";
            if (i.Probe is null) return "Reading the file (ffprobe)…";
            try
            {
                var ext = OutputPlanner.ContainerExtension(Manual.Container, i.SourcePath);
                var streams = AbAv1Commands.PlanStreams(TrackOptions.FromManual(Manual), i.Probe, ext, i.AudioSelection, i.SubtitleSelection);
                var plan = OutputPlanner.Plan(Settings, i, i.CrfOverride ?? Manual.Quality, ext, Manual.Preset);
                var args = ManualCommands.Build(Manual, i.Probe, i.SourcePath, plan.PartialPath, ext, streams,
                    i.CrfOverride ?? Manual.Quality, Path.Combine(AppPaths.Progress, "<progress>.txt"), Settings.FailFast);
                var notes = streams.Warnings.Concat(streams.Blocker is null ? [] : [streams.Blocker])
                    .Concat(plan.SkipReason is null ? [] : [plan.SkipReason]).Concat(ManualCommands.Validate(Manual, Tools)).ToList();
                return CommandLine.Format(Tools.FfmpegPath, args) +
                       $"\n\n# Writes {Path.GetFileName(plan.PartialPath)}, renamed to {Path.GetFileName(plan.FinalPath)} only after verification." +
                       (notes.Count > 0 ? "\n\n# Notes\n" + string.Join("\n", notes.Select(n => "• " + n)) : "");
            }
            catch (Exception ex) { return "Cannot build the command: " + ex.Message; }
        }
    }

    // ============================================================= storage for the target

    private string _targetFree = "—", _targetSource = "—", _targetTemp = "—";
    public string TargetFreeSpaceText { get => _targetFree; private set => Set(ref _targetFree, value); }
    public string TargetSourceText { get => _targetSource; private set => Set(ref _targetSource, value); }
    public string TargetTempText { get => _targetTemp; private set => Set(ref _targetTemp, value); }

    private void NotifyTargetStorage()
    {
        var i = EncodeTarget;
        if (i is null) { TargetFreeSpaceText = TargetSourceText = TargetTempText = "—"; return; }
        var dir = OutputPlanner.OutputDirectory(Settings, i);
        var disk = Win32.DiskSpace(dir);
        TargetFreeSpaceText = disk is { } d ? $"{Fmt.Bytes((long)d.Free)} on {Path.GetPathRoot(dir)}" : "unknown";
        TargetSourceText = Fmt.Bytes(i.SourceSize);
        long est = EstimateOutput(i);
        string basis = i.Search?.PredictedSize != null ? "ab-av1 prediction"
            : _previewEstimate.ContainsKey(i.Id) ? "extrapolated from preview" : "no estimate yet — source size used";
        long need = (long)(est * Settings.SpaceSafetyFactor) + (long)(Settings.MinFreeSpaceGB * 1024 * 1024 * 1024);
        TargetTempText = $"{Fmt.Bytes(need)} ({basis}, × {Settings.SpaceSafetyFactor:0.##} + {Settings.MinFreeSpaceGB:0.#} GB reserve)" +
                         (disk is { } dd && (long)dd.Free < need ? " — NOT ENOUGH SPACE" : "");
    }

    // ============================================================= preview

    private readonly Dictionary<Guid, long> _previewEstimate = new();
    private CancellationTokenSource? _previewCts;

    public int PreviewSeconds
    {
        get => Manual.PreviewSeconds;
        set { Manual.PreviewSeconds = Math.Clamp(value, 2, 600); OnPropertyChanged(); }
    }

    private bool _isPreviewRunning;
    public bool IsPreviewRunning { get => _isPreviewRunning; private set { if (Set(ref _isPreviewRunning, value)) CommandManagerInvalidate(); } }

    private string _previewStatus = "Encode a short sample with the exact settings to judge quality, size and speed before the full encode.";
    public string PreviewStatus { get => _previewStatus; private set => Set(ref _previewStatus, value); }

    private PreviewResult? _preview;
    public PreviewResult? Preview { get => _preview; private set { Set(ref _preview, value); Notify(nameof(HasPreview)); } }
    public bool HasPreview => Preview != null;

    private ImageSource? _sourceFrame, _encodedFrame;
    public ImageSource? SourceFrame { get => _sourceFrame; private set => Set(ref _sourceFrame, value); }
    public ImageSource? EncodedFrame { get => _encodedFrame; private set => Set(ref _encodedFrame, value); }

    public RelayCommand EncodePreviewCommand { get; private set; } = null!;
    public RelayCommand PreviewJobCommand { get; private set; } = null!;
    public RelayCommand CancelPreviewCommand { get; private set; } = null!;
    public RelayCommand PlayPreviewCommand { get; private set; } = null!;
    public RelayCommand PlaySourceSegmentCommand { get; private set; } = null!;
    public RelayCommand StartEncodingCommand { get; private set; } = null!;
    public RelayCommand ResetManualCommand { get; private set; } = null!;

    private void InitEncodeCommands()
    {
        EncodePreviewCommand = new RelayCommand(() => _ = RunPreviewAsync(false), () => !IsPreviewRunning && EncodeTarget?.Probe != null && Tools.FfmpegPath != null);
        PreviewJobCommand = new RelayCommand(() => _ = RunPreviewAsync(true), () => !IsPreviewRunning && EncodeTarget?.Probe != null && Tools.FfmpegPath != null);
        CancelPreviewCommand = new RelayCommand(() => _previewCts?.Cancel(), () => IsPreviewRunning);
        PlayPreviewCommand = new RelayCommand(() => Open(Preview?.OutputPath), () => Preview != null);
        PlaySourceSegmentCommand = new RelayCommand(() => Open(Preview?.SourceSegmentPath), () => Preview?.SourceSegmentPath != null);
        StartEncodingCommand = new RelayCommand(() => _ = StartAsync(RunMode.AnalyzeAndEncode, null), CanStart);
        ResetManualCommand = new RelayCommand(() =>
        {
            if (ConfirmDialog?.Invoke("Reset Manual AV1 settings", "Restore all Manual AV1 settings to their defaults?") != true) return;
            Settings.Manual = new ManualSettings();
            AttachManual();
            EnsureManualEncoderAvailable();
            SaveSettings();
            NotifyManual();
        });
    }

    /// <summary>Encodes a preview of <see cref="EncodeTarget"/>. <paramref name="useJobSettings"/>: with the settings and mode
    /// the file was queued with (what will really run); otherwise with the current settings and mode.</summary>
    private async Task RunPreviewAsync(bool useJobSettings)
    {
        var item = EncodeTarget;
        if (item is null) return;
        var mode = useJobSettings && item.Kind == ItemKind.Video ? item.Mode : SelectedMode;
        var settings = (useJobSettings ? JobSettings(item) : Settings).Clone();
        var errors = mode == EncodeMode.Manual ? ManualCommands.Validate(settings.Manual, Tools) : AbAv1Commands.Validate(settings);
        if (errors.Count > 0) { PreviewStatus = "Cannot preview: " + string.Join(" ", errors); return; }
        if (mode == EncodeMode.AbAv1 && Tools.AbAv1Path is null) { PreviewStatus = "ab-av1 not found — see Settings › Tools."; return; }

        IsPreviewRunning = true;
        _previewCts = new CancellationTokenSource();
        PreviewStatus = $"Encoding a {PreviewSeconds} s preview of {item.FileName}…";
        SourceFrame = EncodedFrame = null;
        try
        {
            var r = await Task.Run(() => PreviewService.RunAsync(item, mode, settings, Tools, PreviewSeconds, _previewCts.Token));
            Preview = r;
            SourceFrame = LoadImage(r.SourceFrame);
            EncodedFrame = LoadImage(r.EncodedFrame);
            if (r.EstimatedFullSize is long est && mode == EncodeMode.Manual) _previewEstimate[item.Id] = est;
            PreviewStatus = $"Preview ready: {Fmt.Bytes(r.Size)} for {r.Seconds:0.#} s at {r.BitrateKbps:0} kb/s" +
                            (r.EstimatedFullSize is long e ? $" → full file ≈ {Fmt.Bytes(e)} (extrapolated; real size varies with content)" : "");
            Log.Success($"Preview: {r.Encoder}, {r.QualityText}, preset {r.Preset}: {Fmt.Bytes(r.Size)}, {r.BitrateKbps:0} kb/s, " +
                        $"encoded in {r.EncodeTime.TotalSeconds:0.0} s", item.FileName);
            NotifyTargetStorage();
        }
        catch (OperationCanceledException) { PreviewStatus = "Preview cancelled."; }
        catch (Exception ex)
        {
            var why = ErrorExplainer.Explain(ex.Message);
            PreviewStatus = $"Preview failed: {ex.Message}\nWhy: {why.Why}\nWhat you can do: {why.Fix}";
            Log.Error("Preview failed: " + ex.Message, item.FileName);
        }
        finally { IsPreviewRunning = false; }
    }

    private static ImageSource? LoadImage(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static void Open(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Cannot open {path}: {ex.Message}"); }
    }

    /// <summary>Ask before starting when Manual settings would convert or discard HDR / colour information.</summary>
    private bool ConfirmManualWarnings(IEnumerable<QueueItem> items)
    {
        var warnings = items.Where(i => i.Probe != null)
            .SelectMany(i => ManualCommands.Warnings(Manual, i.Probe!)).Distinct().ToList();
        if (warnings.Count == 0) return true;
        return ConfirmDialog?.Invoke("Please confirm",
            "Some Manual AV1 settings may change or discard information:\n\n" + string.Join("\n", warnings.Select(w => "• " + w)) +
            "\n\nThis operation may convert or discard HDR information. Continue?") ?? false;
    }

    // ============================================================= AB-AV1 summaries (Encode page)

    public string AbAv1AudioSummary => Settings.AudioMode switch
    {
        AudioMode.Copy => "Copy all audio" + (string.IsNullOrWhiteSpace(Settings.AudioLanguages) ? "" : $" (languages: {Settings.AudioLanguages})"),
        AudioMode.Transcode => $"Transcode to {Settings.AudioCodec}" + (string.IsNullOrWhiteSpace(Settings.AudioBitrate) ? "" : $" {Settings.AudioBitrate}"),
        _ => "Remove audio",
    };

    public string AbAv1SubtitleSummary => Settings.SubtitleMode switch
    {
        SubtitleMode.CopyAll => "Copy all subtitles",
        SubtitleMode.Languages => $"Keep subtitles: {Settings.SubtitleLanguages}",
        _ => "Remove subtitles",
    };

    public string AbAv1AnalysisSummary =>
        $"Max size {Fmt.Num(Settings.MaxEncodedPercent ?? 80)}% of source · CRF range {(Settings.MinCrf?.ToString() ?? "auto")}–{(Settings.MaxCrf?.ToString() ?? "auto")}" +
        $" · samples {(Settings.Samples?.ToString() ?? "auto")}{(Settings.Thorough ? " · thorough" : "")}";

    public string OutputSummary =>
        (string.IsNullOrWhiteSpace(Settings.DestinationFolder) ? "Next to each source" : Settings.DestinationFolder) +
        " · " + Settings.Naming switch
        {
            NamingMode.Suffix => $"Movie.mkv → Movie{Settings.Suffix}.<ext>",
            NamingMode.SameName => "same filename",
            _ => $"template {Settings.NameTemplate}",
        };
}
