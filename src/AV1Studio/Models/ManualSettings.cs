using System.Text.Json;
using System.Text.Json.Serialization;
using AV1Studio.Mvvm;

namespace AV1Studio.Models;

/// <summary>How a queued file is encoded.</summary>
public enum EncodeMode
{
    /// <summary>ab-av1: target VMAF → automatic CRF search → encode.</summary>
    AbAv1,
    /// <summary>Direct FFmpeg + chosen AV1 encoder with user-chosen parameters (ab-av1 not used).</summary>
    Manual,
}

public enum BitDepth { Bit10, Bit8 }
public enum ResolutionMode { Source, P1080, P1440, P2160, Custom }
public enum Deinterlace { Off, Auto, Always }
public enum Denoise { Off, Light, Medium, Strong }
public enum HdrHandling { Preserve, ToneMapToSdr }
public enum SettingsLevel { Basic, Advanced, Expert }

/// <summary>
/// Manual AV1 configuration. Completely separate from the AB-AV1 settings in <see cref="AppSettings"/>:
/// nothing here is passed to ab-av1, and no AB-AV1 option is used by the Manual engine.
/// Observable so the Encode page (command preview, estimates) updates live. Generated from a
/// compact spec: keep property/field pairs in sync when editing.
/// </summary>
public sealed class ManualSettings : ObservableObject
{

    // ---------- video: basic ----------
    private string _encoder = "libsvtav1";
    /// <summary>libsvtav1, av1_nvenc, av1_qsv or av1_amf — only encoders that pass a real test encode are offered.</summary>
    public string Encoder { get => _encoder; set => Set(ref _encoder, value); }
    private double _quality = 30;
    /// <summary>SVT-AV1 CRF (0–63), NVENC CQ (0–51), QSV ICQ (1–51), AMF QP (0–255). Lower = higher quality, bigger file.</summary>
    public double Quality { get => _quality; set => Set(ref _quality, value); }
    private string _preset = "5";
    /// <summary>SVT-AV1 0–13; NVENC p1–p7; QSV veryfast…veryslow; AMF speed/balanced/quality.</summary>
    public string Preset { get => _preset; set => Set(ref _preset, value); }
    private BitDepth _bitDepth = BitDepth.Bit10;
    public BitDepth BitDepth { get => _bitDepth; set => Set(ref _bitDepth, value); }
    private ResolutionMode _resolution = ResolutionMode.Source;
    public ResolutionMode Resolution { get => _resolution; set => Set(ref _resolution, value); }
    private int _customWidth = 1920;
    public int CustomWidth { get => _customWidth; set => Set(ref _customWidth, value); }
    private int _customHeight = 1080;
    public int CustomHeight { get => _customHeight; set => Set(ref _customHeight, value); }
    private string _fps = "";
    /// <summary>Empty = keep source frame rate.</summary>
    public string Fps { get => _fps; set => Set(ref _fps, value); }

    // ---------- video: advanced ----------
    private string _keyint = "10s";
    /// <summary>Keyframe interval: frames ("240") or seconds ("10s"). Empty = encoder default.</summary>
    public string Keyint { get => _keyint; set => Set(ref _keyint, value); }
    private bool _sceneDetection = true;
    /// <summary>SVT-AV1 scene-change detection (scd).</summary>
    public bool SceneDetection { get => _sceneDetection; set => Set(ref _sceneDetection, value); }
    private int _filmGrain = 0;
    /// <summary>SVT-AV1 film-grain synthesis 0–50 (0 = off).</summary>
    public int FilmGrain { get => _filmGrain; set => Set(ref _filmGrain, value); }
    private bool _filmGrainDenoise = false;
    public bool FilmGrainDenoise { get => _filmGrainDenoise; set => Set(ref _filmGrainDenoise, value); }
    private int _fastDecode = 0;
    /// <summary>SVT-AV1 fast-decode 0–2 (0 = off).</summary>
    public int FastDecode { get => _fastDecode; set => Set(ref _fastDecode, value); }
    private int? _tune = null;
    /// <summary>SVT-AV1 tune: 0 = VQ (subjective), 1 = PSNR. Null = encoder default.</summary>
    public int? Tune { get => _tune; set => Set(ref _tune, value); }
    private int _tileColumns = 0;
    /// <summary>log2 tile columns (0 = encoder decides).</summary>
    public int TileColumns { get => _tileColumns; set => Set(ref _tileColumns, value); }
    private int _tileRows = 0;
    /// <summary>log2 tile rows (0 = encoder decides).</summary>
    public int TileRows { get => _tileRows; set => Set(ref _tileRows, value); }
    private int _threads = 0;
    /// <summary>SVT-AV1 lp (logical processors). 0 = encoder decides.</summary>
    public int Threads { get => _threads; set => Set(ref _threads, value); }

