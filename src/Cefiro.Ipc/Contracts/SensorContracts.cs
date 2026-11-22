namespace Cefiro.Ipc.Contracts;

public sealed record HelloRequest(string Client, int ProtocolVersion);

public sealed record HelloResponse(string ServiceVersion, int ProtocolVersion, bool DemoMode, string MachineName);

public enum SensorKind
{
    Temperature,
    Load,
    Clock,
    Power,
    Voltage,
    Fan,
    Control,
    Data,
    Throughput,
    Other,
}

public enum DeviceCategory
{
    Cpu,
    Gpu,
    Motherboard,
    Memory,
    Other,
}

/// <param name="Id">Identificador estable (el de LibreHardwareMonitor, p. ej. "/amdcpu/0/temperature/2").</param>
/// <param name="Name">Nombre legible en español.</param>
/// <param name="Device">Nombre del componente (p. ej. "AMD Ryzen 7 7700X").</param>
public sealed record SensorDto(string Id, string Name, string Device, DeviceCategory Category, SensorKind Kind, string Unit);

/// <summary>Sensores principales que usa el panel (null si no existen en este PC).</summary>
public sealed record KeySensorsDto
{
    public string? CpuTemperature { get; init; }
    public string? CpuLoad { get; init; }
    public string? CpuClock { get; init; }
    public string? CpuPower { get; init; }
    public string? GpuTemperature { get; init; }
    public string? GpuHotSpot { get; init; }
    public string? GpuLoad { get; init; }
    public string? GpuClock { get; init; }
    public string? GpuPower { get; init; }
    public string? GpuMemoryUsed { get; init; }
    public string? GpuMemoryTotal { get; init; }
    public string? RamLoad { get; init; }
    public string? RamUsed { get; init; }
    public string? RamAvailable { get; init; }
    public string? RamTemperature { get; init; }
    public string? BoardTemperature { get; init; }
    public string? VrmTemperature { get; init; }
    public string? ChipsetTemperature { get; init; }
}

public sealed record BackendInfoDto(
    string Name,
    bool IsAdministrator,
    bool PawnIoInstalled,
    string? Motherboard,
    string? SuperIoChip,
    string? Cpu,
    string? Gpu,
    IReadOnlyList<string> Warnings);

/// <summary>Todo lo que la interfaz necesita saber del hardware. Cambia de versión si cambia la lista.</summary>
public sealed record CatalogDto(
    int Version,
    IReadOnlyList<SensorDto> Sensors,
    KeySensorsDto Keys,
    IReadOnlyList<FanChannelDto> FanChannels,
    BackendInfoDto Backend);

/// <summary>Valores de todos los sensores, en el mismo orden que <see cref="CatalogDto.Sensors"/>.</summary>
public sealed record SensorFrameDto(int CatalogVersion, long Timestamp, float?[] Values);

public enum HistoryRange
{
    Last60Seconds,
    Last10Minutes,
    LastHour,
}

public sealed record HistoryRequest(IReadOnlyList<string> SensorIds, HistoryRange Range);

public sealed record HistorySeriesDto(string SensorId, float?[] Values);

/// <param name="EndTimestamp">Marca de tiempo (Unix ms) del último punto.</param>
/// <param name="IntervalMilliseconds">Separación entre puntos.</param>
public sealed record HistoryResponse(long EndTimestamp, int IntervalMilliseconds, IReadOnlyList<HistorySeriesDto> Series);

public enum ConflictSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>Programa o ajuste de Windows que puede pelearse con Céfiro por el hardware.</summary>
public sealed record ConflictDto(string Id, string Name, string Detail, ConflictSeverity Severity);

public sealed record StatusDto
{
    public bool HardwareReady { get; init; }
    public bool OpenRgbConnected { get; init; }
    public bool EmergencyActive { get; init; }
    public string? EmergencyReason { get; init; }
    public string? ActiveProfileId { get; init; }
    public IReadOnlyList<ConflictDto> Conflicts { get; init; } = [];
    public double ServiceCpuPercent { get; init; }
    public long ServiceMemoryBytes { get; init; }
    public long UptimeSeconds { get; init; }
    public double LastUpdateMilliseconds { get; init; }
    public string? LastError { get; init; }
}

public enum AlertKind
{
    TemperatureHigh,
    FanStopped,
    Emergency,
    EmergencyCleared,
    SensorLost,
    Conflict,
    Info,
}

public sealed record AlertDto(AlertKind Kind, string Title, string Message, long Timestamp);
