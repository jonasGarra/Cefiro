using Cefiro.Core.Hardware;
using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Fans;

/// <summary>
/// Comprueba y corrige cualquier configuración de ventiladores antes de usarla o guardarla.
/// Nunca falla: lo que no es válido se sustituye por un valor seguro.
/// </summary>
public static class FanSettingsValidator
{
    public static FanSettingsDto Validate(
        FanSettingsDto? input,
        IReadOnlyList<FanChannelDto> channels,
        IReadOnlyList<SensorDescriptor> sensors,
        KeySensorsDto keys,
        IReadOnlyDictionary<string, string>? pumpsSeen = null)
    {
        var temperatureIds = sensors
            .Where(s => s.Kind == SensorKind.Temperature)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        var inputChannels = (input?.Channels ?? [])
            .Where(c => c?.ChannelId is not null)
            .GroupBy(c => c.ChannelId)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var result = new List<FanChannelSettingsDto>(channels.Count);
        foreach (var channel in channels)
        {
            var defaults = FanDefaults.ForChannel(channel, keys);
            var given = inputChannels.GetValueOrDefault(channel.Id) ?? defaults;
            result.Add(ValidateChannel(given, channel, defaults, temperatureIds, pumpsSeen));
        }

        var validIds = result.Select(c => c.ChannelId).ToHashSet(StringComparer.Ordinal);
        var groups = (input?.Groups ?? [])
            .Where(g => g?.Id is not null && !string.IsNullOrWhiteSpace(g.Name))
            .Select(g => new FanGroupDto(
                g.Id,
                Truncate(g.Name.Trim(), 40),
                (g.ChannelIds ?? []).Where(validIds.Contains).Distinct().ToList()))
            .GroupBy(g => g.Id)
            .Select(g => g.Last())
            .ToList();

        // Una salida solo puede estar en un grupo: el último que la declare.
        var groupOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in groups)
            foreach (var id in g.ChannelIds)
                groupOf[id] = g.Id;

        result = result.Select(c => c with { GroupId = groupOf.GetValueOrDefault(c.ChannelId) }).ToList();

        return new FanSettingsDto(result, groups, ValidateSafety(input?.Safety));
    }

    public static FanChannelSettingsDto ValidateChannel(
        FanChannelSettingsDto given,
        FanChannelDto channel,
        FanChannelSettingsDto defaults,
        IReadOnlySet<string> temperatureIds,
        IReadOnlyDictionary<string, string>? pumpsSeen)
    {
        // Rol: si una salida ha girado como una bomba, se protege como bomba aunque se diga otra cosa.
        var role = given.Role;
        if (pumpsSeen?.ContainsKey(channel.Id) == true)
            role = FanRole.Pump;
        if (channel.DetectedRole == FanRole.Gpu)
            role = FanRole.Gpu; // los ventiladores de la GPU no pueden ser otra cosa

        var floor = SafetyGuard.FloorFor(role, channel.HardwareMinimumPercent);

        var mode = Enum.IsDefined(given.Mode) ? given.Mode : FanMode.Bios;

        var sourceIds = (given.Source?.SensorIds ?? [])
            .Where(temperatureIds.Contains)
            .Distinct()
            .Take(8)
            .ToList();
        var aggregation = given.Source is { } src && Enum.IsDefined(src.Aggregation) ? src.Aggregation : TemperatureAggregation.Single;
        if (sourceIds.Count == 0)
        {
            sourceIds = [.. defaults.Source.SensorIds];
            aggregation = defaults.Source.Aggregation;
        }

        if (aggregation == TemperatureAggregation.Single && sourceIds.Count > 1)
            sourceIds = [sourceIds[0]];

        // Sin ninguna temperatura disponible no puede haber curva.
        if (mode == FanMode.Curve && sourceIds.Count == 0)
            mode = FanMode.Bios;

        var minimum = Clamp(given.MinimumPercent, 0, 100, defaults.MinimumPercent);
        minimum = Math.Max(minimum, floor);

        var allowStop = given.AllowStop && SafetyGuard.CanStop(role);
        var start = Clamp(given.StartPercent, 0, 100, defaults.StartPercent);
        start = Math.Max(start, minimum);

        var fixedPercent = Clamp(given.FixedPercent, 0, 100, defaults.FixedPercent);
        if (!(fixedPercent <= 0 && allowStop))
            fixedPercent = Math.Max(fixedPercent, floor);

        var curve = FanCurve.Normalize(given.Curve, defaults.Curve);

        return new FanChannelSettingsDto
        {
            ChannelId = channel.Id,
            Name = string.IsNullOrWhiteSpace(given.Name) ? defaults.Name : Truncate(given.Name.Trim(), 40),
            Role = role,
            Mode = mode,
            FixedPercent = MathF.Round(fixedPercent, 1),
            Curve = curve,
            Source = new TemperatureSourceDto(aggregation, sourceIds),
            HysteresisCelsius = Clamp(given.HysteresisCelsius, 0, 15, defaults.HysteresisCelsius),
            RampUpPercentPerSecond = Clamp(given.RampUpPercentPerSecond, 0.5f, 100, defaults.RampUpPercentPerSecond),
            RampDownPercentPerSecond = Clamp(given.RampDownPercentPerSecond, 0.5f, 100, defaults.RampDownPercentPerSecond),
            MinimumPercent = MathF.Round(minimum, 1),
            StartPercent = MathF.Round(start, 1),
            AllowStop = allowStop,
            GroupId = given.GroupId,
        };
    }

    public static SafetySettingsDto ValidateSafety(SafetySettingsDto? s)
    {
        var d = FanDefaults.DefaultSafety();
        if (s is null)
            return d;

        return new SafetySettingsDto
        {
            CpuCriticalCelsius = Clamp(s.CpuCriticalCelsius, 60, 100, d.CpuCriticalCelsius),
            GpuCriticalCelsius = Clamp(s.GpuCriticalCelsius, 60, 95, d.GpuCriticalCelsius),
            RecoveryMarginCelsius = Clamp(s.RecoveryMarginCelsius, 2, 20, d.RecoveryMarginCelsius),
            RecoveryHoldSeconds = (int)Clamp(s.RecoveryHoldSeconds, 5, 120, d.RecoveryHoldSeconds),
            SensorTimeoutSeconds = (int)Clamp(s.SensorTimeoutSeconds, 2, 30, d.SensorTimeoutSeconds),
            CpuWarningCelsius = Clamp(s.CpuWarningCelsius, 50, 100, d.CpuWarningCelsius),
            GpuWarningCelsius = Clamp(s.GpuWarningCelsius, 50, 95, d.GpuWarningCelsius),
        };
    }

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
