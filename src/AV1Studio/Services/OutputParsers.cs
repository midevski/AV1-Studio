using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AV1Studio.Models;

namespace AV1Studio.Services;

public abstract record SearchEvent;
public sealed record AttemptEvent(CrfAttempt Attempt) : SearchEvent;
public sealed record DoneEvent(CrfSearchResult Result) : SearchEvent;
public sealed record SearchErrorEvent(string Message) : SearchEvent;

/// <summary>Parses `ab-av1 crf-search --stdout-format json` NDJSON (stdout-format-json.md),
/// with a fallback for the human format of older ab-av1 versions.</summary>
public static class CrfSearchParser
{
    private static readonly Regex HumanResult = new(
        @"crf\s+(?<crf>[\d.]+)\s+VMAF\s+(?<vmaf>[\d.]+).*?\((?<pct>[\d.]+)%\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SearchEvent? ParseStdoutLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return null;
        if (line.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = r.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "crf-search-error":
                        return new SearchErrorEvent(r.TryGetProperty("message", out var m) ? m.GetString() ?? "crf-search failed" : "crf-search failed");
                    case "crf-search-done":
                        return new DoneEvent(new CrfSearchResult
                        {
                            Crf = Num(r, "crf") ?? 0,
                            Vmaf = Num(r, "vmaf"),
                            PredictedSize = (long?)Num(r, "predicted_encode_size"),
                            PredictedPercent = Num(r, "predicted_encode_percent"),
                            PredictedSeconds = Num(r, "predicted_encode_seconds"),
                            FromCache = r.TryGetProperty("from_cache", out var fc) && fc.ValueKind == JsonValueKind.True,
                        });
                    case "sample-encode-done":
                    case null when r.TryGetProperty("crf", out _):
                        return new AttemptEvent(new CrfAttempt
                        {
                            Crf = Num(r, "crf") ?? 0,
                            Vmaf = Num(r, "vmaf"),
                            PredictedSize = (long?)Num(r, "predicted_encode_size"),
                            PredictedPercent = Num(r, "predicted_encode_percent"),
                            PredictedSeconds = Num(r, "predicted_encode_seconds"),
                            FromCache = r.TryGetProperty("from_cache", out var fc2) && fc2.ValueKind == JsonValueKind.True,
                        });
                    default:
                        return null; // unknown message kinds are ignored, as the docs require
                }
            }
            catch (JsonException) { return null; }
        }

        // Legacy human output: "crf 31 VMAF 95.20 predicted video stream size 5.8 GiB (30%) taking 2 hours"
        var h = HumanResult.Match(line);
        if (h.Success)
        {
            return new DoneEvent(new CrfSearchResult
            {
                Crf = double.Parse(h.Groups["crf"].Value, CultureInfo.InvariantCulture),
                Vmaf = double.Parse(h.Groups["vmaf"].Value, CultureInfo.InvariantCulture),
                PredictedPercent = double.Parse(h.Groups["pct"].Value, CultureInfo.InvariantCulture),
            });
        }
        return null;
    }

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>Interprets ab-av1's stderr (env_logger info lines are emitted when stderr is not a terminal).</summary>
public static class AbAv1StderrParser
{
    private static readonly Regex Sample = new(@"encoding sample (\d+)/(\d+) crf ([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex Pct = new(@"\]\s*(\d+)%, ([\d.]+) fps, eta (.+)$", RegexOptions.Compiled);
    private static readonly Regex LogPrefix = new(@"^\[[^\]]*\]\s*", RegexOptions.Compiled);

    public static string? Activity(string line)
    {
        var m = Sample.Match(line);
        if (m.Success) return $"Testing CRF {m.Groups[3].Value} · sample {m.Groups[1].Value}/{m.Groups[2].Value}";
        return null;
    }

    public static string? ErrorMessage(string line)
    {
        var t = line.Trim();
        return t.StartsWith("Error:", StringComparison.Ordinal) ? t[6..].Trim() : null;
    }

    public static string StripPrefix(string line) => LogPrefix.Replace(line, "");
}

/// <summary>One block of ffmpeg `-progress` output.</summary>
public sealed class FfmpegProgress
{
    public long? Frame { get; set; }
    public double? Fps { get; set; }
    public string? Bitrate { get; set; }
    public long? TotalSize { get; set; }
    public double? OutTimeSeconds { get; set; }
    public double? Speed { get; set; }
    public bool End { get; set; }
}

/// <summary>Incremental parser for ffmpeg's key=value progress stream (tail of a file).</summary>
public sealed class FfmpegProgressParser
{
    private readonly Dictionary<string, string> _cur = new();

    /// <summary>Feed a line; returns a completed snapshot when a "progress=" line closes a block.</summary>
    public FfmpegProgress? Feed(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return null;
        var key = line[..eq].Trim();
        var val = line[(eq + 1)..].Trim();
        if (key != "progress") { _cur[key] = val; return null; }

        var p = new FfmpegProgress
        {
            Frame = L("frame"),
            Fps = D("fps"),
            Bitrate = _cur.TryGetValue("bitrate", out var br) && br != "N/A" ? br : null,
            TotalSize = L("total_size"),
            Speed = D("speed", "x"),
            End = val == "end",
        };
        // out_time_us is microseconds; out_time_ms is (historically) also microseconds.
        var us = L("out_time_us") ?? L("out_time_ms");
        if (us is long u && u >= 0) p.OutTimeSeconds = u / 1_000_000.0;
        _cur.Clear();
        return p;
    }

    private long? L(string k) =>
        _cur.TryGetValue(k, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;

    private double? D(string k, string? trimSuffix = null)
    {
        if (!_cur.TryGetValue(k, out var v)) return null;
        if (trimSuffix != null && v.EndsWith(trimSuffix)) v = v[..^trimSuffix.Length];
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && double.IsFinite(r) ? r : null;
    }
}
