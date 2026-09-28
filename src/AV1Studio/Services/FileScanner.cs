using System.IO;
using AV1Studio.Models;

namespace AV1Studio.Services;

public static class FileScanner
{
    public static HashSet<string> ParseExtensions(string list) => MediaTypes.Parse(list);

    /// <summary>Enumerate video files. Skips our own temporary/partial outputs and anything inside
    /// the destination folder (so encoded results are never picked up as new sources).</summary>
    public static IEnumerable<(string Path, string Root)> Scan(IEnumerable<string> inputs, AppSettings s, CancellationToken ct)
    {
        var exts = ParseExtensions(s.Extensions);
        string? dest = string.IsNullOrWhiteSpace(s.DestinationFolder) ? null
            : Path.GetFullPath(s.DestinationFolder).TrimEnd('\\') + "\\";

        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(input))
            {
                // Explicitly dropped files are accepted even with an unlisted extension.
                if (!OutputPlanner.IsOurTemporaryFile(input))
                    yield return (Path.GetFullPath(input), Path.GetDirectoryName(Path.GetFullPath(input))!);
                continue;
            }
            if (!Directory.Exists(input)) continue;

            var root = Path.GetFullPath(input);
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = s.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            foreach (var f in Directory.EnumerateFiles(root, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (!exts.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                if (OutputPlanner.IsOurTemporaryFile(f)) continue;
                if (dest != null && f.StartsWith(dest, StringComparison.OrdinalIgnoreCase)
                    && !root.StartsWith(dest, StringComparison.OrdinalIgnoreCase)) continue;
                yield return (f, root);
            }
        }
    }
}