    // ---------- hardware encoder workload (only shown for GPU encoders) ----------
    private int? _hwBFrames = null;
    /// <summary>Max B-frames (NVENC/QSV). Null = encoder default.</summary>
    public int? HwBFrames { get => _hwBFrames; set => Set(ref _hwBFrames, value); }
    private int? _hwLookahead = null;
    /// <summary>Rate-control lookahead in frames (NVENC rc-lookahead, QSV look_ahead_depth). Null = default.</summary>
    public int? HwLookahead { get => _hwLookahead; set => Set(ref _hwLookahead, value); }
    private string _hwMultipass = "";
    /// <summary>NVENC multipass: disabled, qres or fullres. Empty = default.</summary>
    public string HwMultipass { get => _hwMultipass; set => Set(ref _hwMultipass, value); }
    private bool _hwSpatialAq = false;
    /// <summary>NVENC spatial adaptive quantization.</summary>
    public bool HwSpatialAq { get => _hwSpatialAq; set => Set(ref _hwSpatialAq, value); }
    private bool _hwTemporalAq = false;
    /// <summary>NVENC temporal adaptive quantization.</summary>
    public bool HwTemporalAq { get => _hwTemporalAq; set => Set(ref _hwTemporalAq, value); }
    private string _maxBitrate = "";
    /// <summary>Optional bitrate cap, e.g. 12M. Empty = no cap.</summary>
    public string MaxBitrate { get => _maxBitrate; set => Set(ref _maxBitrate, value); }

