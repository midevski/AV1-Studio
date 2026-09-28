namespace AV1Studio.Views;

/// <summary>Help texts shown as tooltips and by the "?" help icons.</summary>
public static class Tips
{
    public const string ModeAbAv1 =
        "AB-AV1 · AUTOMATIC QUALITY\n\n" +
        "Choose a quality target (VMAF). AB-AV1 test-encodes short samples, measures their quality and picks the CRF " +
        "that reaches the target with the smallest file. The video is then encoded with that CRF.\n\n" +
        "Recommended for most libraries.";

    public const string ModeManual =
        "MANUAL AV1 · FULL CONTROL\n\n" +
        "Choose the encoder, quality (CRF/CQ), preset and every other option yourself. " +
        "The video is encoded directly with FFmpeg using exactly these settings.";

    public const string Profile =
        "Profiles set the quality and preset for the selected mode. You can adjust every value afterwards.";

    public const string Vmaf =
        "VMAF TARGET\n\n" +
        "VMAF is a video-quality score from 0 to 100, developed by Netflix. AB-AV1 finds the CRF that reaches this score.\n\n" +
        "Higher: closer to the original, larger files.\n" +
        "Lower: smaller files, more visible loss.\n\n" +
        "Default: 95.";

    public const string CrfSearch =
        "CRF SEARCH\n\n" +
        "AB-AV1 encodes a few short samples at different CRF values and measures each with VMAF to find the best CRF for the target. " +
        "More or longer samples are more accurate but take longer. Results are remembered for unchanged files.";

    public const string Samples =
        "SAMPLES\n\n" +
        "How many short segments of the video are test-encoded and measured to find the CRF.\n\n" +
        "Auto (recommended) — AB-AV1 picks the number from the video length.\n" +
        "1–10 — a fixed number. Fewer samples are faster, but the CRF can be off: if the samples land on " +
        "unusually simple or complex scenes, the result can be too low in quality or produce a much larger file " +
        "than needed. More samples are slower and more reliable.";

    public const string Crf =
        "CRF (CONSTANT RATE FACTOR)\n\n" +
        "Sets the quality level of the encode.\n\n" +
        "Lower: higher quality, larger files.\n" +
        "Higher: lower quality, smaller files.\n\n" +
        "The best value depends on the video — use Preview to compare. Hardware encoders call this CQ, ICQ or QP.";

    public const string Preset =
        "PRESET\n\n" +
        "Trades encoding speed for efficiency. Slower presets produce smaller files at the same quality; faster presets finish sooner.\n\n" +
        "SVT-AV1: 0 (slowest) to 13 (fastest). 4–6 is a good range for archiving.";

    public const string BitDepth =
        "BIT DEPTH\n\n" +
        "10-bit reduces banding in skies and gradients and is required for HDR. Supported by virtually all AV1 players.\n\n" +
        "Default: 10-bit.";

    public const string Hardware =
        "HARDWARE ENCODER\n\n" +
        "Encodes on the GPU's video engine (NVIDIA NVENC, Intel Quick Sync, AMD AMF): much faster and light on the CPU, " +
        "usually with somewhat larger files than SVT-AV1 at the same quality.\n\n" +
        "Only encoders that work on this PC can be selected.";

    public const string CpuUsage =
        "CPU USAGE\n\n" +
        "Controls how many CPU threads the encoder uses.\n\n" +
        "Auto — all threads except a few kept free for Windows (default).\n" +
        "Maximum — all threads.\n" +
        "Balanced — about three quarters of the threads.\n" +
        "Low — about half of the threads, for background encoding.\n" +
        "Custom — a number of threads and, optionally, specific CPU cores.";

    public const string CpuSearchVsEncode =
        "AB-AV1 works in two stages: the CRF search (short sample encodes and VMAF measurements) and the final encode. " +
        "Each stage has its own CPU setting.";

