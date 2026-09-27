using System.IO;
using AV1Studio.Models;
using AV1Studio.Util;

namespace AV1Studio.Services;

/// <summary>History of finished encodes, so the Library keeps its overview after items are cleared from the queue.</summary>
public sealed class LibraryRecord
{
    public Guid Id { get; set; }
    public string SourcePath { get; set; } = "";
    public string? OutputPath { get; set; }
    public long OriginalSize { get; set; }
    public long? EncodedSize { get; set; }
    public string? SourceCodec { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public string? Hdr { get; set; }
    public EncodeMode Mode { get; set; }
    public string? Encoder { get; set; }
    public ItemStatus Status { get; set; }
    public bool SourceDeleted { get; set; }
    public DateTime CompletedUtc { get; set; }
}

public sealed class LibraryStore
{
    public static string FilePath => Path.Combine(AppPaths.Root, "library.json");
    public List<LibraryRecord> Records { get; set; } = new();

    public static LibraryStore Load() => JsonFile.Load<LibraryStore>(FilePath) ?? new LibraryStore();

    public void Save()
    {
        try { JsonFile.Save(FilePath, this); }
        catch (Exception ex) { Log.Warn($"Could not save library history: {ex.Message}"); }
    }

    /// <summary>Record a finished (completed / source-deleted) item. Returns true when something changed.</summary>
    public bool Upsert(QueueItem i)
    {
        if (!i.Status.IsDone()) return false;
        var r = Records.FirstOrDefault(x => x.Id == i.Id);
        if (r is null) { r = new LibraryRecord { Id = i.Id }; Records.Add(r); }
        var v = i.Probe?.MainVideo;
        r.SourcePath = i.SourcePath;
        r.OutputPath = i.OutputPath;
        r.OriginalSize = i.SourceSize;
        r.EncodedSize = i.ActualOutputSize;
        r.SourceCodec = v?.Codec;
        r.Width = v?.Width;
        r.Height = v?.Height;
        r.DurationSeconds = i.Probe?.DurationSeconds;
        r.Hdr = i.Probe?.HdrFormat;
        r.Mode = i.Mode;
        r.Encoder = i.EncoderText;
        r.Status = i.Status;
        r.SourceDeleted = i.SourceDeleted;
        r.CompletedUtc = i.CompletedAtUtc ?? DateTime.UtcNow;
        return true;
    }
}

/// <summary>One row of the Library page (a queue item or a history record).</summary>
public sealed class LibraryEntry
{
    public string FileName { get; init; } = "";
    public string Path { get; init; } = "";
    public long OriginalSize { get; init; }
    public long? EncodedSize { get; init; }
    public string Codec { get; init; } = "";
    public int? Height { get; init; }
    public string Resolution { get; init; } = "";
    public double? DurationSeconds { get; init; }
    public string Hdr { get; init; } = "";
    public string Status { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Encoder { get; init; } = "";
    public bool IsEncoded { get; init; }
    public bool IsFailed { get; init; }
    public bool IsAv1 { get; init; }
    public bool IsHdr => Hdr is not ("" or "SDR");

    public long? Saved => EncodedSize is long e ? OriginalSize - e : null;
    public string OriginalText => Fmt.Bytes(OriginalSize);
    public string EncodedText => Fmt.Bytes(EncodedSize);
    public string SavedText => Saved is long s ? $"{Fmt.Bytes(s)} ({100.0 * s / Math.Max(1, OriginalSize):0}%)" : "";
    public double? Ratio => EncodedSize is long e && e > 0 ? (double)OriginalSize / e : null;
    public string RatioText => Ratio is double r ? $"{r:0.00}:1" : "";
    public string DurationText => Fmt.Duration(DurationSeconds);

    /// <summary>Resolution class used by the 1080p / 1440p / 4K filters.</summary>
    public string ResolutionClass => Height switch
    {
        >= 1800 => "4K",
        >= 1300 => "1440p",
        >= 900 => "1080p",
        null => "",
        _ => "SD/720p",
    };
}