    // ---------- profile ----------
    private string _profileName = "Balanced";
    /// <summary>Last applied profile (informational).</summary>
    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }

    // ---------- video: expert ----------
    private string _svtParams = "";
    /// <summary>Additional -svtav1-params key=value pairs.</summary>
    public string SvtParams { get => _svtParams; set => Set(ref _svtParams, value); }
    private string _extraFfmpegArgs = "";
    /// <summary>Additional FFmpeg output options, one name=value per line.</summary>
    public string ExtraFfmpegArgs { get => _extraFfmpegArgs; set => Set(ref _extraFfmpegArgs, value); }

    // ---------- filters (all off = "No processing") ----------
    private string _crop = "";
    /// <summary>w:h:x:y</summary>
    public string Crop { get => _crop; set => Set(ref _crop, value); }
    private Deinterlace _deinterlace = Deinterlace.Off;
    public Deinterlace Deinterlace { get => _deinterlace; set => Set(ref _deinterlace, value); }
    private bool _detelecine = false;
    public bool Detelecine { get => _detelecine; set => Set(ref _detelecine, value); }
    private Denoise _denoise = Denoise.Off;
    public Denoise Denoise { get => _denoise; set => Set(ref _denoise, value); }
    private bool _deblock = false;
    public bool Deblock { get => _deblock; set => Set(ref _deblock, value); }
    private bool _grayscale = false;
    public bool Grayscale { get => _grayscale; set => Set(ref _grayscale, value); }
    private string _customFilter = "";
    public string CustomFilter { get => _customFilter; set => Set(ref _customFilter, value); }

    // ---------- HDR / color ----------
    private HdrHandling _hdr = HdrHandling.Preserve;
    /// <summary>Preserve (default) or tone-map HDR to SDR (destructive, asks for confirmation).</summary>
    public HdrHandling Hdr { get => _hdr; set => Set(ref _hdr, value); }
    private string _colorRange = "";
    /// <summary>Tag override; empty = keep source.</summary>
    public string ColorRange { get => _colorRange; set => Set(ref _colorRange, value); }
    private string _colorPrimaries = "";
    /// <summary>Tag override; empty = keep source.</summary>
    public string ColorPrimaries { get => _colorPrimaries; set => Set(ref _colorPrimaries, value); }
    private string _colorTransfer = "";
    /// <summary>Tag override; empty = keep source.</summary>
    public string ColorTransfer { get => _colorTransfer; set => Set(ref _colorTransfer, value); }
    private string _colorMatrix = "";
    /// <summary>Tag override; empty = keep source.</summary>
    public string ColorMatrix { get => _colorMatrix; set => Set(ref _colorMatrix, value); }

    // ---------- audio ----------
    private AudioMode _audioMode = AudioMode.Copy;
    public AudioMode AudioMode { get => _audioMode; set => Set(ref _audioMode, value); }
    private string _audioCodec = "libopus";
    public string AudioCodec { get => _audioCodec; set => Set(ref _audioCodec, value); }
    private string _audioBitrate = "";
    public string AudioBitrate { get => _audioBitrate; set => Set(ref _audioBitrate, value); }
    private bool _downmixToStereo = false;
    public bool DownmixToStereo { get => _downmixToStereo; set => Set(ref _downmixToStereo, value); }
    private string _audioLanguages = "";
    public string AudioLanguages { get => _audioLanguages; set => Set(ref _audioLanguages, value); }

    // ---------- subtitles ----------
    private SubtitleMode _subtitleMode = SubtitleMode.CopyAll;
    public SubtitleMode SubtitleMode { get => _subtitleMode; set => Set(ref _subtitleMode, value); }
    private string _subtitleLanguages = "";
    public string SubtitleLanguages { get => _subtitleLanguages; set => Set(ref _subtitleLanguages, value); }
    private bool _forcedSubtitlesOnly = false;
    public bool ForcedSubtitlesOnly { get => _forcedSubtitlesOnly; set => Set(ref _forcedSubtitlesOnly, value); }
    private string _defaultSubtitleLanguage = "";
    /// <summary>Language of the subtitle to flag as default. Empty = keep source flags.</summary>
    public string DefaultSubtitleLanguage { get => _defaultSubtitleLanguage; set => Set(ref _defaultSubtitleLanguage, value); }

    // ---------- metadata ----------
    private bool _keepChapters = true;
    public bool KeepChapters { get => _keepChapters; set => Set(ref _keepChapters, value); }
    private bool _keepGlobalMetadata = true;
    public bool KeepGlobalMetadata { get => _keepGlobalMetadata; set => Set(ref _keepGlobalMetadata, value); }
    private bool _keepAttachments = true;
    public bool KeepAttachments { get => _keepAttachments; set => Set(ref _keepAttachments, value); }

    // ---------- output ----------
    // Legacy: the output container is AppSettings.Container (shared by both modes); kept for old settings files.
    private ContainerFormat _container = ContainerFormat.Mkv;
    public ContainerFormat Container { get => _container; set => Set(ref _container, value); }

    // ---------- preview ----------
    private int _previewSeconds = 30;
    public int PreviewSeconds { get => _previewSeconds; set => Set(ref _previewSeconds, value); }

    public ManualSettings Clone() => JsonSerializer.Deserialize<ManualSettings>(JsonSerializer.Serialize(this))!;

    /// <summary>True when any filter/processing is active ("No processing" otherwise).</summary>
    [JsonIgnore]
    public bool AnyFilter =>
        !string.IsNullOrWhiteSpace(Crop) || Deinterlace != Deinterlace.Off || Detelecine || Denoise != Denoise.Off
        || Deblock || Grayscale || !string.IsNullOrWhiteSpace(CustomFilter) || Hdr == HdrHandling.ToneMapToSdr
        || Resolution != ResolutionMode.Source || !string.IsNullOrWhiteSpace(Fps);
}
