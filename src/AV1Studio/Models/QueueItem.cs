using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;
using AV1Studio.Mvvm;
using AV1Studio.Util;

namespace AV1Studio.Models;

/// <summary>A video in the library queue. Persisted fields survive restarts (see StateStore);
/// [JsonIgnore] fields are live progress only.</summary>
public sealed class QueueItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // ---------- mode ----------
    private EncodeMode _mode = EncodeMode.AbAv1;
    /// <summary>AB-AV1 (automatic quality) or Manual AV1 (direct FFmpeg). Persisted; old queues default to AB-AV1.</summary>
    public EncodeMode Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value)) Notify(nameof(ModeText), nameof(CrfText)); }
    }

    [JsonIgnore] public string ModeText => Kind == ItemKind.Copy ? "Copy" : Mode == EncodeMode.AbAv1 ? "AB-AV1" : "Manual AV1";

    private ItemKind _kind = ItemKind.Video;
    /// <summary>Video = encode; Copy = non-video file of a mirrored folder, copied unchanged.</summary>
    public ItemKind Kind
    {
        get => _kind;
        set { if (Set(ref _kind, value)) Notify(nameof(ModeText), nameof(StatusText), nameof(KindIcon)); }
    }

    /// <summary>Settings snapshot this job was queued with (see QueueState.Profiles). Changing the
    /// current settings never modifies already queued jobs.</summary>
    public string? ProfileId { get; set; }

    /// <summary>Set when the file belongs to a folder that is mirrored into the destination.</summary>
    public Guid? FolderJobId { get; set; }

    /// <summary>Path relative to the folder the user selected, e.g. "Series\Season 1\Episode 01.mkv".</summary>
    [JsonIgnore]
    public string RelativePath
    {
        get
        {
            if (string.IsNullOrEmpty(SourceRoot)) return FileName;
            var rel = Path.GetRelativePath(SourceRoot, SourcePath);
            return rel.StartsWith("..") ? FileName : rel;
        }
    }

    /// <summary>Queue grouping: the folder job's root name plus the relative sub-folder.</summary>
    [JsonIgnore]
    public string GroupKey
    {
        get
        {
            if (FolderJobId is null || string.IsNullOrEmpty(SourceRoot)) return "Individual files";
            var root = Path.GetFileName(SourceRoot.TrimEnd('\\')) is { Length: > 0 } n ? n : SourceRoot;
            var dir = Path.GetDirectoryName(RelativePath);
            return string.IsNullOrEmpty(dir) ? root : $"{root}\\{dir}";
        }
    }

    /// <summary>Icon (with text alternative in ModeText/StatusText, never colour alone).</summary>
    [JsonIgnore]
    public string KindIcon => Kind == ItemKind.Video ? "🎬" : Path.GetExtension(SourcePath).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff" => "🖼",
        ".srt" or ".ass" or ".ssa" or ".sub" or ".idx" or ".vtt" or ".sup" => "💬",
        _ => "📄",
    };

    // Planned (not yet started) or actual encoder / preset shown in the queue.
    private string? _encoderText;
    public string? EncoderText { get => _encoderText; set => Set(ref _encoderText, value); }

    private string? _targetText;
    /// <summary>What the job aims for: "VMAF 95" (AB-AV1) or "CRF 30" (Manual AV1).</summary>
    public string? TargetText { get => _targetText; set => Set(ref _targetText, value); }

    private string? _presetText;
    public string? PresetText { get => _presetText; set => Set(ref _presetText, value); }

    /// <summary>Manual mode quality value the file will use (template value or per-file override).</summary>
    private double? _manualQuality;
    [JsonIgnore] public double? ManualQuality { get => _manualQuality; set { if (Set(ref _manualQuality, value)) OnPropertyChanged(nameof(CrfText)); } }

    // ---------- source ----------
    private string _sourcePath = "";
    public string SourcePath
    {
        get => _sourcePath;
        set { if (Set(ref _sourcePath, value)) Notify(nameof(FileName), nameof(SourceDirectory), nameof(RelativePath), nameof(GroupKey)); }
    }

    /// <summary>Folder the file was scanned from; used to mirror sub-folders in the destination.</summary>
    public string? SourceRoot { get; set; }

    private long _sourceSize;
    public long SourceSize
    {
        get => _sourceSize;
        set { if (Set(ref _sourceSize, value)) Notify(nameof(SourceSizeText), nameof(CompressionText)); }
    }

    public DateTime SourceModifiedUtc { get; set; }
    public string? QuickHash { get; set; }

    private ProbeInfo? _probe;
    public ProbeInfo? Probe
    {
        get => _probe;
        set { if (Set(ref _probe, value)) Notify(nameof(DurationText), nameof(Resolution), nameof(VideoCodec), nameof(MediaSummary), nameof(HdrText), nameof(ColorSummary)); }
    }

    // ---------- status ----------
    private ItemStatus _status = ItemStatus.Waiting;
    public ItemStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) Notify(nameof(StatusText), nameof(IsBusy), nameof(SourceStateText)); }
    }

    private string? _statusDetail;
    public string? StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }

    private string? _error;
    /// <summary>What happened (human readable).</summary>
    public string? ErrorMessage { get => _error; set { if (Set(ref _error, value)) OnPropertyChanged(nameof(SourceStateText)); } }

    private string? _errorWhy;
    /// <summary>Why it probably happened.</summary>
    public string? ErrorWhy { get => _errorWhy; set => Set(ref _errorWhy, value); }

    private string? _errorFix;
    /// <summary>What the user can do.</summary>
    public string? ErrorFix { get => _errorFix; set => Set(ref _errorFix, value); }

    /// <summary>Technical detail (raw tool message) kept for the log / copy.</summary>
    public string? ErrorDetail { get; set; }

    private bool _sourceDeleted;
    public bool SourceDeleted { get => _sourceDeleted; set { if (Set(ref _sourceDeleted, value)) OnPropertyChanged(nameof(SourceStateText)); } }

    // ---------- analysis ----------
    private CrfSearchResult? _search;
    public CrfSearchResult? Search
    {
        get => _search;
        set { if (Set(ref _search, value)) Notify(nameof(CrfText), nameof(VmafText), nameof(EstimatedSizeText), nameof(EffectiveCrf)); }
    }

    public string? SearchFingerprint { get; set; }
    public DateTime? SearchedAtUtc { get; set; }
    public ObservableCollection<CrfAttempt> Attempts { get; set; } = new();

    private double? _crfOverride;
    /// <summary>User override of the detected CRF. Null = use ab-av1's result.</summary>
    public double? CrfOverride
    {
        get => _crfOverride;
        set { if (Set(ref _crfOverride, value)) Notify(nameof(CrfText), nameof(EffectiveCrf), nameof(CrfOverrideText)); }
    }

    [JsonIgnore]
    public string CrfOverrideText
    {
        get => CrfOverride is double d ? Fmt.Arg(d) : "";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) CrfOverride = null;
            else if (Fmt.TryParseDouble(value, out var d) && d >= 0 && d <= 255) CrfOverride = d;
            OnPropertyChanged();
        }
    }

    /// <summary>AB-AV1: override or detected CRF. (Manual mode resolves its quality from the Manual settings.)</summary>
    [JsonIgnore] public double? EffectiveCrf => CrfOverride ?? Search?.Crf;

    // ---------- output ----------
    private string? _outputPath;
    public string? OutputPath { get => _outputPath; set => Set(ref _outputPath, value); }

    /// <summary>Temporary output path while encoding (".partial"). Recorded so crashes can be recovered.</summary>
    public string? PartialPath { get; set; }

    /// <summary>Set when a verified partial output is waiting to be renamed over the (deleted) source.</summary>
    public bool PendingRename { get; set; }

    private long? _actualOutputSize;
    public long? ActualOutputSize
    {
        get => _actualOutputSize;
        set { if (Set(ref _actualOutputSize, value)) Notify(nameof(OutputSizeText), nameof(CompressionText)); }
    }

    public string? EncodeParameters { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    private string? _lastSearchCmd;
    public string? LastCrfSearchCommand { get => _lastSearchCmd; set => Set(ref _lastSearchCmd, value); }

    private string? _lastEncodeCmd;
    public string? LastEncodeCommand { get => _lastEncodeCmd; set => Set(ref _lastEncodeCmd, value); }

    /// <summary>Per-file audio track choice (type indexes to keep). Null = global rules.</summary>
    public List<int>? AudioSelection { get; set; }
    /// <summary>Per-file subtitle track choice (type indexes to keep). Null = global rules.</summary>
    public List<int>? SubtitleSelection { get; set; }

    // ---------- live progress (not persisted) ----------
    private double _progress;
    [JsonIgnore] public double Progress { get => _progress; set => Set(ref _progress, value); }

    private string? _activity;
    [JsonIgnore] public string? Activity { get => _activity; set => Set(ref _activity, value); }

    private double? _fps;
    [JsonIgnore] public double? CurrentFps { get => _fps; set { if (Set(ref _fps, value)) OnPropertyChanged(nameof(SpeedText)); } }

    private bool _isPaused;
    /// <summary>The running encode is suspended (processes frozen, not killed).</summary>
    [JsonIgnore] public bool IsPaused { get => _isPaused; set { if (Set(ref _isPaused, value)) OnPropertyChanged(nameof(StatusText)); } }

    private double? _gpu;
    /// <summary>GPU video-encode engine utilisation of the encoder processes (hardware encoders only).</summary>
    [JsonIgnore] public double? GpuPercent { get => _gpu; set => Set(ref _gpu, value); }

    private double? _curBitrate;
    /// <summary>Bitrate of the last measurement interval, kbit/s.</summary>
    [JsonIgnore] public double? CurrentBitrateKbps { get => _curBitrate; set { if (Set(ref _curBitrate, value)) OnPropertyChanged(nameof(CurrentBitrateText)); } }

    private double? _avgBitrate;
    /// <summary>Average bitrate of the output so far, kbit/s.</summary>
    [JsonIgnore] public double? AverageBitrateKbps { get => _avgBitrate; set { if (Set(ref _avgBitrate, value)) OnPropertyChanged(nameof(AverageBitrateText)); } }

    private double? _avgFps;
    [JsonIgnore] public double? AverageFps { get => _avgFps; set => Set(ref _avgFps, value); }

    private string? _bitrate;
    [JsonIgnore] public string? Bitrate { get => _bitrate; set => Set(ref _bitrate, value); }

    private double? _speed;
    [JsonIgnore] public double? Speed { get => _speed; set { if (Set(ref _speed, value)) OnPropertyChanged(nameof(SpeedText)); } }

    private TimeSpan? _elapsed;
    [JsonIgnore] public TimeSpan? Elapsed { get => _elapsed; set { if (Set(ref _elapsed, value)) OnPropertyChanged(nameof(ElapsedText)); } }

    private TimeSpan? _eta;
    [JsonIgnore] public TimeSpan? Eta { get => _eta; set { if (Set(ref _eta, value)) OnPropertyChanged(nameof(EtaText)); } }

    private double? _cpu;
    [JsonIgnore] public double? CpuPercent { get => _cpu; set => Set(ref _cpu, value); }

    private long? _ram;
    [JsonIgnore] public long? RamBytes { get => _ram; set => Set(ref _ram, value); }

    private long? _currentOutputSize;
    [JsonIgnore]
    public long? CurrentOutputSize
    {
        get => _currentOutputSize;
        set { if (Set(ref _currentOutputSize, value)) Notify(nameof(OutputSizeText), nameof(CompressionText)); }
    }

    private long? _projectedSize;
    [JsonIgnore] public long? ProjectedSize { get => _projectedSize; set => Set(ref _projectedSize, value); }

    /// <summary>Running tool process (for pause/resume). Not persisted.</summary>
    [JsonIgnore] internal Services.ChildProcess? ActiveProcess { get; set; }

    /// <summary>Per-item log lines (bounded), shown in the details pane.</summary>
    [JsonIgnore] public ObservableCollection<string> ItemLog { get; } = new();

    public void ResetLiveStats()
    {
        Progress = 0; Activity = null; CurrentFps = null; AverageFps = null; Bitrate = null; Speed = null;
        GpuPercent = null; CurrentBitrateKbps = null; AverageBitrateKbps = null; IsPaused = false;
        Elapsed = null; Eta = null; CpuPercent = null; RamBytes = null; CurrentOutputSize = null; ProjectedSize = null;
    }

    public void InvalidateAnalysis()
    {
        Search = null;
        SearchFingerprint = null;
        SearchedAtUtc = null;
        Attempts = new ObservableCollection<CrfAttempt>();
        OnPropertyChanged(nameof(Attempts));
    }

    // ---------- display helpers ----------
    [JsonIgnore] public string FileName => Path.GetFileName(SourcePath);
    [JsonIgnore] public string SourceDirectory => Path.GetDirectoryName(SourcePath) ?? "";
    [JsonIgnore] public string SourceSizeText => Fmt.Bytes(SourceSize);
    [JsonIgnore] public string DurationText => Fmt.Duration(Probe?.DurationSeconds);
    [JsonIgnore]
    public string Resolution => Probe?.MainVideo is { Width: int w, Height: int h } ? $"{w}×{h}" : "";
    [JsonIgnore] public string VideoCodec => Probe?.MainVideo?.Codec ?? "";
    [JsonIgnore]
    public string StatusText => IsPaused ? "Paused" : Kind == ItemKind.Copy
        ? Status switch
        {
            ItemStatus.Encoding => "Copying",
            ItemStatus.Completed => "Copied",
            _ => Status.Label(),
        }
        : Status.Label();

    [JsonIgnore] public string HdrText => Probe?.HdrFormat ?? "";

    [JsonIgnore]
    public string ColorSummary
    {
        get
        {
            var v = Probe?.MainVideo;
            if (v is null) return "";
            string N(string? x) => string.IsNullOrEmpty(x) ? "unspecified" : x;
            var lines = new List<string>
            {
                $"Dynamic range: {Probe!.HdrFormat}",
                $"Bit depth: {(Probe.BitDepth is int b ? b + "-bit" : "unknown")} ({v.PixFmt})",
                $"Primaries: {N(v.ColorPrimaries)} · Transfer: {N(v.ColorTransfer)} · Matrix: {N(v.ColorSpace)} · Range: {N(v.ColorRange)}",
            };
            if (v.MasteringDisplay != null) lines.Add($"Mastering display: {v.MasteringDisplay}");
            if (v.ContentLight != null) lines.Add($"Content light (MaxCLL,MaxFALL): {v.ContentLight}");
            if (v.DolbyVision) lines.Add("Dolby Vision metadata present (dynamic metadata is not preserved by re-encoding)");
            return string.Join(Environment.NewLine, lines);
        }
    }

    [JsonIgnore]
    public string MediaSummary
    {
        get
        {
            if (Probe is null) return "Not probed yet";
            var v = Probe.MainVideo;
            var lines = new List<string>();
            if (v != null)
                lines.Add($"Video: {v.Codec} {v.Width}×{v.Height} {v.PixFmt} {Fmt.Num(v.FrameRate, "0.###")} fps");
            foreach (var a in Probe.Audio) lines.Add("Audio " + a.Describe());
            foreach (var t in Probe.Subtitles) lines.Add("Subtitle " + t.Describe());
            int att = Probe.Attachments.Count();
            if (att > 0) lines.Add($"Attachments: {att}");
            if (Probe.ChapterCount > 0) lines.Add($"Chapters: {Probe.ChapterCount}");
            if (AudioSelection != null || SubtitleSelection != null) lines.Add("Custom track selection active");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public void RefreshMediaSummary() => OnPropertyChanged(nameof(MediaSummary));
    [JsonIgnore] public bool IsBusy => Status.IsActive();

    [JsonIgnore]
    public string CrfText => CrfOverride is double o
        ? $"{Fmt.Num(o)} (override)"
        : Mode == EncodeMode.Manual ? Fmt.Num(ManualQuality) : Fmt.Num(Search?.Crf);

    [JsonIgnore] public string VmafText => Fmt.Num(Search?.Vmaf, "0.00");
    [JsonIgnore] public string EstimatedSizeText => Fmt.Bytes(Search?.PredictedSize);

    [JsonIgnore]
    public string OutputSizeText => Fmt.Bytes(ActualOutputSize ?? CurrentOutputSize);

    [JsonIgnore]
    public string CompressionText
    {
        get
        {
            var outSize = ActualOutputSize;
            if (outSize is not long o || o <= 0 || SourceSize <= 0) return "";
            return $"{(double)SourceSize / o:0.00}:1 (−{100.0 * (SourceSize - o) / SourceSize:0}%)";
        }
    }

    [JsonIgnore]
    public string SpeedText => CurrentFps is double f
        ? $"{f:0.#} fps" + (Speed is double s ? $" · {s:0.00}x" : "")
        : "";

    [JsonIgnore] public string ElapsedText => Fmt.Duration(Elapsed);
    [JsonIgnore] public string EtaText => Fmt.Duration(Eta);

    [JsonIgnore]
    public string SourceStateText => SourceDeleted ? "Source deleted" :
        Status is ItemStatus.Failed or ItemStatus.Cancelled ? "Source: PRESERVED" : "";

    [JsonIgnore] public string CurrentBitrateText => CurrentBitrateKbps is double c ? $"{c:0} kb/s" : "N/A";
    [JsonIgnore] public string AverageBitrateText => AverageBitrateKbps is double a ? $"{a:0} kb/s" : "N/A";
}
