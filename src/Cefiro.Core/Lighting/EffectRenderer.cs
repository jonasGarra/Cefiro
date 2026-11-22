using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Lighting;

/// <summary>Datos de un fotograma para pintar un efecto.</summary>
public readonly record struct EffectContext(
    double Time,
    double Speed,
    IReadOnlyList<Rgb> Colors,
    bool Reverse,
    double? Temperature01,
    float[]? AudioBands,
    float AudioLevel,
    float Sensitivity);

/// <summary>
/// Efectos por software. Cada zona se pinta como una tira independiente (posiciones de 0 a 1), así
/// cada cadena de ventiladores muestra el efecto completo, todos con la misma base de tiempo.
/// </summary>
public static class EffectRenderer
{
    /// <summary>Degradado del efecto Temperatura: azul (frío) → cian → verde → amarillo → rojo (caliente).</summary>
    public static readonly IReadOnlyList<Rgb> TemperatureGradient =
    [
        new(0x00, 0x66, 0xFF), new(0x00, 0xE5, 0xFF), new(0x00, 0xE6, 0x76), new(0xFF, 0xD6, 0x00), new(0xFF, 0x17, 0x44),
    ];

    public static readonly IReadOnlyList<Rgb> MusicGradient =
    [
        new(0x00, 0xE6, 0x76), new(0xFF, 0xD6, 0x00), new(0xFF, 0x17, 0x44),
    ];

    /// <summary>¿Necesita fotogramas continuos?</summary>
    public static bool IsAnimated(EffectKind kind) =>
        kind is EffectKind.Breathing or EffectKind.Rainbow or EffectKind.Wave or EffectKind.ColorCycle or EffectKind.Comet or EffectKind.Music;

    public static void Render(EffectKind kind, Span<Rgb> leds, in EffectContext ctx)
    {
        if (leds.Length == 0)
            return;

        switch (kind)
        {
            case EffectKind.Off:
                leds.Fill(Rgb.Black);
                break;
            case EffectKind.Static:
                Static(leds, ctx);
                break;
            case EffectKind.Breathing:
                Breathing(leds, ctx);
                break;
            case EffectKind.Rainbow:
                Rainbow(leds, ctx);
                break;
            case EffectKind.Wave:
                Wave(leds, ctx);
                break;
            case EffectKind.ColorCycle:
                ColorCycle(leds, ctx);
                break;
            case EffectKind.Comet:
                Comet(leds, ctx);
                break;
            case EffectKind.Temperature:
                Temperature(leds, ctx);
                break;
            case EffectKind.Music:
                Music(leds, ctx);
                break;
            case EffectKind.DeviceMode:
                break; // lo pinta el propio dispositivo
        }
    }

    private static Rgb First(in EffectContext ctx) => ctx.Colors.Count > 0 ? ctx.Colors[0] : Rgb.White;

    private static double Position(int index, int count, bool reverse)
    {
        var p = count <= 1 ? 0 : (double)index / count;
        return reverse ? 1 - p : p;
    }

    private static void Static(Span<Rgb> leds, in EffectContext ctx)
    {
        if (ctx.Colors.Count <= 1)
        {
            leds.Fill(First(ctx));
            return;
        }

        // Varios colores: se reparten como un degradado a lo largo de la zona.
        for (var i = 0; i < leds.Length; i++)
            leds[i] = Rgb.Gradient(ctx.Colors, leds.Length <= 1 ? 0 : (double)i / (leds.Length - 1));
    }

    private static void Breathing(Span<Rgb> leds, in EffectContext ctx)
    {
        var period = 4.0 / Math.Max(0.05, ctx.Speed);
        var phase = ctx.Time / period;
        var colors = ctx.Colors.Count > 0 ? ctx.Colors : [Rgb.White];
        var color = colors[(int)Math.Floor(phase) % colors.Count];
        var level = (1 - Math.Cos(2 * Math.PI * (phase - Math.Floor(phase)))) / 2;
        leds.Fill(color.Scale(0.03 + 0.97 * level * level));
    }

