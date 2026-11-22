using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Fans;

/// <summary>Curvas temperatura → % de ventilador.</summary>
public static class FanCurve
{
    public const int MinPoints = 2;
    public const int MaxPoints = 16;
    public const float MinTemperature = 0;
    public const float MaxTemperature = 120;

    /// <summary>
    /// Interpolación lineal entre puntos. Por debajo del primero se usa su %, por encima del último el suyo.
    /// Espera una curva ya normalizada (ver <see cref="Normalize"/>).
    /// </summary>
    public static float Evaluate(IReadOnlyList<CurvePointDto> points, float temperature)
    {
        if (points.Count == 0)
            return 100; // sin curva: lo más seguro

        if (float.IsNaN(temperature))
            return 100;

        if (temperature <= points[0].Temperature)
            return points[0].Percent;

        for (var i = 1; i < points.Count; i++)
        {
            var b = points[i];
            if (temperature <= b.Temperature)
            {
                var a = points[i - 1];
                var span = b.Temperature - a.Temperature;
                if (span <= 0)
                    return b.Percent;

                var t = (temperature - a.Temperature) / span;
                return a.Percent + (b.Percent - a.Percent) * t;
            }
        }

        return points[^1].Percent;
    }

    /// <summary>
    /// Deja una curva válida: ordenada por temperatura, sin temperaturas repetidas, valores dentro de
    /// rango y nunca decreciente (a más temperatura, nunca menos ventilador). Entre 2 y 16 puntos.
    /// </summary>
    public static IReadOnlyList<CurvePointDto> Normalize(IReadOnlyList<CurvePointDto>? points, IReadOnlyList<CurvePointDto> fallback)
    {
        if (points is null || points.Count < MinPoints)
            return fallback;

        var cleaned = points
            .Where(p => float.IsFinite(p.Temperature) && float.IsFinite(p.Percent))
            .Select(p => new CurvePointDto(
                MathF.Round(Math.Clamp(p.Temperature, MinTemperature, MaxTemperature), 1),
                MathF.Round(Math.Clamp(p.Percent, 0, 100), 1)))
            .GroupBy(p => p.Temperature)
            .Select(g => g.Last())
            .OrderBy(p => p.Temperature)
            .Take(MaxPoints)
            .ToList();

        if (cleaned.Count < MinPoints)
            return fallback;

        for (var i = 1; i < cleaned.Count; i++)
        {
            if (cleaned[i].Percent < cleaned[i - 1].Percent)
                cleaned[i] = cleaned[i] with { Percent = cleaned[i - 1].Percent };
        }

        return cleaned;
    }
}
