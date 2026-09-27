using System.Windows;
using System.Windows.Media;
using AV1Studio.Models;

namespace AV1Studio.Views;

/// <summary>Switches between the dark and light appearance at runtime: the Fluent theme mode plus the
/// application's own brushes (mutated in place, so every StaticResource user updates).</summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    private static readonly Dictionary<string, (Color Dark, Color Light)> Palette = new()
    {
        ["AccentText"] = (C("#7FB2FF"), C("#1F5FD0")),
        ["SubtleText"] = (C("#9AA0A6"), C("#5F6368")),
        ["DangerText"] = (C("#FF6B6B"), C("#C62828")),
        ["SuccessText"] = (C("#4CD18A"), C("#1E8E4F")),
        ["WarningText"] = (C("#F5B041"), C("#A15C00")),
        ["CardBackground"] = (C("#14FFFFFF"), C("#0A000000")),
        ["CardBorder"] = (C("#22FFFFFF"), C("#1F000000")),
        ["CardHover"] = (C("#22FFFFFF"), C("#14000000")),
        ["NavBackground"] = (C("#0CFFFFFF"), C("#08000000")),
        ["SelectedCardBorder"] = (C("#4F8EF7"), C("#2F6FDB")),
    };

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var app = Application.Current;
        if (app is null) return;
        app.ThemeMode = theme == AppTheme.Light ? ThemeMode.Light : ThemeMode.Dark;
        foreach (var (key, colors) in Palette)
        {
            var c = theme == AppTheme.Light ? colors.Light : colors.Dark;
            if (app.Resources[key] is SolidColorBrush b && !b.IsFrozen) b.Color = c;
            else app.Resources[key] = new SolidColorBrush(c);
        }
        foreach (Window w in app.Windows) WindowChrome.Refresh(w);
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
