namespace AV1Studio.Services;

public sealed record ErrorExplanation(string Why, string Fix);

/// <summary>Turns technical failure messages into "what happened / why / what you can do".
/// The raw message is always kept too (log + details pane), so nothing is hidden.</summary>
public static class ErrorExplainer
{
    private static readonly (string[] Patterns, string Why, string Fix)[] Rules =
    [
        (["not found (moved or deleted", "Source file not found"],
            "The file was moved, renamed or deleted, or the drive holding it is disconnected.",
            "Check that the file still exists and the drive is connected, then remove it from the queue and add it again."),
        (["empty (0 bytes)"],
            "The source file has no content — usually an incomplete download or copy.",
            "Re-copy or re-download the file."),
        (["No video stream"],
            "FFprobe found no video track in this file (audio-only file, image, or unsupported container).",
            "Check the file in a media player. Only files with a video track can be encoded."),
        (["Invalid or corrupted", "Invalid data found", "ffprobe failed", "moov atom not found", "EBML header parsing failed"],
            "The file could not be read: it is damaged, incomplete, or in a format FFmpeg does not understand.",
            "Try playing the file. If it is damaged, re-download it or remux it (e.g. with MKVToolNix), then retry."),
        (["decode error", "Error while decoding", "corrupt", "Invalid NAL", "error concealment"],
            "FFmpeg hit damaged data in the source while decoding (stopped by the 'fail fast' safety option).",
            "The source is probably corrupted. Verify it plays correctly; re-download it, or disable 'Stop at first FFmpeg error' if you accept a damaged result."),
        (["No capable devices", "OpenEncodeSessionEx failed", "Failed to create", "hardware device", "No NVENC capable", "MFX", "unsupported by the QSV runtime", "DLL amfrt64"],
            "The selected hardware encoder could not start: this GPU or driver does not support AV1 hardware encoding, or the GPU is busy.",
            "Choose SVT-AV1 (CPU) or another encoder, update the GPU driver, or close applications that are using the GPU encoder."),
        (["Unknown encoder", "Encoder not found", "Unrecognized option", "Option not found", "Error setting option", "Invalid argument", "Error parsing", "Undefined constant"],
            "FFmpeg rejected a parameter: an encoder is missing from this FFmpeg build, or a custom/advanced parameter is invalid.",
            "Check the custom SVT-AV1 / FFmpeg parameters in the Expert section, or use a full FFmpeg build (Settings › Tools › Download)."),
        (["No space left", "Insufficient disk space", "not enough space", "disk full"],
            "The destination drive ran out of space.",
            "Free up space, choose another destination drive, or enable source deletion so space is reclaimed after each verified file."),
        (["Permission denied", "Access to the path", "UnauthorizedAccess", "being used by another process", "access denied"],
            "Windows refused access: the file or folder is read-only, protected, or open in another program.",
            "Close programs using the file (players, antivirus scans), check folder permissions, or choose another destination folder."),
        (["already exists", "appeared during encoding"],
            "A file with the output name already exists; it is never overwritten without your permission.",
            "Change the naming settings (suffix/template), pick another destination, or allow overwriting existing outputs in Settings › Storage."),
        (["Verification failed: Duration"],
            "The encoded file's length does not match the source — the encode stopped early or the source has timing problems.",
            "Check the source plays to the end. Remove filters that change timing (fps, custom). The source was kept."),
        (["Verification failed: Audio tracks", "Verification failed: Subtitle tracks"],
            "Some audio or subtitle tracks did not make it into the output (often a container limitation).",
            "Use the MKV container, or change the audio/subtitle settings. The source was kept."),
        (["Verification failed: Full decode", "Verification failed: FFprobe", "Verification failed: Video"],
            "The encoded file is unreadable or damaged.",
            "Retry the file. If it fails again, try another preset or encoder and check the Logs page. The source was kept."),
        (["Verification failed: Source unchanged"],
            "The source file changed while it was being encoded (modified, replaced or moved).",
            "Make sure nothing else writes to the library during encoding, then retry."),
        (["suitable crf"],
            "ab-av1 found no CRF that reaches the target VMAF while staying under the size limit.",
            "Lower the target VMAF, raise 'Max encoded size %', or use Manual AV1 mode for this file."),
        (["timed out"],
            "A tool did not respond in time.",
            "Retry; if it persists, check the file and the Logs page."),
    ];

    public static ErrorExplanation Explain(string? message)
    {
        var m = message ?? "";
        foreach (var (patterns, why, fix) in Rules)
            if (patterns.Any(p => m.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return new ErrorExplanation(why, fix);
        return new ErrorExplanation(
            "The encoding tool reported an error that the application does not recognise.",
            "Open the Logs page (and enable 'Show tool output') for the full FFmpeg / ab-av1 output. The exact command is in the Queue details and can be copied to reproduce the problem.");
    }
}
