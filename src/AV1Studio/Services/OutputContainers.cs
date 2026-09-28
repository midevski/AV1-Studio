using System.IO;
using AV1Studio.Models;

namespace AV1Studio.Services;

/// <summary>One output container: file extension, FFmpeg muxer and muxer options. The video is AV1 in every case.</summary>
public sealed record OutputContainer(ContainerFormat Format, string Label, string Extension, string Muxer, IReadOnlyList<string> MuxerOptions)
{
    /// <summary>Muxer options in ab-av1 "--enc name=value" form.</summary>
    public IEnumerable<string> EncArgs => new[] { $"f={Muxer}" }
        .Concat(MuxerOptions.Chunk(2).Select(p => $"{p[0]}={p[1]}"));

    /// <summary>Muxer options as plain FFmpeg arguments (without "-f", which goes right before the output file).</summary>
    public IEnumerable<string> FfmpegArgs => MuxerOptions.Select((o, i) => i % 2 == 0 ? "-" + o : o);
}

/// <summary>The single definition of the output containers AV1 Studio can write, and how to recognise them.</summary>
public static class OutputContainers
{
    public static readonly OutputContainer Mkv = new(ContainerFormat.Mkv, "MKV", "mkv", "matroska", []);
    // faststart moves the index to the front, so MP4 files start playing immediately (and stream well).
    public static readonly OutputContainer Mp4 = new(ContainerFormat.Mp4, "MP4", "mp4", "mp4", ["movflags", "+faststart"]);
    /// <summary>Only for jobs queued by earlier versions.</summary>
    public static readonly OutputContainer WebM = new(ContainerFormat.WebM, "WebM", "webm", "webm", []);

    /// <summary>The choices offered in the interface.</summary>
    public static readonly IReadOnlyList<OutputContainer> Choices = [Mkv, Mp4];

    public const ContainerFormat Default = ContainerFormat.Mkv;

    /// <summary>The container for a job. The user's choice always wins; "same as source" only exists for jobs
    /// queued by earlier versions.</summary>
    public static OutputContainer Resolve(ContainerFormat format, string sourcePath) => format switch
    {
        ContainerFormat.Mp4 => Mp4,
        ContainerFormat.WebM => WebM,
        ContainerFormat.SameAsSource => Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => Mp4,
            ".webm" => WebM,
            _ => Mkv,
        },
        _ => Mkv,
    };

    public static OutputContainer FromExtension(string ext) => ext.TrimStart('.').ToLowerInvariant() switch
    {
        "mp4" or "m4v" => Mp4,
        "webm" => WebM,
        _ => Mkv,
    };

    /// <summary>Checks the container FFprobe actually found (not the file name). MP4 and QuickTime MOV share one
    /// demuxer, so MP4 is told apart by its brand.</summary>
    public static bool Matches(OutputContainer expected, ProbeInfo probe, out string actual)
    {
        var name = probe.FormatName ?? "";
        var brand = (probe.MajorBrand ?? "").Trim();
        actual = name.Length == 0 ? "unknown" : brand.Length > 0 ? $"{name} (brand {brand})" : name;
        if (expected.Muxer == "mp4")
            return name.Contains("mp4", StringComparison.OrdinalIgnoreCase) && !brand.Equals("qt", StringComparison.OrdinalIgnoreCase);
        // FFprobe reports Matroska and WebM as "matroska,webm"
        return name.Contains("matroska", StringComparison.OrdinalIgnoreCase) || name.Contains("webm", StringComparison.OrdinalIgnoreCase);
    }
}
