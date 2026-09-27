namespace AV1Studio.Models;

/// <summary>One ab-av1 sample-encode result (a "sample-encode-done" JSON message).</summary>
public sealed class CrfAttempt
{
    public double Crf { get; set; }
    public double? Vmaf { get; set; }
    public long? PredictedSize { get; set; }
    public double? PredictedPercent { get; set; }
    public double? PredictedSeconds { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>Final result of an ab-av1 crf-search.</summary>
public sealed class CrfSearchResult
{
    public double Crf { get; set; }
    public double? Vmaf { get; set; }
    public long? PredictedSize { get; set; }
    public double? PredictedPercent { get; set; }
    public double? PredictedSeconds { get; set; }
    public bool FromCache { get; set; }
}
