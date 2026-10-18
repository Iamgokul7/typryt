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
        ["WindowBackgroundBrush"] = ("#1C1C1E", "#F5F5F7"),
        ["SurfaceBrush"] = ("#2C2C2E", "#FFFFFF"),
        ["TextPrimaryBrush"] = ("#F2F2F2", "#1C1C1E"),
        ["TextSecondaryBrush"] = ("#A1A1A6", "#6E6E73"),
        ["AccentBrush"] = ("#E8620A", "#E8620A"),
    };

    /// <summary>Applies "dark" or "light". Any other value means dark.</summary>
    public static void Apply(string theme)
    {
        bool light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        foreach (var (key, colours) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(light ? colours.Light : colours.Dark);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }
}
