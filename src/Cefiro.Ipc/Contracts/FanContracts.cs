namespace Cefiro.Ipc.Contracts;

/// <summary>Qué hay conectado a una salida de ventilador. Determina los límites de seguridad.</summary>
public enum FanRole
{
    /// <summary>Ventiladores de caja. Pueden pararse (modo 0 RPM).</summary>
    Case,

    /// <summary>Ventiladores del radiador de la líquida. Nunca por debajo del 30 %.</summary>
    Radiator,

    /// <summary>Bomba de la líquida. Nunca por debajo del 60 % y nunca parada.</summary>
    Pump,

    /// <summary>Ventiladores de la tarjeta gráfica (el firmware impone su mínimo).</summary>
    Gpu,
}

public enum FanMode
{
    /// <summary>Lo controla la BIOS (o el driver de la GPU).</summary>
    Bios,
    Fixed,
    Curve,
}

public enum TemperatureAggregation
{
    Single,
    Maximum,
    Average,
}

/// <param name="Id">ID estable de la salida (el del sensor de control de LHM).</param>
/// <param name="Header">Conector físico (CPU_FAN1, PUMP_FAN1, SYS_FAN1…) o "GPU 1/2".</param>
/// <param name="DetectedRole">Rol deducido por Céfiro (por nombre y por RPM).</param>
/// <param name="RoleEvidence">Explicación de por qué se dedujo ese rol.</param>
public sealed record FanChannelDto(
    string Id,
    string Header,
    string Device,
    string DefaultName,
    FanRole DetectedRole,
    string RoleEvidence,
    string? RpmSensorId,
    string ControlSensorId,
    float HardwareMinimumPercent,
    float HardwareMaximumPercent);

public sealed record CurvePointDto(float Temperature, float Percent);

public sealed record TemperatureSourceDto(TemperatureAggregation Aggregation, IReadOnlyList<string> SensorIds);

public sealed record FanChannelSettingsDto
{
    public required string ChannelId { get; init; }
    public required string Name { get; init; }
    public FanRole Role { get; init; }
    public FanMode Mode { get; init; } = FanMode.Bios;
    public float FixedPercent { get; init; } = 50;
    public IReadOnlyList<CurvePointDto> Curve { get; init; } = [];
    public TemperatureSourceDto Source { get; init; } = new(TemperatureAggregation.Single, []);

    /// <summary>Cuánto tiene que BAJAR la temperatura para que la curva reaccione hacia abajo.</summary>
    public float HysteresisCelsius { get; init; } = 2;

    public float RampUpPercentPerSecond { get; init; } = 10;
    public float RampDownPercentPerSecond { get; init; } = 3;

    /// <summary>Por debajo de esto el ventilador no gira bien: se sube a este valor (o se para si AllowStop).</summary>
    public float MinimumPercent { get; init; } = 20;

    /// <summary>Empujón para arrancar un ventilador parado.</summary>
    public float StartPercent { get; init; } = 35;

    /// <summary>Permite pararlo (0 %) cuando la curva pide menos del mínimo. Solo para ventiladores de caja.</summary>
    public bool AllowStop { get; init; }

    public string? GroupId { get; init; }
}

public sealed record FanGroupDto(string Id, string Name, IReadOnlyList<string> ChannelIds);

public sealed record SafetySettingsDto
{
    /// <summary>Temperatura de la CPU que activa el modo de emergencia (todo al 100 %).</summary>
    public float CpuCriticalCelsius { get; init; } = 90;

    public float GpuCriticalCelsius { get; init; } = 85;

    /// <summary>Margen por debajo del límite para salir de la emergencia.</summary>
    public float RecoveryMarginCelsius { get; init; } = 8;

    /// <summary>Segundos seguidos por debajo del margen antes de salir de la emergencia.</summary>
    public int RecoveryHoldSeconds { get; init; } = 15;

    /// <summary>Segundos sin lectura válida tras los que un sensor se da por perdido.</summary>
    public int SensorTimeoutSeconds { get; init; } = 3;

    /// <summary>Aviso de temperatura alta (no emergencia).</summary>
    public float CpuWarningCelsius { get; init; } = 85;

    public float GpuWarningCelsius { get; init; } = 80;
}

public sealed record FanSettingsDto(
    IReadOnlyList<FanChannelSettingsDto> Channels,
    IReadOnlyList<FanGroupDto> Groups,
    SafetySettingsDto Safety);

public sealed record FanStateDto(
    string ChannelId,
    float? Rpm,
    float? OutputPercent,
    float? TargetPercent,
    FanMode EffectiveMode,
    float? SourceTemperature,
    string? Note);

public sealed record FanFrameDto(long Timestamp, IReadOnlyList<FanStateDto> Channels, bool EmergencyActive);

public enum FanPreset
{
    Silent,
    Balanced,
    Performance,
    Maximum,
}

public sealed record ApplyPresetRequest(FanPreset Preset, IReadOnlyList<string>? ChannelIds);

public sealed record CalibrationRequest(IReadOnlyList<string> ChannelIds);

public sealed record CalibrationPointDto(float Percent, float Rpm);

public sealed record CalibrationResultDto(
    string ChannelId,
    IReadOnlyList<CalibrationPointDto> Points,
    float? StopPercent,
    float? StartPercent,
    float? MaxRpm,
    string Status);

public sealed record CalibrationStatusDto(
    bool Running,
    string? CurrentChannelId,
    float Progress,
    IReadOnlyList<CalibrationResultDto> Results,
    string? Message);
