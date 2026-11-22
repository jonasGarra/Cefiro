using System.Globalization;
using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Lighting;

/// <summary>Comprueba y corrige la configuración de iluminación antes de usarla o guardarla.</summary>
public static class LightingValidator
{
    /// <summary>
    /// LEDs por defecto de un cabezal ARGB sin configurar. Los ventiladores RS de Corsair van en paralelo
    /// (cada ventilador repite los mismos 8 LEDs), comprobado en tu PC en la Fase 3: 8 es lo correcto.
    /// </summary>
    public const int DefaultResizableZoneLeds = 8;

    public static LightingSettingsDto Validate(LightingSettingsDto? input, IReadOnlySet<string> temperatureSensorIds)
    {
        var d = new LightingSettingsDto();
        if (input is null)
            return d;

        return new LightingSettingsDto
        {
            Enabled = input.Enabled,
            Brightness = Clamp(input.Brightness, 0, 100, d.Brightness),
            SpeedMultiplier = Clamp(input.SpeedMultiplier, 0.1f, 5, d.SpeedMultiplier),
            SyncAll = input.SyncAll,
            SyncEffect = ValidateEffect(input.SyncEffect, temperatureSensorIds),
            Layers = (input.Layers ?? [])
                .Where(l => l?.Target is not null && !string.IsNullOrWhiteSpace(l.Target.DeviceKey))
                .Take(128)
                .Select(l => new LightingLayerDto(
                    new LightingTargetDto(
                        l.Target.DeviceKey,
                        l.Target.ZoneIndex is >= 0 ? l.Target.ZoneIndex : null,
                        l.Target.LedIndex is >= 0 ? l.Target.LedIndex : null),
                    ValidateEffect(l.Effect, temperatureSensorIds)))
                .ToList(),
            Favorites = (input.Favorites ?? [])
                .Where(Rgb.IsValidHex)
                .Select(h => Rgb.FromHex(h, Rgb.Black).ToHex())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList(),
            Night = ValidateNight(input.Night),
            ZoneSizes = (input.ZoneSizes ?? [])
                .Where(z => z is not null && !string.IsNullOrWhiteSpace(z.DeviceKey) && z.ZoneIndex >= 0)
                .Select(z => z with { LedCount = Math.Clamp(z.LedCount, 0, 1000) })
                .GroupBy(z => (z.DeviceKey, z.ZoneIndex))
                .Select(g => g.Last())
                .ToList(),
            Fps = Math.Clamp(input.Fps, 5, 60),
        };
    }

    public static EffectDto ValidateEffect(EffectDto? e, IReadOnlySet<string> temperatureSensorIds)
    {
        var d = new EffectDto();
        if (e is null)
            return d;

        var colors = (e.Colors ?? [])
            .Where(Rgb.IsValidHex)
            .Select(h => Rgb.FromHex(h, Rgb.Black).ToHex())
            .Take(16)
            .ToList();

        var min = Clamp(e.MinTemperature, 0, 110, d.MinTemperature);
        var max = Clamp(e.MaxTemperature, 0, 120, d.MaxTemperature);
        if (max <= min + 1)
            (min, max) = (d.MinTemperature, d.MaxTemperature);

        return new EffectDto
        {
            Kind = Enum.IsDefined(e.Kind) ? e.Kind : EffectKind.Static,
            Colors = colors.Count > 0 ? colors : d.Colors,
            Speed = Clamp(e.Speed, 0.1f, 5, d.Speed),
            Brightness = Clamp(e.Brightness, 0, 100, d.Brightness),
            Reverse = e.Reverse,
            SensorId = e.SensorId is { } id && temperatureSensorIds.Contains(id) ? id : null,
            MinTemperature = min,
            MaxTemperature = max,
            Sensitivity = Clamp(e.Sensitivity, 0.1f, 5, d.Sensitivity),
            NativeMode = string.IsNullOrWhiteSpace(e.NativeMode) ? null : e.NativeMode.Trim()[..Math.Min(64, e.NativeMode.Trim().Length)],
        };
    }

    public static NightModeDto ValidateNight(NightModeDto? n)
    {
        var d = new NightModeDto();
        if (n is null)
            return d;

        return new NightModeDto
        {
            Enabled = n.Enabled,
            Start = TryParseTime(n.Start, out _) ? n.Start.Trim() : d.Start,
            End = TryParseTime(n.End, out _) ? n.End.Trim() : d.End,
            Action = Enum.IsDefined(n.Action) ? n.Action : d.Action,
            DimBrightness = Clamp(n.DimBrightness, 0, 100, d.DimBrightness),
        };
    }

    public static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text?.Trim(), [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out time)
        && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    /// <summary>¿Está activo el modo noche a esta hora? Admite franjas que cruzan la medianoche.</summary>
    public static bool IsNightActive(NightModeDto night, TimeSpan timeOfDay)
    {
        if (!night.Enabled || !TryParseTime(night.Start, out var start) || !TryParseTime(night.End, out var end) || start == end)
            return false;

        return start < end
            ? timeOfDay >= start && timeOfDay < end
            : timeOfDay >= start || timeOfDay < end;
    }

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
