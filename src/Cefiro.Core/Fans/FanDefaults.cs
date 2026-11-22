using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Fans;

/// <summary>Ajustes por defecto y modos predefinidos (Silencioso, Equilibrado, Rendimiento, Máximo).</summary>
public static class FanDefaults
{
    private static CurvePointDto P(float t, float p) => new(t, p);

    /// <summary>Curvas por rol y modo. Pensadas para un Ryzen 7700X con líquida de 360 mm y una RTX 5070.</summary>
    public static IReadOnlyList<CurvePointDto> Curve(FanRole role, FanPreset preset) => (role, preset) switch
    {
        (FanRole.Case, FanPreset.Silent) => [P(35, 25), P(55, 35), P(70, 50), P(80, 70), P(88, 100)],
        (FanRole.Case, FanPreset.Balanced) => [P(35, 30), P(50, 40), P(65, 60), P(75, 80), P(85, 100)],
        (FanRole.Case, FanPreset.Performance) => [P(30, 45), P(45, 55), P(60, 75), P(70, 90), P(80, 100)],

        (FanRole.Radiator, FanPreset.Silent) => [P(40, 30), P(60, 40), P(72, 55), P(82, 80), P(90, 100)],
        (FanRole.Radiator, FanPreset.Balanced) => [P(40, 35), P(55, 45), P(70, 65), P(80, 85), P(88, 100)],
        (FanRole.Radiator, FanPreset.Performance) => [P(35, 50), P(50, 65), P(65, 80), P(75, 95), P(82, 100)],

        (FanRole.Pump, FanPreset.Silent) => [P(40, 65), P(70, 80), P(85, 100)],
        (FanRole.Pump, FanPreset.Balanced) => [P(40, 80), P(65, 90), P(80, 100)],
        (FanRole.Pump, FanPreset.Performance) => [P(30, 100), P(90, 100)],

        (FanRole.Gpu, FanPreset.Silent) => [P(50, 30), P(65, 40), P(75, 60), P(83, 85), P(88, 100)],
        (FanRole.Gpu, FanPreset.Balanced) => [P(45, 30), P(60, 45), P(70, 60), P(78, 80), P(85, 100)],
        (FanRole.Gpu, FanPreset.Performance) => [P(40, 40), P(55, 55), P(65, 75), P(75, 95), P(80, 100)],

        _ => [P(30, 100), P(90, 100)], // Máximo
    };

    /// <summary>Temperatura de la que depende cada rol por defecto.</summary>
    public static TemperatureSourceDto DefaultSource(FanRole role, KeySensorsDto keys)
    {
        var cpu = keys.CpuTemperature;
        var gpu = keys.GpuTemperature;
        return role switch
        {
            // Los de caja sacan el calor de la CPU y de la GPU: la más alta de las dos.
            FanRole.Case => new TemperatureSourceDto(TemperatureAggregation.Maximum, NonNull(cpu, gpu)),
            FanRole.Gpu => new TemperatureSourceDto(TemperatureAggregation.Single, NonNull(gpu ?? cpu)),
            _ => new TemperatureSourceDto(TemperatureAggregation.Single, NonNull(cpu ?? gpu)),
        };
    }

    private static string[] NonNull(params string?[] ids) => ids.Where(i => i is not null).Select(i => i!).ToArray();

    /// <summary>
    /// Ajustes de una salida nueva. Por seguridad empieza en modo BIOS (Céfiro no toma el control hasta que
    /// eliges un modo); la curva y la fuente ya vienen rellenas para activarlas con un clic.
    /// </summary>
    public static FanChannelSettingsDto ForChannel(FanChannelDto channel, KeySensorsDto keys)
    {
        var role = channel.DetectedRole;
        return new FanChannelSettingsDto
        {
            ChannelId = channel.Id,
            Name = DefaultName(channel),
            Role = role,
            Mode = FanMode.Bios,
            FixedPercent = role == FanRole.Pump ? 100 : 50,
            Curve = Curve(role, FanPreset.Balanced),
            Source = DefaultSource(role, keys),
            HysteresisCelsius = role == FanRole.Radiator ? 3 : 2,
            RampUpPercentPerSecond = role == FanRole.Pump ? 5 : 10,
            RampDownPercentPerSecond = role is FanRole.Radiator or FanRole.Pump ? 2 : 3,
            MinimumPercent = role switch
            {
                FanRole.Pump => SafetyGuard.PumpFloorPercent,
                FanRole.Radiator => SafetyGuard.RadiatorFloorPercent,
                FanRole.Gpu => Math.Max(30, channel.HardwareMinimumPercent),
                _ => 25,
            },
            StartPercent = role == FanRole.Pump ? 70 : 35,
            AllowStop = false,
        };
    }

    public static string DefaultName(FanChannelDto channel) => channel.DetectedRole switch
    {
        FanRole.Pump => $"Bomba ({channel.Header})",
        FanRole.Radiator => $"Radiador ({channel.Header})",
        FanRole.Gpu => channel.DefaultName,
        _ => $"Caja ({channel.Header})",
    };

    public static SafetySettingsDto DefaultSafety() => new();

    /// <summary>Aplica un modo predefinido conservando nombres, fuentes y grupos.</summary>
    public static FanChannelSettingsDto ApplyPreset(FanChannelSettingsDto channel, FanPreset preset)
    {
        if (preset == FanPreset.Maximum)
            return channel with { Mode = FanMode.Fixed, FixedPercent = 100 };

        // La GPU en Silencioso y Equilibrado se deja al driver: conserva su modo 0 RPM en reposo.
        if (channel.Role == FanRole.Gpu && preset is FanPreset.Silent or FanPreset.Balanced)
            return channel with { Mode = FanMode.Bios };

        return channel with
        {
            Mode = FanMode.Curve,
            Curve = Curve(channel.Role, preset),
            AllowStop = false,
        };
    }
}
