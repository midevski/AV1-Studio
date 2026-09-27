namespace AV1Studio.Models;

/// <summary>Video files are encoded; every other file of a mirrored folder is copied unchanged.</summary>
public enum ItemKind { Video, Copy }

public enum ItemStatus
{
    Waiting,
    Analyzing,
    CrfFound,
    Ready,
    Encoding,
    Verifying,
    Completed,
    Skipped,
    Failed,
    /// <summary>Encode completed and verified, and the source was deleted.</summary>
    Deleted,
    /// <summary>Cancelled by the user; source untouched, partial output removed.</summary>
    Cancelled,
}

public static class ItemStatusExtensions
{
    public static string Label(this ItemStatus s) => s switch
    {
        ItemStatus.Waiting => "Queued",
        ItemStatus.CrfFound => "CRF Found",
        ItemStatus.Deleted => "Completed (source deleted)",
        _ => s.ToString(),
    };

    /// <summary>States that mean a child process is (or was, before a crash) running.</summary>
    public static bool IsActive(this ItemStatus s) =>
        s is ItemStatus.Analyzing or ItemStatus.Encoding or ItemStatus.Verifying;

    public static bool IsFinal(this ItemStatus s) =>
        s is ItemStatus.Completed or ItemStatus.Deleted or ItemStatus.Skipped or ItemStatus.Cancelled;

    public static bool IsDone(this ItemStatus s) => s is ItemStatus.Completed or ItemStatus.Deleted;
}
