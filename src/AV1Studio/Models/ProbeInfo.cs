namespace AV1Studio.Models;

/// <summary>Subset of ffprobe output that the application needs.</summary>
public sealed class ProbeInfo
{
    public double? DurationSeconds { get; set; }
    public long? SizeBytes { get; set; }
    public string? FormatName { get; set; }
    public string? Title { get; set; }
    public int ChapterCount { get; set; }
    public List<StreamInfo> Streams { get; set; } = new();

    /// <summary>The main video stream: first video stream that is not cover art.</summary>
    public StreamInfo? MainVideo =>
        Streams.FirstOrDefault(s => s.Type == "video" && !s.IsAttachedPic)
        ?? Streams.FirstOrDefault(s => s.Type == "video");

    public IEnumerable<StreamInfo> Audio => Streams.Where(s => s.Type == "audio");
    public IEnumerable<StreamInfo> Subtitles => Streams.Where(s => s.Type == "subtitle");
    public IEnumerable<StreamInfo> Attachments => Streams.Where(s => s.Type == "attachment");
    public IEnumerable<StreamInfo> Data => Streams.Where(s => s.Type == "data");

    /// <summary>SDR / HDR10 / HLG / Dolby Vision, from the main video's transfer characteristics.</summary>
    public string HdrFormat
    {
        get
        {
            var v = MainVideo;
            if (v is null) return "";
            if (v.DolbyVision) return v.ColorTransfer == "smpte2084" ? "Dolby Vision (HDR10 base)" : "Dolby Vision";
            return v.ColorTransfer switch
            {
                "smpte2084" => v.ContentLight != null || v.MasteringDisplay != null ? "HDR10" : "HDR (PQ)",
                "arib-std-b67" => "HLG",
                _ => "SDR",
            };
        }
    }

    public bool IsHdr => HdrFormat is not ("SDR" or "");

    public int? BitDepth
    {
        get
        {
            var v = MainVideo;
            if (v is null) return null;
            if (v.BitsPerSample is int b && b > 0) return b;
            var pf = v.PixFmt ?? "";
            if (pf.Contains("12")) return 12;
            if (pf.Contains("10") || pf.StartsWith("p010")) return 10;
            return pf.Length > 0 ? 8 : null;
        }
    }
}

public sealed class StreamInfo
{
    /// <summary>Absolute ffprobe stream index.</summary>
    public int Index { get; set; }
    /// <summary>Index among streams of the same type (ffmpeg "0:a:N" specifier).</summary>
    public int TypeIndex { get; set; }
    public string Type { get; set; } = "";
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public int? Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? PixFmt { get; set; }
    public double? FrameRate { get; set; }
    public bool IsAttachedPic { get; set; }
    public string? ColorRange { get; set; }
    public string? ColorPrimaries { get; set; }
    public string? ColorTransfer { get; set; }
    public string? ColorSpace { get; set; }
    public int? BitsPerSample { get; set; }
    /// <summary>SVT-AV1 style "G(x,y)B(x,y)R(x,y)WP(x,y)L(max,min)" when the source carries it.</summary>
    public string? MasteringDisplay { get; set; }
    /// <summary>"MaxCLL,MaxFALL" when the source carries it.</summary>
    public string? ContentLight { get; set; }
    public bool DolbyVision { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }

    public string Describe()
    {
        var parts = new List<string> { $"#{TypeIndex}", Codec ?? "?" };
        if (!string.IsNullOrEmpty(Language)) parts.Add(Language!);
        if (Channels is int c) parts.Add(c switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => $"{c}ch" });
        if (!string.IsNullOrEmpty(Title)) parts.Add($"\"{Title}\"");
        if (IsDefault) parts.Add("default");
        if (IsForced) parts.Add("forced");
        return string.Join(" · ", parts);
    }

    /// <summary>Bitmap subtitle formats cannot be converted to text formats (mov_text/webvtt).</summary>
    public bool IsBitmapSubtitle => Codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub";
}
