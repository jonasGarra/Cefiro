using System.Globalization;

namespace Cefiro.App.Infrastructure;

/// <summary>Formato de números en español (coma decimal, punto de miles).</summary>
public static class Format
{
    public static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public static bool UseFahrenheit { get; set; }

    public static double? ToUnit(float? celsius) =>
        celsius is { } c ? UseFahrenheit ? c * 9 / 5 + 32 : c : null;

    public static string TemperatureUnit => UseFahrenheit ? "°F" : "°C";

    public static string Temperature(float? celsius, int decimals = 1) =>
        ToUnit(celsius) is { } v ? v.ToString(decimals == 0 ? "0" : "0.0", Es) + " " + TemperatureUnit : "—";

    public static string TemperatureValue(float? celsius, int decimals = 0) =>
        ToUnit(celsius) is { } v ? v.ToString(decimals == 0 ? "0" : "0.0", Es) : "—";

    public static string Percent(float? value) => value is { } v ? v.ToString("0", Es) + " %" : "—";

    public static string Rpm(float? value) => value is { } v ? v.ToString("#,0", Es) + " RPM" : "—";

    public static string Mhz(float? value) =>
        value is { } v ? v >= 1000 ? (v / 1000).ToString("0.00", Es) + " GHz" : v.ToString("0", Es) + " MHz" : "—";

    public static string Watts(float? value) => value is { } v ? v.ToString("0", Es) + " W" : "—";

    public static string Gigabytes(float? bytes) =>
        bytes is { } b ? (b / 1_073_741_824.0).ToString("0.0", Es) + " GB" : "—";

    public static string Value(float? value, string unit) => unit switch
    {
        "°C" => Temperature(value),
        "%" => Percent(value),
        "RPM" => Rpm(value),
        "MHz" => Mhz(value),
        "W" => value is { } w ? w.ToString("0.0", Es) + " W" : "—",
        "V" => value is { } v ? v.ToString("0.000", Es) + " V" : "—",
        "B" => Gigabytes(value),
        _ => value is { } x ? x.ToString("0.##", Es) + " " + unit : "—",
    };

    /// <summary>Color de una temperatura: frío (acento) → templado (ámbar) → caliente (rosa neón).</summary>
    public static System.Windows.Media.Color TemperatureColor(float? celsius, float warm = 65, float hot = 85)
    {
        if (celsius is not { } c)
            return System.Windows.Media.Color.FromRgb(0x5D, 0x68, 0x80);

        static System.Windows.Media.Color Lerp(System.Windows.Media.Color a, System.Windows.Media.Color b, double t) =>
            System.Windows.Media.Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        var cool = System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xFF);
        var amber = System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x00);
        var pink = System.Windows.Media.Color.FromRgb(0xFF, 0x2E, 0x88);

        if (c <= warm - 20) return cool;
        if (c <= warm) return Lerp(cool, amber, (c - (warm - 20)) / 20.0);
        if (c <= hot) return Lerp(amber, pink, (c - warm) / (hot - warm));
        return pink;
    }
}
