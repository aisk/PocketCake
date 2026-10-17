using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PocketCake.Views;

/// <summary>Vector icon and category colour of each chain module, for x:Bind function bindings.</summary>
public static class ModuleVisuals
{
    private static readonly Dictionary<string, uint> Colors = new()
    {
        ["NR"] = 0x64748B,
        ["FX1"] = 0x8B5CF6,
        ["DRV"] = 0xF97316,
        ["AMP"] = 0xEF4444,
        ["IR"] = 0xC2410C,
        ["EQ"] = 0x16A34A,
        ["FX2"] = 0xDB2777,
        ["DLY"] = 0x0891B2,
        ["RVB"] = 0x2563EB,
    };

    // Path markup on a 24x24 canvas (host it in a 24x24 Path inside a Viewbox), non-zero fill: clockwise shapes are solid, counter-clockwise ones cut holes.
    private static readonly Dictionary<string, string> Icons = new()
    {
        // Pedal with one big pointer knob
        ["NR"] = new IconPath().Pedal()
            .Circle(12, 8, 4, hole: true).Circle(12, 8, 2.8).Rect(11.4, 5.4, 1.2, 2.8, 0.6, hole: true)
            .Footswitch().ToString(),
        // Pedal with two knobs
        ["FX1"] = new IconPath().Pedal()
            .Knob(8.8, 7, 2.5, 1.5).Knob(15.2, 7, 2.5, 1.5)
            .Footswitch().ToString(),
        // Wide pedal with three knobs
        ["DRV"] = new IconPath().Rect(3.5, 1.5, 17, 21, 2.5)
            .Knob(7.5, 6.5, 1.9, 1.1).Knob(12, 6.5, 1.9, 1.1).Knob(16.5, 6.5, 1.9, 1.1)
            .Footswitch().ToString(),
        // Amp head: handle, control panel with knobs, grille
        ["AMP"] = new IconPath().Rect(2, 6, 20, 14, 2.5)
            .Raw("M8,6.5 V4.5 A1.5,1.5 0 0 1 9.5,3 H14.5 A1.5,1.5 0 0 1 16,4.5 V6.5 H14.5 V4.5 H9.5 V6.5 Z")
            .Rect(4, 8, 16, 3.6, 1, hole: true)
            .Circle(7, 9.8, 0.9).Circle(10, 9.8, 0.9).Circle(13, 9.8, 0.9).Circle(16, 9.8, 0.9)
            .Rect(4, 13.2, 16, 5, 1, hole: true).ToString(),
        // 4x12 cabinet
        ["IR"] = new IconPath().Rect(2.5, 2.5, 19, 19, 2.5)
            .Knob(8.3, 8.3, 3.2, 1.3).Knob(15.7, 8.3, 3.2, 1.3)
            .Knob(8.3, 15.7, 3.2, 1.3).Knob(15.7, 15.7, 3.2, 1.3).ToString(),
        // Four-band slider EQ
        ["EQ"] = new IconPath().Rect(2.5, 3, 19, 18, 2.5)
            .Slider(6, 9.5).Slider(10, 14.5).Slider(14, 8).Slider(18, 12.5).ToString(),
        // Pedal with a display window
        ["FX2"] = new IconPath().Pedal()
            .Rect(7.5, 4, 9, 6, 1.2, hole: true).Rect(9, 6.3, 6, 1.4, 0.7)
            .Footswitch().ToString(),
        // Fading repeats
        ["DLY"] = new IconPath().Circle(5.5, 12, 4).Circle(13.2, 12, 2.8).Circle(19.6, 12, 1.9).ToString(),
        // Ripples
        ["RVB"] = new IconPath().Circle(12, 12, 2.4)
            .Circle(12, 12, 6.4).Circle(12, 12, 4.6, hole: true)
            .Circle(12, 12, 10.4).Circle(12, 12, 8.6, hole: true).ToString(),
    };

    private static readonly Dictionary<(string, byte), SolidColorBrush> Brushes = [];

    /// <summary>The module's icon. A Geometry can only belong to one element, so every call parses a new one.</summary>
    public static Geometry Icon(string module) =>
        (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Icons.GetValueOrDefault(module, ""));

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
            var rgb = Colors.GetValueOrDefault(module, 0x808080u);
            brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            Brushes[(module, alpha)] = brush;
        }

        return brush;
    }

    /// <summary>Builds non-zero path markup from rounded rectangles and circles.</summary>
    private sealed class IconPath
    {
        private readonly StringBuilder _sb = new("F1");

        public IconPath Pedal() => Rect(5, 1.5, 14, 21, 2.5);

        public IconPath Footswitch() => Knob(12, 17.5, 2.6, 1.5);

        /// <summary>A solid dot inside a ring-shaped hole.</summary>
        public IconPath Knob(double cx, double cy, double outer, double inner) =>
            Circle(cx, cy, outer, hole: true).Circle(cx, cy, inner);

        /// <summary>A slider cap in its own clearance, with the slot above and below it.</summary>
        public IconPath Slider(double x, double capY)
        {
            const double top = 5.5, bottom = 18.5, clearance = 1.6;
            return Rect(x - 0.7, top, 1.4, capY - clearance - top, 0, hole: true)
                .Rect(x - 0.7, capY + clearance, 1.4, bottom - capY - clearance, 0, hole: true)
                .Rect(x - clearance, capY - clearance, clearance * 2, clearance * 2, 0.8, hole: true)
                .Rect(x - 1, capY - 0.8, 2, 1.6, 0.4);
        }

        public IconPath Rect(double x, double y, double w, double h, double r, bool hole = false)
        {
            double r2 = x + w, b = y + h;
            return hole
                ? Append($" M{x + r},{y} A{r},{r} 0 0 0 {x},{y + r} V{b - r} A{r},{r} 0 0 0 {x + r},{b} H{r2 - r} A{r},{r} 0 0 0 {r2},{b - r} V{y + r} A{r},{r} 0 0 0 {r2 - r},{y} Z")
                : Append($" M{x + r},{y} H{r2 - r} A{r},{r} 0 0 1 {r2},{y + r} V{b - r} A{r},{r} 0 0 1 {r2 - r},{b} H{x + r} A{r},{r} 0 0 1 {x},{b - r} V{y + r} A{r},{r} 0 0 1 {x + r},{y} Z");
        }

        public IconPath Circle(double cx, double cy, double r, bool hole = false)
        {
            var sweep = hole ? 0 : 1;
            return Append($" M{cx - r},{cy} A{r},{r} 0 1 {sweep} {cx + r},{cy} A{r},{r} 0 1 {sweep} {cx - r},{cy} Z");
        }

        public IconPath Raw(string markup) => Append($" {markup}");

        public override string ToString() => _sb.ToString();

        private IconPath Append(FormattableString part)
        {
            _sb.Append(part.ToString(CultureInfo.InvariantCulture));
            return this;
        }
    }
}
