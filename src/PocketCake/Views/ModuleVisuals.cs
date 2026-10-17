using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PocketCake.Views;

/// <summary>Icon glyph and category colour of each chain module, for x:Bind function bindings.</summary>
public static class ModuleVisuals
{
    private static readonly Dictionary<string, (string Glyph, uint Rgb)> Map = new()
    {
        ["NR"] = ("", 0x64748B),
        ["FX1"] = ("", 0x8B5CF6),
        ["DRV"] = ("", 0xF97316),
        ["AMP"] = ("", 0xEF4444),
        ["IR"] = ("", 0xC2410C),
        ["EQ"] = ("", 0x16A34A),
        ["FX2"] = ("", 0xDB2777),
        ["DLY"] = ("", 0x0891B2),
        ["RVB"] = ("", 0x2563EB),
    };

    private static readonly Dictionary<(string, byte), SolidColorBrush> Brushes = [];

    public static string Glyph(string module) => Map.TryGetValue(module, out var v) ? v.Glyph : "";

    /// <summary>The module's colour, for its icon.</summary>
    public static SolidColorBrush Brush(string module) => Get(module, 0xFF);

    /// <summary>A translucent wash of the module's colour, for the icon's backdrop.</summary>
    public static SolidColorBrush Tint(string module) => Get(module, 0x30);

    /// <summary>Dims a bypassed module's icon.</summary>
    public static double Dim(bool isOn) => isOn ? 1 : 0.4;

    private static SolidColorBrush Get(string module, byte alpha)
    {
        if (!Brushes.TryGetValue((module, alpha), out var brush))
        {
            var rgb = Map.TryGetValue(module, out var v) ? v.Rgb : 0x808080;
            brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            Brushes[(module, alpha)] = brush;
        }

        return brush;
    }
}
