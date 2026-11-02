using System.Windows;
using System.Windows.Media;

namespace Typryt;

/// <summary>
/// Switches the app between the dark and light colours. The windows use the brushes named in
/// App.xaml through DynamicResource, so replacing the brushes here changes every open window.
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        ["WindowBackgroundBrush"] = ("#121212", "#F4EEDF"),
        ["SurfaceBrush"] = ("#1C1C1C", "#FFFFFF"),
        ["Panel2Brush"] = ("#2A2A2A", "#E9E1CC"),
        ["TextPrimaryBrush"] = ("#FFFFFF", "#000000"),
        ["TextSecondaryBrush"] = ("#D4D4D4", "#333333"),
        ["AccentBrush"] = ("#DE5C0A", "#DE5C0A"),
        ["CardShadowBrush"] = ("#3A3A3A", "#000000"),
        ["ButtonShadowBrush"] = ("#DE5C0A", "#000000"),
        ["LineBrush"] = ("#3F3F3F", "#CFC6AE"),
        ["FocusBrush"] = ("#DE5C0A", "#000000"),
    };

    /// <summary>One frozen brush per colour and theme, made once; Apply only swaps references.</summary>
    private static readonly Dictionary<string, (Brush Dark, Brush Light)> Brushes = Palette.ToDictionary(
        p => p.Key,
        p => (Make(p.Value.Dark), Make(p.Value.Light)));

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>Applies "dark" or "light". Any other value means dark.</summary>
    public static void Apply(string theme)
    {
        bool light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        foreach (var (key, pair) in Brushes)
        {
            Application.Current.Resources[key] = light ? pair.Light : pair.Dark;
        }
    }
}
