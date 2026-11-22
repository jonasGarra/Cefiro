using System.Globalization;

namespace Cefiro.Core.Lighting;

/// <summary>Color RGB de 8 bits por canal.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    public static bool TryParseHex(string? hex, out Rgb color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);

        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;

        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static Rgb FromHex(string? hex, Rgb fallback) => TryParseHex(hex, out var c) ? c : fallback;

    public static bool IsValidHex(string? hex) => TryParseHex(hex, out _);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Tono 0-360, saturación y valor 0-1.</summary>
    public static Rgb FromHsv(double hue, double saturation = 1, double value = 1)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    public (double Hue, double Saturation, double Value) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * ((g - b) / delta % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }

        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t, MidpointRounding.AwayFromZero),
            (byte)Math.Round(a.G + (b.G - a.G) * t, MidpointRounding.AwayFromZero),
            (byte)Math.Round(a.B + (b.B - a.B) * t, MidpointRounding.AwayFromZero));
    }

    /// <summary>Multiplica el brillo (0-1).</summary>
    public Rgb Scale(double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new Rgb((byte)Math.Round(R * factor, MidpointRounding.AwayFromZero), (byte)Math.Round(G * factor, MidpointRounding.AwayFromZero), (byte)Math.Round(B * factor, MidpointRounding.AwayFromZero));
    }

    /// <summary>Color de un degradado de varios colores en la posición t (0-1).</summary>
    public static Rgb Gradient(IReadOnlyList<Rgb> stops, double t)
    {
        if (stops.Count == 0) return Black;
        if (stops.Count == 1) return stops[0];

        t = Math.Clamp(t, 0, 1) * (stops.Count - 1);
        var i = Math.Min((int)t, stops.Count - 2);
        return Lerp(stops[i], stops[i + 1], t - i);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
