using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Cefiro.App.Infrastructure;

/// <summary>Cambia el color de acento de toda la interfaz en tiempo real.</summary>
public static class ThemeService
{
    public static Color Accent { get; private set; } = Color.FromRgb(0x00, 0xE5, 0xFF);

    public static event Action<Color>? AccentChanged;

    public static void ApplyAccent(Color accent, Color? secondary = null)
    {
        // Colores casi negros o grises no sirven de acento: se usa el cian por defecto.
        var max = Math.Max(accent.R, Math.Max(accent.G, accent.B));
        var min = Math.Min(accent.R, Math.Min(accent.G, accent.B));
        if (max < 60 || max - min < 40)
            accent = Color.FromRgb(0x00, 0xE5, 0xFF);

        var second = secondary ?? Shift(accent, 70);
        var res = Application.Current.Resources;

        res["CefiroAccentColor"] = accent;
        res["CefiroAccent2Color"] = second;
        res["CefiroAccentGlowColor"] = accent;
        res["CefiroAccentBrush"] = Frozen(new SolidColorBrush(accent));
        res["CefiroAccent2Brush"] = Frozen(new SolidColorBrush(second));
        res["CefiroAccentSoftBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)));

        var gradient = new LinearGradientBrush(accent, second, new Point(0, 0), new Point(1, 1));
        res["CefiroAccentGradientBrush"] = Frozen(gradient);

        // Controles de WPF-UI (interruptores, casillas…).
        ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark);

        Accent = accent;
        AccentChanged?.Invoke(accent);
    }

    /// <summary>Gira el tono para obtener un segundo color que combine (análogo).</summary>
    public static Color Shift(Color c, double degrees)
    {
        var rgb = new Core.Lighting.Rgb(c.R, c.G, c.B);
        var (h, s, v) = rgb.ToHsv();
        var shifted = Core.Lighting.Rgb.FromHsv(h + degrees, Math.Max(0.55, s), Math.Max(0.75, v));
        return Color.FromRgb(shifted.R, shifted.G, shifted.B);
    }

    public static Color Parse(string? hex, Color fallback) =>
        Core.Lighting.Rgb.TryParseHex(hex, out var c) ? Color.FromRgb(c.R, c.G, c.B) : fallback;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
