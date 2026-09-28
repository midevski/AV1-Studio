using System.Globalization;
using System.Text.Json;
using AV1Studio.Models;

namespace AV1Studio.Services;

public sealed class ProbeException(string message) : Exception(message);

public static class FfprobeService
{
    public static async Task<ProbeInfo> ProbeAsync(string ffprobe, string file, CancellationToken ct = default)
    {
        var (code, stdout, stderr) = await ChildProcess.RunCaptureAsync(ffprobe,
        [
            "-v", "error", "-hide_banner",
            "-print_format", "json",
            "-show_format", "-show_streams", "-show_chapters",
            "-i", file,
        ], ct, TimeSpan.FromMinutes(3));

        if (code != 0)
            throw new ProbeException($"ffprobe failed (exit {code}): {FirstLine(stderr) ?? "unknown error"}");
        ProbeInfo info;
        try
        {
            info = Parse(stdout);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ProbeException($"ffprobe returned unreadable output: {ex.Message}");
        }

        // HDR10 static metadata is often only in the first frame's side data (not the container).
        var v = info.MainVideo;
        if (v is { ColorTransfer: "smpte2084" } && (v.MasteringDisplay is null || v.ContentLight is null))
        {
            try
            {
                var (fc, fout, _) = await ChildProcess.RunCaptureAsync(ffprobe,
                [
                    "-v", "error", "-hide_banner", "-print_format", "json", "-select_streams", $"v:{v.TypeIndex}",
                    "-read_intervals", "%+#1", "-show_frames", "-show_entries", "frame=side_data_list", "-i", file,
                ], ct, TimeSpan.FromMinutes(1));
                if (fc == 0)
                {
                    using var doc = JsonDocument.Parse(fout);
                    if (doc.RootElement.TryGetProperty("frames", out var frames))
                        foreach (var f in frames.EnumerateArray()) ApplySideData(v, f);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* optional metadata */ }
        }
        return info;
    }

    /// <summary>Read HDR mastering display / content light / Dolby Vision side data (stream or frame level).</summary>
    internal static void ApplySideData(StreamInfo v, JsonElement owner)
    {
        if (!owner.TryGetProperty("side_data_list", out var list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (var sd in list.EnumerateArray())
        {
            var type = Str(sd, "side_data_type") ?? "";
            if (type.StartsWith("Mastering display", StringComparison.OrdinalIgnoreCase) && v.MasteringDisplay is null)
            {
                double? F(string k) => Rational(Str(sd, k)) ?? Dbl(sd, k);
                if (F("green_x") is double gx && F("green_y") is double gy && F("blue_x") is double bx && F("blue_y") is double by
                    && F("red_x") is double rx && F("red_y") is double ry && F("white_point_x") is double wx && F("white_point_y") is double wy
                    && F("max_luminance") is double maxL && F("min_luminance") is double minL)
                {
                    string P(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
                    v.MasteringDisplay = $"G({P(gx)},{P(gy)})B({P(bx)},{P(by)})R({P(rx)},{P(ry)})WP({P(wx)},{P(wy)})L({P(maxL)},{P(minL)})";
                }
            }
            else if (type.StartsWith("Content light", StringComparison.OrdinalIgnoreCase) && v.ContentLight is null)
            {
                if (Int(sd, "max_content") is int mc && Int(sd, "max_average") is int ma) v.ContentLight = $"{mc},{ma}";
            }
            else if (type.Contains("DOVI", StringComparison.OrdinalIgnoreCase) || type.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
            {
                v.DolbyVision = true;
            }
        }
    }

    internal static ProbeInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = new ProbeInfo();

        if (root.TryGetProperty("format", out var fmt))
        {
            info.DurationSeconds = Dbl(fmt, "duration");
            info.SizeBytes = (long?)Dbl(fmt, "size");
            info.FormatName = Str(fmt, "format_name");
            if (fmt.TryGetProperty("tags", out var tags))
            {
                info.Title = Tag(tags, "title");
                info.MajorBrand = Tag(tags, "major_brand");
            }
        }
        if (root.TryGetProperty("chapters", out var ch) && ch.ValueKind == JsonValueKind.Array)
            info.ChapterCount = ch.GetArrayLength();

        var typeCounters = new Dictionary<string, int>();
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type") ?? "unknown";
                typeCounters.TryGetValue(type, out int ti);
                typeCounters[type] = ti + 1;

                var si = new StreamInfo
                {
                    Index = s.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0,
                    TypeIndex = ti,
                    Type = type,
                    Codec = Str(s, "codec_name"),
                    Width = Int(s, "width"),
                    Height = Int(s, "height"),
                    PixFmt = Str(s, "pix_fmt"),
                    Channels = Int(s, "channels"),
                    ChannelLayout = Str(s, "channel_layout"),
                    FrameRate = Rational(Str(s, "avg_frame_rate")) ?? Rational(Str(s, "r_frame_rate")),
                    ColorRange = Str(s, "color_range"),
                    ColorPrimaries = Str(s, "color_primaries"),
                    ColorTransfer = Str(s, "color_transfer"),
                    ColorSpace = Str(s, "color_space"),
                    BitsPerSample = Int(s, "bits_per_raw_sample"),
                };
                if (type == "video") ApplySideData(si, s);
                if (s.TryGetProperty("tags", out var tags))
                {
                    si.Language = Tag(tags, "language");
                    si.Title = Tag(tags, "title");
                    // Matroska often stores the duration only as a stream tag.
                    if (info.DurationSeconds is null && type == "video" && Tag(tags, "DURATION") is string d
                        && TimeSpan.TryParse(d.Length > 16 ? d[..16] : d, CultureInfo.InvariantCulture, out var ts))
                        info.DurationSeconds = ts.TotalSeconds;
                }
                if (s.TryGetProperty("disposition", out var disp))
                {
                    si.IsAttachedPic = Int(disp, "attached_pic") == 1;
                    si.IsDefault = Int(disp, "default") == 1;
                    si.IsForced = Int(disp, "forced") == 1;
                }
                info.Streams.Add(si);
            }
        }
        return info;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i)) return i;
        return null;
    }

    private static double? Dbl(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static string? Tag(JsonElement tags, string name)
    {
        foreach (var p in tags.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }

    private static double? Rational(string? r)
    {
        if (string.IsNullOrEmpty(r)) return null;
        var parts = r.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
            return n / d;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && x > 0 ? x : null;
    }

    private static string? FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