    private static void Rainbow(Span<Rgb> leds, in EffectContext ctx)
    {
        var shift = ctx.Time * 72 * ctx.Speed; // una vuelta cada 5 s a velocidad 1
        for (var i = 0; i < leds.Length; i++)
            leds[i] = Rgb.FromHsv(Position(i, leds.Length, ctx.Reverse) * 360 - shift);
    }

    private static void Wave(Span<Rgb> leds, in EffectContext ctx)
    {
        var single = ctx.Colors.Count <= 1;
        var color = First(ctx);
        for (var i = 0; i < leds.Length; i++)
        {
            var p = Position(i, leds.Length, ctx.Reverse);
            var v = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (p * 1.5 - ctx.Time * 0.5 * ctx.Speed));
            leds[i] = single ? color.Scale(0.12 + 0.88 * v) : Rgb.Gradient(ctx.Colors, v);
        }
    }

    private static void ColorCycle(Span<Rgb> leds, in EffectContext ctx)
    {
        if (ctx.Colors.Count < 2)
        {
            leds.Fill(Rgb.FromHsv(ctx.Time * 36 * ctx.Speed)); // ciclo de tono completo cada 10 s
            return;
        }

        var period = 3.0 / Math.Max(0.05, ctx.Speed);
        var t = ctx.Time / period;
        var index = (int)Math.Floor(t) % ctx.Colors.Count;
        var next = (index + 1) % ctx.Colors.Count;
        var frac = t - Math.Floor(t);
        var smooth = frac * frac * (3 - 2 * frac);
        leds.Fill(Rgb.Lerp(ctx.Colors[index], ctx.Colors[next], smooth));
    }

    private static void Comet(Span<Rgb> leds, in EffectContext ctx)
    {
        var head = ctx.Time * 0.5 * ctx.Speed;
        head -= Math.Floor(head);
        const double tail = 0.35;
        var color = First(ctx);
        var background = ctx.Colors.Count > 1 ? ctx.Colors[1].Scale(0.15) : Rgb.Black;

        for (var i = 0; i < leds.Length; i++)
        {
            var p = Position(i, leds.Length, ctx.Reverse);
            var behind = head - p;
            if (behind < 0)
                behind += 1;

            var intensity = behind < tail ? Math.Pow(1 - behind / tail, 2) : 0;
            leds[i] = Rgb.Lerp(background, color, intensity);
        }
    }

    private static void Temperature(Span<Rgb> leds, in EffectContext ctx)
    {
        var stops = ctx.Colors.Count >= 2 ? ctx.Colors : TemperatureGradient;
        leds.Fill(Rgb.Gradient(stops, ctx.Temperature01 ?? 0));
    }

    private static void Music(Span<Rgb> leds, in EffectContext ctx)
    {
        var palette = ctx.Colors.Count >= 2 ? ctx.Colors : MusicGradient;
        var bands = ctx.AudioBands;

        if (bands is null || bands.Length == 0)
        {
            // Sin audio: respiración lenta y visible ("esperando música"), para que no parezca parado.
            var idle = 0.08 + 0.22 * (1 + Math.Sin(ctx.Time * 1.2)) / 2;
            for (var i = 0; i < leds.Length; i++)
                leds[i] = Rgb.Gradient(palette, (double)i / Math.Max(1, leds.Length - 1)).Scale(idle);
            return;
        }

        if (leds.Length == 1)
        {
            var level = Math.Clamp(ctx.AudioLevel * ctx.Sensitivity, 0, 1);
            leds[0] = Rgb.Gradient(palette, level).Scale(0.1 + 0.9 * level);
            return;
        }

        // Cada LED escucha una banda (graves a un lado, agudos al otro) y su color depende de la posición.
        for (var i = 0; i < leds.Length; i++)
        {
            var p = Position(i, leds.Length, ctx.Reverse);
            var band = bands[Math.Min(bands.Length - 1, (int)(p * bands.Length))];
            var level = Math.Clamp(band * ctx.Sensitivity, 0, 1);
            leds[i] = Rgb.Gradient(palette, p).Scale(0.05 + 0.95 * level);
        }
    }
}
