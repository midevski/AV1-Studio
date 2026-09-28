using System.IO;
using AV1Studio.Models;
using AV1Studio.Util;

namespace AV1Studio.Services;

/// <param name="ReplacesSource">Output has the same path as the source ("Same filename" in the
/// source folder): the source is deleted only after verification, then the partial is renamed into place.</param>
/// <param name="ExistingOutput">The final file already exists and the policy says "reuse if valid":
/// it is verified instead of being re-created.</param>
public sealed record OutputPlan(
    string FinalPath,
    string PartialPath,
    string Extension,
    bool ReplacesSource,
    bool OverwriteExisting,
    string? SkipReason,
    bool ExistingOutput = false);

public static class OutputPlanner
{
    /// <summary>Temporary outputs are "Name.av1studio.partial.ext" — distinctive, so user files are never mistaken for ours.</summary>
    public const string PartialMarker = ".av1studio.partial";
    /// <summary>ab-av1's own temp prefix (encode writes here, then renames to -o).</summary>
    public const string AbAv1TempPrefix = ".tmp.ab-av1-encoding.";

    public static string ContainerExtension(AppSettings s, string sourcePath) => ContainerExtension(s.Container, sourcePath);

    public static string ContainerExtension(ContainerFormat container, string sourcePath) =>
        OutputContainers.Resolve(container, sourcePath).Extension;

    /// <summary>Destination folder for a file: destination root + the file's folder relative to the root the
    /// user selected (never flattened). Empty destination = next to the source.</summary>
    public static string OutputDirectory(AppSettings s, QueueItem item)
    {
        var srcDir = Path.GetDirectoryName(item.SourcePath)!;
        if (string.IsNullOrWhiteSpace(s.DestinationFolder)) return srcDir;
        var dest = s.DestinationFolder.Trim();
        if ((s.PreserveFolderStructure || item.FolderJobId != null) && !string.IsNullOrEmpty(item.SourceRoot))
        {
            var rel = Path.GetRelativePath(item.SourceRoot, srcDir);
            if (!rel.StartsWith("..") && !Path.IsPathRooted(rel) && rel != ".")
                return Path.Combine(dest, rel);
        }
        return dest;
    }

    public static NamingMode EffectiveNaming(AppSettings s) => s.Naming == NamingMode.Auto
        ? (string.IsNullOrWhiteSpace(s.DestinationFolder) ? NamingMode.Suffix : NamingMode.SameName)
        : s.Naming;

    public static string BaseName(AppSettings s, QueueItem item, double? crf, string? presetText = null)
    {
        var name = Path.GetFileNameWithoutExtension(item.SourcePath);
        string result = EffectiveNaming(s) switch
        {
            NamingMode.SameName => name,
            NamingMode.Template => (string.IsNullOrWhiteSpace(s.NameTemplate) ? "{name}_AV1" : s.NameTemplate)
                .Replace("{name}", name)
                .Replace("{crf}", crf is double c ? Fmt.Arg(c) : "auto")
                .Replace("{vmaf}", Fmt.Num(item.Search?.Vmaf, "0.#"))
                .Replace("{preset}", presetText ?? s.Preset?.ToString() ?? "default")
                .Replace("{date}", DateTime.Now.ToString("yyyyMMdd")),
            _ => name + (string.IsNullOrEmpty(s.Suffix) ? "_AV1" : s.Suffix),
        };
        foreach (var ch in Path.GetInvalidFileNameChars()) result = result.Replace(ch, '_');
        result = result.Trim().TrimEnd('.');
        return result.Length == 0 ? name + "_AV1" : result;
    }

    /// <summary>Plan an encoded video's output.</summary>
    /// <param name="policyOverride">Decision taken for this run when the policy is "Ask".</param>
    public static OutputPlan Plan(AppSettings s, QueueItem item, double? crf, string? ext = null, string? presetText = null,
        CollisionPolicy? policyOverride = null)
    {
        ext ??= ContainerExtension(s, item.SourcePath);
        var dir = OutputDirectory(s, item);
        var baseName = BaseName(s, item, crf, presetText);
        return Resolve(s, item, dir, baseName, ext, policyOverride);
    }

    /// <summary>Plan the copy of a non-video file (or of a video kept unchanged): same name, same relative folder.</summary>
    public static OutputPlan PlanCopy(AppSettings s, QueueItem item, CollisionPolicy? policyOverride = null)
    {
        var dir = OutputDirectory(s, item);
        var ext = Path.GetExtension(item.SourcePath).TrimStart('.');
        var baseName = Path.GetFileNameWithoutExtension(item.SourcePath);
        return Resolve(s, item, dir, baseName, ext, policyOverride);
    }

    private static OutputPlan Resolve(AppSettings s, QueueItem item, string dir, string baseName, string ext, CollisionPolicy? policyOverride)
    {
        string Name(string b) => ext.Length > 0 ? $"{b}.{ext}" : b;
        var final = Path.GetFullPath(Path.Combine(dir, Name(baseName)));
        var source = Path.GetFullPath(item.SourcePath);
        bool replaces = SamePath(final, source);
        bool overwrite = false, existing = false;
        string? skip = null;
        var policy = policyOverride ?? s.Collision;

        if (replaces)
        {
            if (item.Kind == ItemKind.Copy)
                skip = "The destination is the same file as the source; nothing to copy.";
            else if (!s.DeleteSourceAfterSuccess)
                skip = "Output would have the same path as the source. Enable \"Delete source after successful encoding\" " +
                       "to replace it after verification, or use a suffix / another destination folder.";
        }
        else if (File.Exists(final))
        {
            switch (policy)
            {
                case CollisionPolicy.Skip:
                case CollisionPolicy.Ask: // unresolved "Ask" never overwrites
                    skip = $"Destination file already exists: {final}";
                    break;
                case CollisionPolicy.ReuseIfValid:
                    existing = true;
                    break;
                case CollisionPolicy.AppendNumber:
                    for (int n = 2; ; n++)
                    {
                        var candidate = Path.Combine(dir, Name($"{baseName} ({n})"));
                        if (!File.Exists(candidate) && !SamePath(candidate, source)) { final = candidate; break; }
                    }
                    break;
                case CollisionPolicy.Overwrite:
                    overwrite = true;
                    break;
            }
        }

        var partialName = Path.GetFileNameWithoutExtension(final) + PartialMarker + (ext.Length > 0 ? "." + ext : "");
        var partial = Path.Combine(Path.GetDirectoryName(final)!, partialName);
        return new OutputPlan(final, partial, ext, replaces, overwrite, skip, existing);
    }

    public static string AbAv1TempFile(string partialPath) =>
        Path.Combine(Path.GetDirectoryName(partialPath)!, AbAv1TempPrefix + Path.GetFileName(partialPath));

    /// <summary>Normalised full path (resolves ".", "..", separators) for comparisons.</summary>
    public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsOurTemporaryFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(AbAv1TempPrefix, StringComparison.OrdinalIgnoreCase)
               || name.Contains(PartialMarker, StringComparison.OrdinalIgnoreCase);
    }
}
