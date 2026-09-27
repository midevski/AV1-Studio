using System.Globalization;

namespace AV1Studio.Util;

public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Bytes(long? bytes)
    {
        if (bytes is not long b) return "";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = b;
        int u = 0;
        while (Math.Abs(v) >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{b} B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", Inv) + " " + units[u];
    }

    public static string Duration(TimeSpan? t)
    {
        if (t is not TimeSpan ts || ts < TimeSpan.Zero) return "";
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    public static string Duration(double? seconds) =>
        seconds is double s && double.IsFinite(s) ? Duration(TimeSpan.FromSeconds(s)) : "";

    public static string Num(double? v, string format = "0.##") =>
        v is double d && double.IsFinite(d) ? d.ToString(format, Inv) : "";

    public static string Percent(double? v) =>
        v is double d && double.IsFinite(d) ? d.ToString("0.0", Inv) + "%" : "";

    /// <summary>Invariant parsing for user-entered numbers ("95.5" or "95,5").</summary>
    public static bool TryParseDouble(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, Inv, out value) && double.IsFinite(value);
    }

    /// <summary>Culture-independent number for command-line arguments.</summary>
    public static string Arg(double v) => v.ToString("0.###", Inv);
}