    public const string Priority =
        "PROCESS PRIORITY\n\n" +
        "How Windows shares the CPU between the encoder and other programs.\n\n" +
        "High — the encoder comes first; fastest encoding (default).\n" +
        "Above normal / Normal — shares the CPU more evenly.\n" +
        "Below normal / Low — other programs come first; best while you use the PC.";

    public const string Affinity =
        "CPU AFFINITY (OPTIONAL)\n\n" +
        "Runs the encoder only on the listed logical processors, e.g. \"0-7\" or \"4-15,20\" (numbering starts at 0). " +
        "Leave empty to use the thread count.";

    public const string GpuWorkload =
        "HARDWARE ENCODER OPTIONS\n\n" +
        "These options control how much analysis the GPU encoder does per frame. Slower settings improve efficiency and reduce speed. " +
        "Leave a field empty to use the encoder's default.";

    public const string BFrames =
        "B-FRAMES\n\n" +
        "Frames predicted from earlier and later frames. More B-frames usually improve compression at a small cost in speed.";

    public const string Lookahead =
        "LOOKAHEAD\n\n" +
        "Number of upcoming frames analysed before encoding. Helps with scene changes and fades; uses more GPU memory.";

    public const string Multipass =
        "MULTIPASS (NVENC)\n\n" +
        "An extra analysis pass per frame (quarter or full resolution) for better bit distribution, at some cost in speed.";

    public const string Aq =
        "ADAPTIVE QUANTIZATION (NVENC)\n\n" +
        "Spatial AQ improves flat areas; temporal AQ improves static parts of the picture. Usually improves perceived quality.";

    public const string MaxBitrate =
        "MAXIMUM BITRATE (OPTIONAL)\n\n" +
        "Limits the bitrate (e.g. 12M) for devices or streaming with bandwidth limits. Leave empty for no limit.";

    public const string Gop =
        "KEYFRAME INTERVAL\n\n" +
        "Distance between keyframes, in frames (240) or seconds (10s). Shorter gives faster seeking; longer gives slightly smaller files. " +
        "Default: 10 seconds.";

    public const string SvtParams =
        "ADVANCED SVT-AV1 PARAMETERS\n\n" +
        "Extra key=value options for SVT-AV1, e.g. enable-overlays=1. CRF, preset, keyframes and scene detection are set by the controls above.";

    public const string AudioCopy =
        "AUDIO COPY\n\n" +
        "Keeps every audio track unchanged (AAC, AC-3, E-AC-3, DTS, TrueHD, FLAC, Opus…) with no quality loss. " +
        "Tracks the output container cannot store are converted to Opus.";

    public const string DeleteSource =
        "DELETE SOURCE AFTER ENCODING\n\n" +
        "The original video is deleted only after its AV1 file passes every check: the encoder finished successfully, the file exists, " +
        "is complete and readable, contains AV1 video, has the right duration and tracks, and decodes without errors. " +
        "If any check fails, the original is kept.\n\n" +
        "Copied non-video files are never deleted.";

    public const string Mirror =
        "COPY FOLDER STRUCTURE\n\n" +
        "When you add a folder and a destination is set, the destination becomes a full copy of the source: every sub-folder " +
        "(including empty ones) is created, videos are encoded with their original names, and all other files are copied and verified.";

    public const string Collision =
        "EXISTING DESTINATION FILES\n\n" +
        "Reuse if valid — keep an existing complete AV1 file (no re-encode); an invalid file is skipped and left untouched.\n" +
        "Ask — decide before encoding starts.\n" +
        "Skip — leave the existing file.\n" +
        "Add a number — write \"Movie (2).mkv\".\n" +
        "Replace — overwrite the existing file after the new one is verified (never the source).";

    public const string OutputContainer =
        "OUTPUT CONTAINER\n\n" +
        "The file format that holds the encoded AV1 video. MKV supports every audio and subtitle format; " +
        "MP4 plays on more devices and apps. The video is AV1 either way.\n\n" +
        "Default: MKV.";

    public const string Concurrency =
        "CONCURRENT JOBS\n\n" +
        "Number of files encoded at the same time. 1 is recommended: AV1 encoders already use every CPU thread.";
}
