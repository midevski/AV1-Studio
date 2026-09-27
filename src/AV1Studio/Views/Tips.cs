namespace AV1Studio.Views;

/// <summary>Help texts shown as tooltips and by the "?" help icons. Basic controls get short explanations;
/// advanced controls get progressively more technical ones.</summary>
public static class Tips
{
    public const string ModeAbAv1 =
        "AB-AV1 — AUTOMATIC QUALITY\n\n" +
        "You choose a quality target (VMAF). AB-AV1 encodes short samples at several CRF values, measures their quality, " +
        "and picks the CRF that reaches your target with the smallest file. Then the full video is encoded with that CRF.\n\n" +
        "Best when you want AV1 Studio to choose the quality setting for each file.";

    public const string ModeManual =
        "MANUAL AV1 — FULL CONTROL\n\n" +
        "You choose the encoder, the quality value (CRF/CQ), the preset and every other option. AV1 Studio runs FFmpeg directly " +
        "with exactly those settings; AB-AV1 is not used.\n\n" +
        "Best when you know which settings you want, or need options AB-AV1 does not offer.";

    public const string Profile =
        "Profiles fill in the quality and preset for the selected mode as a starting point. You can change every value afterwards.\n\n" +
        "They are not universally optimal: the best settings depend on the source (grain, motion, resolution) and on your preferences.";

    public const string Vmaf =
        "VMAF TARGET\n\n" +
        "VMAF is a perceptual video-quality metric developed by Netflix (0–100). AB-AV1 uses VMAF measurements on samples to " +
        "automatically search for an appropriate CRF.\n\n" +
        "Higher target → higher measured similarity to the source, larger files. Lower target → smaller files, more visible loss.\n\n" +
        "VMAF is not a perfect representation of human perception, especially for grain, dark scenes and animation.\n\n" +
        "Default: 95.";

    public const string CrfSearch =
        "CRF SEARCH\n\n" +
        "AB-AV1 encodes a few short samples of the video at different CRF values and scores each one with VMAF, narrowing down " +
        "to the highest CRF that still meets the target. More samples or a thorough search can be more accurate but take longer.\n\n" +
        "Results are cached, so an unchanged file is never analysed twice with the same settings.";

    public const string Crf =
        "CRF (CONSTANT RATE FACTOR)\n\n" +
        "What it does: sets the quality target used during encoding. The encoder spends as many bits as each scene needs to reach it.\n\n" +
        "Lower values → higher visual quality and larger files. Higher values → lower quality and smaller files.\n\n" +
        "The optimal value depends heavily on the source content; there is no value that is right for every video. " +
        "Use Preview to compare. Hardware encoders call this CQ, ICQ or QP.";

    public const string Preset =
        "PRESET\n\n" +
        "What it does: controls how much computation the encoder spends searching for the most efficient way to compress each frame.\n\n" +
        "Slower presets generally take longer but can improve compression efficiency (smaller files at the same quality). " +
        "Faster presets reduce encoding time at the cost of potentially larger files for similar visual quality.\n\n" +
        "SVT-AV1: 0 (slowest) … 13 (fastest); 4–6 is a common range for archiving libraries. Encoding speed also depends on the CPU and the source.";

    public const string BitDepth =
        "10-BIT / 8-BIT\n\n" +
        "10-bit encoding provides a larger range of tonal values than 8-bit. It can reduce banding (visible steps in skies and gradients) " +
        "and is needed to keep HDR video.\n\n" +
        "Compatibility and performance can vary depending on the playback device; almost all AV1-capable players support 10-bit.\n\n" +
        "Default: 10-bit.";

    public const string Hardware =
        "HARDWARE ENCODING\n\n" +
        "Uses a dedicated media encoder available on supported GPUs or integrated graphics (NVIDIA NVENC, Intel Quick Sync, AMD AMF). " +
        "It is generally much faster and reduces CPU usage.\n\n" +
        "Available controls and compression efficiency depend on the hardware generation and the encoder implementation. " +
        "Hardware encoding is not automatically lower quality — results depend on the encoder, its settings and the source.\n\n" +
        "Only encoders that pass a real test encode on this PC can be selected.";

    public const string CpuUsage =
        "CPU USAGE\n\n" +
        "Controls how many logical processors the encoder may use and its Windows scheduling priority (applied to every encoder process).\n\n" +
        "Auto — uses almost all processors, keeping a few free for Windows and AV1 Studio (calculated from your CPU).\n" +
        "Maximum — all processors, normal priority. Fastest; the PC may feel sluggish.\n" +
        "Balanced — about ¾ of the processors, below-normal priority.\n" +
        "Low — about half, lowest priority: runs like a background task.\n" +
        "Custom — choose the number of processors, the priority and optionally exact processors (affinity).\n\n" +
        "There is no '% slider' because a percentage cannot reliably steer an encoder.";

    public const string CpuSearchVsEncode =
        "AB-AV1 runs in two stages: the CRF search (many short sample encodes plus VMAF scoring) and the final encode. " +
        "Each stage can use its own CPU setting — for example Low while analysing a library overnight, Maximum for the final encodes. " +
        "The limits apply to the real FFmpeg / encoder processes, not only to the small ab-av1 orchestration process.";

