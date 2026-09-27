using System.Text.Json;

namespace AV1Studio.Models;

public enum AudioMode { Copy, Transcode, Remove }
public enum SubtitleMode { CopyAll, Languages, Remove }
public enum ContainerFormat { Mkv, Mp4, WebM, SameAsSource }
/// <summary>Auto: same filename when writing to a destination folder, suffix when writing next to the source.</summary>
public enum NamingMode { Suffix, SameName, Template, Auto }
/// <summary>What to do when the destination file already exists.</summary>
public enum CollisionPolicy { Skip, AppendNumber, Overwrite, ReuseIfValid, Ask }
public enum DeleteMode { Permanent, RecycleBin }
public enum SpaceAction { PauseQueue, SkipFile }
public enum EncoderPriority { Normal, BelowNormal, Idle }
public enum ScdMode { Default, On, Off }
public enum AppTheme { Dark, Light }
public enum CpuUsageMode { Auto, Maximum, Balanced, Low, Custom }
/// <summary>Process priorities offered to users. Realtime is intentionally not available.</summary>
public enum ProcessPriority { Idle, BelowNormal, Normal, AboveNormal, High }

/// <summary>CPU usage for one processing stage (see ResourcePlanner).</summary>
public sealed class CpuProfile
{
    public CpuUsageMode Mode { get; set; } = CpuUsageMode.Auto;
    /// <summary>Custom: logical processors to use (0 = all).</summary>
    public int Threads { get; set; }
    public ProcessPriority Priority { get; set; } = ProcessPriority.BelowNormal;
    /// <summary>Custom, optional: explicit logical processors, e.g. "0-7,12".</summary>
    public string Affinity { get; set; } = "";
}

/// <summary>All user configuration. Null/empty values mean "let ab-av1 decide" so that
/// ab-av1's own defaults apply wherever the user has not explicitly chosen something.</summary>
public sealed class AppSettings
{
    // ---------- general ----------
    /// <summary>Mode preselected on the Encode page (and used for files added by drag &amp; drop).</summary>
    public EncodeMode DefaultMode { get; set; } = EncodeMode.AbAv1;
    /// <summary>How much of the Manual AV1 page is shown.</summary>
    public SettingsLevel ManualLevel { get; set; } = SettingsLevel.Basic;

    /// <summary>First-run system check has been shown.</summary>
    public bool FirstRunDone { get; set; }

    // ---------- appearance ----------
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool Animations { get; set; } = true;

    // ---------- logs ----------
    /// <summary>Also show raw ab-av1 / FFmpeg output lines in the Logs page (always written to the log file).</summary>
    public bool ShowToolOutput { get; set; }
    public int LogRetentionDays { get; set; } = 30;

    // ---------- Manual AV1 (separate from every AB-AV1 option below) ----------
    public ManualSettings Manual { get; set; } = new();

    // ---------- library ----------
    public string SourceFolder { get; set; } = "";
    /// <summary>Empty = write next to the source file.</summary>
    public string DestinationFolder { get; set; } = "";
    public bool Recursive { get; set; } = true;
    public string Extensions { get; set; } = "mkv,mp4,m4v,webm,avi,mov,wmv,ts,m2ts,mts,flv,mpg,mpeg,vob,ogv,3gp,divx";
    public bool SkipAv1Sources { get; set; } = true;
    public double MinFileSizeMB { get; set; } = 0;

