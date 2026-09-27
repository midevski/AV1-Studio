using AV1Studio.Models;

namespace AV1Studio.Services;

/// <summary>A starting point that fills in settings; every value stays editable afterwards.
/// Profiles are not universally optimal — results depend on the source content.</summary>
public sealed record EncodingProfile(string Name, string Description, double Vmaf, int? SvtPreset,
    (double Svt, double Aom, double Nvenc, double Qsv, double Amf) Quality,
    (string Svt, string Aom, string Nvenc, string Qsv, string Amf) Preset);

public static class EncodingProfiles
{
    public const string Custom = "Custom";

    public static readonly IReadOnlyList<EncodingProfile> All =
    [
        new("Archive", "Highest quality target and slower, more efficient presets. Large files and long encodes; for sources you want to keep near-transparent.",
            97, 5, (22, 22, 24, 20, 80), ("4", "3", "p7", "veryslow", "high_quality")),
        new("High Quality", "Very good quality with good savings. AB-AV1 uses its default preset. A sensible default for movie libraries.",
            95, null, (26, 26, 28, 24, 100), ("5", "4", "p6", "slower", "quality")),
        new("Balanced", "Good quality, smaller files and moderate encoding time. Some fine detail may be simplified on demanding scenes.",
            93, null, (30, 30, 32, 28, 120), ("6", "5", "p5", "slow", "quality")),
        new("Small File", "Prioritises file size. Visible quality loss is possible on grainy or detailed content; uses slower presets to make the most of each bit.",
            90, 5, (36, 36, 38, 34, 150), ("5", "4", "p6", "slower", "quality")),
        new("Fast Encode", "Prioritises encoding speed. Files are usually larger than with slower presets at similar quality.",
            93, 10, (32, 32, 34, 30, 130), ("10", "7", "p2", "veryfast", "speed")),
    ];

    public static EncodingProfile? Find(string? name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>AB-AV1: target VMAF and SVT-AV1 preset (the CRF is found automatically).</summary>
    public static void ApplyAbAv1(AppSettings s, EncodingProfile p)
    {
        s.QualityPreset = p.Name;
        s.TargetVmaf = p.Vmaf;
        s.Preset = p.SvtPreset;
    }

    /// <summary>Manual AV1: quality value and preset for the currently selected encoder.</summary>
    public static void ApplyManual(ManualSettings m, EncodingProfile p)
    {
        (m.Quality, m.Preset) = m.Encoder switch
        {
            "libaom-av1" => (p.Quality.Aom, p.Preset.Aom),
            "av1_nvenc" => (p.Quality.Nvenc, p.Preset.Nvenc),
            "av1_qsv" => (p.Quality.Qsv, p.Preset.Qsv),
            "av1_amf" => (p.Quality.Amf, p.Preset.Amf),
            _ => (p.Quality.Svt, p.Preset.Svt),
        };
        m.ProfileName = p.Name;
    }
}
