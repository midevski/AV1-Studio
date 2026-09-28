using System.IO;

namespace AV1Studio.Services;

/// <summary>The single definition of file types AV1 Studio treats as video. Used by the file dialog, the folder
/// scanner and folder mirroring. The extension only selects candidates: FFprobe decides whether a file really
/// contains video (files without a video stream are skipped, or copied unchanged in a mirrored folder).</summary>
public static class MediaTypes
{
    public static readonly IReadOnlyList<string> DefaultVideoExtensions =
    [
        "mkv", "mp4", "m4v", "mov", "webm", "avi", "wmv", "ts", "m2ts", "mts", "flv", "mpg", "mpeg", "vob", "ogv", "3gp", "divx",
    ];

    public static string DefaultExtensionList => string.Join(",", DefaultVideoExtensions);

    /// <summary>"mkv, .MP4;*.mov" → {".mkv", ".mp4", ".mov"}.</summary>
    public static HashSet<string> Parse(string? list) =>
        (list ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => "." + e.TrimStart('*', '.').ToLowerInvariant())
            .Where(e => e.Length > 1)
            .ToHashSet();

    /// <summary>Adds default extensions missing from a user list (keeps the user's own additions).</summary>
    public static string WithDefaults(string? list)
    {
        var have = Parse(list);
        var missing = DefaultVideoExtensions.Where(e => !have.Contains("." + e)).ToList();
        if (missing.Count == 0) return list ?? DefaultExtensionList;
        return string.IsNullOrWhiteSpace(list) ? DefaultExtensionList : list.TrimEnd(',', ' ') + "," + string.Join(",", missing);
    }

    public static bool IsVideoExtension(string path, ISet<string> extensions) =>
        extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
