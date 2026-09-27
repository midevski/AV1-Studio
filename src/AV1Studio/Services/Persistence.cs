using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AV1Studio.Models;

namespace AV1Studio.Services;

/// <summary>Crash-safe JSON persistence: write to a temp file, flush to disk, then atomically replace.</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly object WriteLock = new();

    public static T? Load<T>(string path) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                using var fs = File.OpenRead(candidate);
                var v = JsonSerializer.Deserialize<T>(fs, Options);
                if (v != null) return v;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read {Path.GetFileName(candidate)}: {ex.Message}");
            }
        }
        return null;
    }

    public static void Save<T>(string path, T value) =>
        SaveBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Options));

    public static void SaveBytes(string path, byte[] bytes)
    {
        lock (WriteLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
    }
}

public sealed class QueueState
{
    public int Version { get; set; } = 2;
    public List<QueueItem> Items { get; set; } = new();
    /// <summary>Settings snapshots referenced by QueueItem.ProfileId (shared, so thousands of jobs stay small).</summary>
    public Dictionary<string, AppSettings> Profiles { get; set; } = new();
    public List<FolderJob> Folders { get; set; } = new();
}

/// <summary>Analysis cache entry: CRF search results keyed by file identity, independent of the queue
/// (removing and re-adding a file, or moving it, does not cost another CRF search).</summary>
public sealed class CachedAnalysis
{
    public string SourcePath { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string? QuickHash { get; set; }
    public string Fingerprint { get; set; } = "";
    public CrfSearchResult Result { get; set; } = new();
    public List<CrfAttempt> Attempts { get; set; } = new();
    public double TargetVmaf { get; set; }
    public string? Parameters { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public sealed class AnalysisCache
{
    public List<CachedAnalysis> Entries { get; set; } = new();

    private readonly object _lock = new();

    public static AnalysisCache Load() => JsonFile.Load<AnalysisCache>(AppPaths.AnalysisCache) ?? new AnalysisCache();

    public void Save()
    {
        lock (_lock) JsonFile.Save(AppPaths.AnalysisCache, this);
    }

    public CachedAnalysis? Find(QueueItem item, string fingerprint)
    {
        lock (_lock)
        {
            // 1) same path, size and modification time
            var hit = Entries.LastOrDefault(e => e.Fingerprint == fingerprint && e.Size == item.SourceSize
                && e.ModifiedUtc == item.SourceModifiedUtc
                && string.Equals(e.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase));
            // 2) moved/renamed file: same size and same content hash
            hit ??= item.QuickHash is null ? null : Entries.LastOrDefault(e => e.Fingerprint == fingerprint
                && e.Size == item.SourceSize && e.QuickHash == item.QuickHash);
            return hit;
        }
    }

    public void Remove(string sourcePath)
    {
        lock (_lock) Entries.RemoveAll(e => string.Equals(e.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
    }

    public void Put(CachedAnalysis entry)
    {
        lock (_lock)
        {
            Entries.RemoveAll(e => e.Fingerprint == entry.Fingerprint &&
                string.Equals(e.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase));
            Entries.Add(entry);
            if (Entries.Count > 50000) Entries.RemoveRange(0, Entries.Count - 50000);
        }
    }
}

public static class FileIdentity
{
    /// <summary>Content fingerprint without reading whole multi-GB files: SHA-256 over the size plus
    /// the first and last 4 MiB. Used to recognise moved/renamed files, not for security.</summary>
    public static string QuickHash(string path)
    {
        const int chunk = 4 * 1024 * 1024;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(fs.Length));
        var buf = new byte[chunk];
        int n = fs.Read(buf, 0, chunk);
        sha.AppendData(buf, 0, n);
        if (fs.Length > chunk * 2L)
        {
            fs.Seek(-chunk, SeekOrigin.End);
            n = fs.Read(buf, 0, chunk);
            sha.AppendData(buf, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..32];
    }
}