    public const string Priority =
        "PROCESS PRIORITY\n\n" +
        "Tells Windows how to share the CPU between the encoder and other programs.\n\n" +
        "Idle / Below normal — other programs always go first; the encoder still uses all spare CPU.\n" +
        "Normal — equal footing with your other programs.\n" +
        "Above normal / High — the encoder is preferred; the PC, audio or games may stutter. Use only on a dedicated encoding PC.\n\n" +
        "Realtime is never used: it can make Windows unresponsive.";

    public const string Affinity =
        "CPU AFFINITY (OPTIONAL)\n\n" +
        "Restricts the encoder to specific logical processors, e.g. \"0-7\" or \"4-15,20\". Leave empty to let the thread count decide.\n\n" +
        "Useful to keep certain cores free (for example for games or streaming). Numbers start at 0.";

    public const string GpuWorkload =
        "GPU WORKLOAD\n\n" +
        "A hardware encoder's utilisation cannot be set as a percentage, and it is different from normal GPU (3D) usage: the encoder is a " +
        "separate media engine. What you can control is how much work each frame gets: preset, quality, resolution, frame rate, " +
        "B-frames, lookahead, multipass and adaptive quantization. Slower settings generally improve efficiency and reduce speed.";

    public const string BFrames =
        "B-FRAMES\n\n" +
        "Frames predicted from both earlier and later frames. More B-frames usually improve compression, especially for slow motion, " +
        "at the cost of some encoding speed. Empty = encoder default.";

    public const string Lookahead =
        "LOOKAHEAD\n\n" +
        "The encoder analyses this many upcoming frames before deciding how to spend bits. More lookahead helps with scene changes and " +
        "fades and usually improves quality per bit, but uses more GPU memory and can reduce speed. Empty = encoder default.";

    public const string Multipass =
        "MULTIPASS (NVENC)\n\n" +
        "Runs an extra analysis pass per frame (quarter or full resolution) to distribute bits better. Improves quality/efficiency at " +
        "some cost in speed. Empty = encoder default.";

    public const string Aq =
        "ADAPTIVE QUANTIZATION (NVENC)\n\n" +
        "Spatial AQ spends more bits on flat areas where artefacts are visible; temporal AQ spends more bits on static parts that are " +
        "seen for longer. Often improves perceived quality; can lower objective metrics like PSNR.";

    public const string MaxBitrate =
        "MAXIMUM BITRATE (OPTIONAL)\n\n" +
        "Caps the bitrate while keeping constant-quality encoding (e.g. 12M). Useful for streaming devices with bandwidth limits. " +
        "When the cap is reached, quality drops in demanding scenes. Empty = no cap (recommended for archiving).";

    public const string Gop =
        "KEYFRAME INTERVAL (GOP)\n\n" +
        "Distance between full keyframes, in frames (240) or seconds (10s). Shorter → faster and more precise seeking, slightly larger files. " +
        "Longer → slightly smaller files, coarser seeking. Default: 10 seconds.";

    public const string SvtParams =
        "ADVANCED SVT-AV1 PARAMETERS\n\n" +
        "Extra key=value options passed to SVT-AV1 (-svtav1-params), e.g. enable-overlays=1 or variance-boost-strength=2. " +
        "For experienced users: invalid values make the encode fail (the error is shown). crf, preset, keyint and scd are set by the controls.";

    public const string AudioCopy =
        "AUDIO PASSTHROUGH (COPY)\n\n" +
        "Keeps every audio track bit-for-bit (AAC, AC-3, E-AC-3, DTS, TrueHD, FLAC, Opus…): no quality loss and no extra encoding time. " +
        "Only tracks the chosen container cannot store are converted (to Opus). Recommended default.";

    public const string DeleteSource =
        "DELETE SOURCE AFTER SUCCESSFUL ENCODING\n\n" +
        "A source video is deleted only after its AV1 output passed every check: exit code 0, output exists and is not empty, FFprobe reads it, " +
        "it has a valid AV1 video stream, its duration matches, expected tracks are present, a full decode succeeds (if enabled) and the " +
        "file has been renamed to its final name. If anything fails, the source is kept.\n\n" +
        "Copied non-video files are never deleted.";

    public const string Mirror =
        "MIRROR FOLDER TREE\n\n" +
        "When you add a folder and a destination is set, the destination becomes a complete replica of the source: every sub-folder " +
        "(including empty ones) is recreated, videos are encoded to the same relative path with their original name, and every other file " +
        "(images, subtitles, documents…) is copied unchanged and verified.";

    public const string Collision =
        "WHEN THE DESTINATION FILE ALREADY EXISTS\n\n" +
        "Reuse if valid (default) — the existing file is fully verified; if valid it is kept (no re-encode, lets you resume), otherwise the file is skipped and reported.\n" +
        "Skip — leave the existing file and skip.\n" +
        "Ask — AV1 Studio asks before starting when existing files are found.\n" +
        "Add a number — write \"Movie (2).mkv\".\n" +
        "Replace — overwrite existing destination files (never the source).";

    public const string Concurrency =
        "CONCURRENT JOBS\n\n" +
        "Files processed at the same time. 1 is recommended: AV1 encoders already use all cores, and more jobs multiply RAM use, disk " +
        "load and the temporary space needed (sources are only deleted after verification).";
}
