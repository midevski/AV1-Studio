using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AV1Studio.Models;
using AV1Studio.Services;

namespace AV1Studio.Views;

public sealed class StatusBrushConverter : IValueConverter
{
    private static SolidColorBrush B(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    private static readonly Dictionary<ItemStatus, Brush> Map = new()
    {
        [ItemStatus.Waiting] = B(0xFF8A8F98),
        [ItemStatus.Analyzing] = B(0xFFB48EF7),
        [ItemStatus.CrfFound] = B(0xFF4FC1E9),
        [ItemStatus.Ready] = B(0xFF5DADE2),
        [ItemStatus.Encoding] = B(0xFFF5A623),
        [ItemStatus.Verifying] = B(0xFFF7DC6F),
        [ItemStatus.Completed] = B(0xFF4CD18A),
        [ItemStatus.Skipped] = B(0xFF9E9E9E),
        [ItemStatus.Failed] = B(0xFFFF5C5C),
        [ItemStatus.Cancelled] = B(0xFFB0A090),
        [ItemStatus.Deleted] = B(0xFF2ECC71),
    };

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is ItemStatus s && Map.TryGetValue(s, out var b) ? b : Brushes.Gray;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class LogBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
        LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xF5, 0xB0, 0x41)),
        LogLevel.Success => new SolidColorBrush(Color.FromRgb(0x4C, 0xD1, 0x8A)),
        LogLevel.Command => new SolidColorBrush(Color.FromRgb(0x7F, 0xB2, 0xFF)),
        _ => DependencyProperty.UnsetValue,
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when the value is non-null and not an empty string. Parameter "invert" flips it.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool has = value switch { null => false, string s => s.Length > 0, bool b => b, int i => i > 0, _ => true };
        if (p as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StatusIsConverter : IValueConverter
{
    /// <summary>True (or Visible) if the status is one of the comma separated names in the parameter.</summary>
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool match = value is ItemStatus s && p is string names && names.Split(',').Any(n => n.Trim() == s.ToString());
        return t == typeof(Visibility) ? (match ? Visibility.Visible : Visibility.Collapsed) : match;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        long l => Util.Fmt.Bytes(l),
        ulong u => Util.Fmt.Bytes((long)u),
        _ => "",
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NumberConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is double d ? Util.Fmt.Num(d, p as string ?? "0.##") : "";

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Two-way binding helper for nullable numbers in text boxes (empty = null).</summary>
public sealed class NullableNumberConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        double d => Util.Fmt.Arg(d),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => "",
    };

    public object? ConvertBack(object value, Type t, object p, CultureInfo c)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return null;
        var target = Nullable.GetUnderlyingType(t) ?? t;
        if (!Util.Fmt.TryParseDouble(s, out var d)) return DependencyProperty.UnsetValue;
        return target == typeof(int) ? (int)Math.Round(d) : d;
    }
}

/// <summary>Visible when the enum value's name equals the parameter.</summary>
public sealed class EnumVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value?.ToString() == p as string ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>RadioButton ↔ enum: IsChecked when value equals the parameter name.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value?.ToString() == p as string;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true && p is string name ? Enum.Parse(Nullable.GetUnderlyingType(t) ?? t, name) : Binding.DoNothing;
}

public sealed class EnumValuesExtension : System.Windows.Markup.MarkupExtension
{
    public Type? Type { get; set; }
    public override object ProvideValue(IServiceProvider sp) => Type is null ? Array.Empty<object>() : Enum.GetValues(Type);
}