    // ---------- tools ----------
    public string AbAv1Path { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    /// <summary>ab-av1 --temp-dir for sample files. Empty = app data temp folder.</summary>
    public string TempFolder { get; set; } = "";

    // ---------- quality / crf-search ----------
    public string QualityPreset { get; set; } = "High Quality";
    public double TargetVmaf { get; set; } = 95;
    /// <summary>ab-av1 --max-encoded-percent (its default is 80).</summary>
    public double? MaxEncodedPercent { get; set; }
    public double? MinCrf { get; set; }
    public double? MaxCrf { get; set; }
    public double? CrfIncrement { get; set; }
    public bool Thorough { get; set; }
    public int? Samples { get; set; }
    public string SampleEvery { get; set; } = "";
    public int? MinSamples { get; set; }
    public string SampleDuration { get; set; } = "";
    public bool AbAv1SampleCache { get; set; } = true;
    /// <summary>Extra --vmaf args, one per line, e.g. n_subsample=2.</summary>
    public string VmafArgs { get; set; } = "";
    /// <summary>--vmaf-scale (auto / none / WxH). Empty = ab-av1 default.</summary>
    public string VmafScale { get; set; } = "";

    // ---------- encoder ----------
    /// <summary>SVT-AV1 preset. Null = ab-av1 default.</summary>
    public int? Preset { get; set; }
    /// <summary>Extra SVT-AV1 parameters, e.g. "film-grain=8 tune=0" (space, colon or newline separated).</summary>
    public string SvtParams { get; set; } = "";
    /// <summary>--keyint, e.g. "10s" or "240". Empty = ab-av1 default.</summary>
    public string Keyint { get; set; } = "";
    public ScdMode Scd { get; set; } = ScdMode.Default;
    /// <summary>SVT-AV1 "lp" (logical processors). 0 = encoder default (all).</summary>
    public int EncoderThreads { get; set; }
    /// <summary>Extra ffmpeg output args via ab-av1 --enc, one "key=value" per line.</summary>
    public string ExtraEncArgs { get; set; } = "";
    /// <summary>Extra ffmpeg input args via ab-av1 --enc-input, one "key=value" per line.</summary>
    public string ExtraEncInputArgs { get; set; } = "";

    // ---------- hardware encoding ----------
    /// <summary>Off by default: SVT-AV1 on the CPU gives the smallest files for a given quality.
    /// When on, ab-av1 drives a GPU AV1 encoder (-e) instead; the VMAF-targeted CRF search still applies.</summary>
    public bool HardwareEncoding { get; set; }
    /// <summary>av1_nvenc (NVIDIA RTX 40xx+) or av1_qsv (Intel Arc / Core Ultra).</summary>
    public string HardwareEncoder { get; set; } = "av1_nvenc";
    /// <summary>GPU encoder preset (nvenc: p1–p7, qsv: veryfast…veryslow). Empty = encoder default.</summary>
    public string HardwarePreset { get; set; } = "";

    // ---------- video ----------
    /// <summary>Empty = ab-av1 default (yuv420p10le for SVT-AV1).</summary>
    public string PixelFormat { get; set; } = "";
    /// <summary>0 = same as source. Otherwise max output height (never upscales).</summary>
    public int MaxHeight { get; set; }
    /// <summary>Empty = same as source.</summary>
    public string Fps { get; set; } = "";
    /// <summary>ffmpeg crop "w:h:x:y". Empty = no crop.</summary>
    public string Crop { get; set; } = "";
    public string CustomVideoFilter { get; set; } = "";

    // ---------- audio ----------
    public AudioMode AudioMode { get; set; } = AudioMode.Copy;
    public string AudioCodec { get; set; } = "libopus";
    /// <summary>e.g. "160k". Empty = ffmpeg/ab-av1 default.</summary>
    public string AudioBitrate { get; set; } = "";
    public bool DownmixToStereo { get; set; }
    /// <summary>Comma separated ISO-639 codes to keep, e.g. "eng,fre,und". Empty = keep all.</summary>
    public string AudioLanguages { get; set; } = "";

    // ---------- subtitles ----------
    public SubtitleMode SubtitleMode { get; set; } = SubtitleMode.CopyAll;
    public string SubtitleLanguages { get; set; } = "";

    // ---------- metadata ----------
    public bool KeepChapters { get; set; } = true;
    public bool KeepGlobalMetadata { get; set; } = true;
    public bool KeepAttachments { get; set; } = true;

    // ---------- output ----------
    public ContainerFormat Container { get; set; } = ContainerFormat.SameAsSource;
    public NamingMode Naming { get; set; } = NamingMode.Auto;
    public string Suffix { get; set; } = "_AV1";
    /// <summary>Tokens: {name} {crf} {vmaf} {preset} {date}</summary>
    public string NameTemplate { get; set; } = "{name}_AV1_crf{crf}";
    public CollisionPolicy Collision { get; set; } = CollisionPolicy.ReuseIfValid;
    public bool PreserveFolderStructure { get; set; } = true;

    // ---------- safety ----------
    public bool DeleteSourceAfterSuccess { get; set; }
    public DeleteMode DeleteMode { get; set; } = DeleteMode.Permanent;
    /// <summary>ab-av1 --verify (full decode + duration check before the output is moved into place).</summary>
    public bool AbAv1Verify { get; set; } = true;
    /// <summary>ab-av1 --fail-fast (ffmpeg -xerror).</summary>
    public bool FailFast { get; set; } = true;
    public double DurationToleranceSeconds { get; set; } = 2.0;
    public bool VerifyStreamCounts { get; set; } = true;
    public double MinFreeSpaceGB { get; set; } = 2;
    /// <summary>Required free space = estimated size × factor (source size when no estimate).</summary>
    public double SpaceSafetyFactor { get; set; } = 1.25;
    public SpaceAction InsufficientSpace { get; set; } = SpaceAction.PauseQueue;

    // ---------- processing ----------
    public int ConcurrentJobs { get; set; } = 1;
    /// <summary>Legacy (pre-1.0) priority setting; superseded by the CPU usage profiles.</summary>
    public EncoderPriority Priority { get; set; } = EncoderPriority.BelowNormal;
    /// <summary>CPU usage of the AB-AV1 CRF search (sample encodes + VMAF).</summary>
    public CpuProfile SearchCpu { get; set; } = new();
    /// <summary>CPU usage of final encodes (AB-AV1 and Manual AV1) and previews.</summary>
    public CpuProfile EncodeCpu { get; set; } = new();

    // ---------- folders ----------
    /// <summary>With a destination folder: recreate the complete source tree (incl. empty folders) and copy
    /// non-video files unchanged next to the encoded videos.</summary>
    public bool MirrorFolderTree { get; set; } = true;
    /// <summary>Also compare SHA-256 hashes of copied non-video files (slower; size is always compared).</summary>
    public bool VerifyCopiesWithHash { get; set; }

    // ---------- ui ----------
    public bool ShowLogPanel { get; set; } = true;
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 900;

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    /// <summary>Legacy quality presets kept for older settings files; see EncodingProfiles for the profiles shown in the UI.</summary>
    public static readonly (string Name, double Vmaf, string Description)[] QualityPresets =
    [
        ("Archive", 97, "VMAF 97 — near-transparent target, larger files"),
        ("High Quality", 95, "VMAF 95 — ab-av1 default target"),
        ("Balanced", 93, "VMAF 93 — smaller files, some detail may be simplified"),
        ("Small File", 90, "VMAF 90 — prioritises size, visible loss possible"),
        ("Fast Encode", 93, "VMAF 93 with a fast preset — quicker, larger files"),
        ("Custom", double.NaN, "Use the values you set"),
    ];
}
