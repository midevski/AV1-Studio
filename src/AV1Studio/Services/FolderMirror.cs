using System.IO;
using AV1Studio.Models;

namespace AV1Studio.Services;

/// <summary>A folder the user added with a destination set: the destination becomes a structural replica of
/// the source tree (every sub-folder, including empty ones; videos encoded, other files copied unchanged).</summary>
public sealed class FolderJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceRoot { get; set; } = "";
    public string DestinationRoot { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? TreeCreatedUtc { get; set; }
    public int DirectoryCount { get; set; }
    public bool Cancelled { get; set; }

    public string Name => Path.GetFileName(SourceRoot.TrimEnd('\\')) is { Length: > 0 } n ? n : SourceRoot;
}

public sealed class TreeScan
{
    public required string Root { get; init; }
    public List<(string Path, ItemKind Kind)> Files { get; } = new();
    public List<string> Directories { get; } = new(); // relative paths
}

public static class FolderMirror
{
    private static EnumerationOptions Options(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        IgnoreInaccessible = true,
        // Windows system files (Thumbs.db, desktop.ini) and hidden items are not part of the library.
        AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <summary>Walk the whole tree. Files with a configured video extension become encode jobs; every other file
    /// becomes a copy job. The destination (if inside the source) and our own temporary files are excluded.</summary>
    public static TreeScan Scan(string root, AppSettings s, CancellationToken ct)
    {
        root = OutputPlanner.Normalize(root);
        var videoExts = FileScanner.ParseExtensions(s.Extensions);
        string? dest = string.IsNullOrWhiteSpace(s.DestinationFolder) ? null : OutputPlanner.Normalize(s.DestinationFolder) + "\\";
        bool Excluded(string p) => dest != null && (p + "\\").StartsWith(dest, StringComparison.OrdinalIgnoreCase);

        var scan = new TreeScan { Root = root };
        foreach (var d in Directory.EnumerateDirectories(root, "*", Options(true)))
        {
            ct.ThrowIfCancellationRequested();
            if (Excluded(d)) continue;
            scan.Directories.Add(Path.GetRelativePath(root, d));
        }
        foreach (var f in Directory.EnumerateFiles(root, "*", Options(true)))
        {
            ct.ThrowIfCancellationRequested();
            if (Excluded(f) || OutputPlanner.IsOurTemporaryFile(f)) continue;
            var kind = videoExts.Contains(Path.GetExtension(f).ToLowerInvariant()) ? ItemKind.Video : ItemKind.Copy;
            scan.Files.Add((f, kind));
        }
        return scan;
    }

    /// <summary>Create a folder job and its queue items (videos to encode, other files to copy) from a scan.
    /// Paths are normalised and files already known are skipped, so the same file is never queued twice.</summary>
    public static (FolderJob Job, List<QueueItem> Items) CreateJob(TreeScan scan, AppSettings s, EncodeMode mode, string? profileId,
        ISet<string>? alreadyQueued = null)
    {
        var job = new FolderJob { SourceRoot = scan.Root, DestinationRoot = s.DestinationFolder.Trim() };
        var items = new List<QueueItem>();
        foreach (var (path, kind) in scan.Files)
        {
            var norm = OutputPlanner.Normalize(path);
            if (alreadyQueued != null && !alreadyQueued.Add(norm)) continue;
            FileInfo fi;
            try { fi = new FileInfo(norm); } catch { continue; }
            items.Add(new QueueItem
            {
                SourcePath = norm, SourceRoot = scan.Root, SourceSize = fi.Length, SourceModifiedUtc = fi.LastWriteTimeUtc,
                Kind = kind, Mode = mode, ProfileId = profileId, FolderJobId = job.Id,
            });
        }
        // Videos first inside each folder, folders in natural order: predictable processing and display.
        items = items.OrderBy(i => Path.GetDirectoryName(i.RelativePath), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => i.Kind).ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        return (job, items);
    }

    /// <summary>Recreate every sub-folder of the source (including empty ones) under the destination. Idempotent.</summary>
    public static int EnsureDirectories(FolderJob job, CancellationToken ct)
    {
        if (!Directory.Exists(job.SourceRoot)) return 0;
        Directory.CreateDirectory(job.DestinationRoot);
        var dest = OutputPlanner.Normalize(job.DestinationRoot) + "\\";
        int count = 0;
        foreach (var d in Directory.EnumerateDirectories(job.SourceRoot, "*", Options(true)))
        {
            ct.ThrowIfCancellationRequested();
            if ((OutputPlanner.Normalize(d) + "\\").StartsWith(dest, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(job.DestinationRoot, Path.GetRelativePath(job.SourceRoot, d));
            Directory.CreateDirectory(target);
            count++;
        }
        return count;
    }
}
