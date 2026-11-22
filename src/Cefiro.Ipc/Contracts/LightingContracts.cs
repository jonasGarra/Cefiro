namespace Cefiro.Ipc.Contracts;

public enum EffectKind
{
    Off,
    Static,
    Breathing,
    Rainbow,
    Wave,
    ColorCycle,
    Comet,
    Temperature,
    Music,

    /// <summary>Efecto propio del dispositivo (lo ejecuta su firmware: no gasta CPU).</summary>
    DeviceMode,
}

/// <summary>Configuración de un efecto. Los colores van en hexadecimal "#RRGGBB".</summary>
public sealed record EffectDto
{
    public EffectKind Kind { get; init; } = EffectKind.Static;
    public IReadOnlyList<string> Colors { get; init; } = ["#00E5FF"];

    /// <summary>Velocidad relativa (0,1 a 5; 1 = normal).</summary>
    public float Speed { get; init; } = 1;

    /// <summary>Brillo propio del efecto (0-100 %), que se multiplica por el brillo general.</summary>
    public float Brightness { get; init; } = 100;

    /// <summary>Sentido de los efectos que se desplazan.</summary>
    public bool Reverse { get; init; }

    // Efecto "Temperatura"
    public string? SensorId { get; init; }
    public float MinTemperature { get; init; } = 35;
    public float MaxTemperature { get; init; } = 85;

    // Efecto "Música"
    public float Sensitivity { get; init; } = 1;

    // Efecto "DeviceMode"
    public string? NativeMode { get; init; }
}

/// <summary>A qué LEDs se aplica una capa. Sin zona = todo el dispositivo; con LedIndex = un solo LED.</summary>
public sealed record LightingTargetDto(string DeviceKey, int? ZoneIndex = null, int? LedIndex = null);

public sealed record LightingLayerDto(LightingTargetDto Target, EffectDto Effect);

public enum NightAction
{
    TurnOff,
    Dim,
}

public sealed record NightModeDto
{
    public bool Enabled { get; init; }
    public string Start { get; init; } = "23:30";
    public string End { get; init; } = "07:30";
    public NightAction Action { get; init; } = NightAction.Dim;
    public float DimBrightness { get; init; } = 15;
}

/// <summary>Número de LEDs de una zona redimensionable (los cabezales ARGB de la placa).</summary>
public sealed record ZoneSizeDto(string DeviceKey, int ZoneIndex, int LedCount);

public sealed record LightingSettingsDto
{
    public bool Enabled { get; init; } = true;

    /// <summary>Brillo general (0-100 %).</summary>
    public float Brightness { get; init; } = 100;

    /// <summary>Multiplicador general de velocidad de los efectos.</summary>
    public float SpeedMultiplier { get; init; } = 1;

    /// <summary>Un único efecto para todo (sincronizar con un clic).</summary>
    public bool SyncAll { get; init; } = true;

    public EffectDto SyncEffect { get; init; } = new();

    /// <summary>Capas por dispositivo, zona o LED (cuando no se sincroniza todo). La última gana.</summary>
    public IReadOnlyList<LightingLayerDto> Layers { get; init; } = [];

    public IReadOnlyList<string> Favorites { get; init; } =
        ["#00E5FF", "#7C4DFF", "#FF2E88", "#FF6D00", "#00E676", "#FFFFFF"];

    public NightModeDto Night { get; init; } = new();
    public IReadOnlyList<ZoneSizeDto> ZoneSizes { get; init; } = [];

    /// <summary>Fotogramas por segundo de los efectos animados.</summary>
    public int Fps { get; init; } = 30;
}

public sealed record LightingZoneDto(
    int Index,
    string Name,
    string Type,
    int LedCount,
    int MinLeds,
    int MaxLeds,
    bool Resizable,
    int StartLed);

public sealed record LightingDeviceDto(
    string Key,
    string Name,
    string Vendor,
    string Kind,
    string Location,
    int LedCount,
    IReadOnlyList<LightingZoneDto> Zones,
    IReadOnlyList<string> NativeModes,
    IReadOnlyList<string> LedNames);

public sealed record LightingStateDto(
    bool Connected,
    string? ServerName,
    int ProtocolVersion,
    IReadOnlyList<LightingDeviceDto> Devices,
    LightingSettingsDto Settings,
    bool NightActive,
    string? Error);

/// <summary>Colores actuales de un dispositivo: "RRGGBB" seguidos, uno por LED (compacto para las vistas previas).</summary>
public sealed record DeviceColorsDto(string DeviceKey, string Colors);

public sealed record LightingFrameDto(long Timestamp, IReadOnlyList<DeviceColorsDto> Devices);

public sealed record ResizeZoneRequest(string DeviceKey, int ZoneIndex, int LedCount);

/// <summary>Hace parpadear en blanco un dispositivo, una zona o un LED para localizarlo.</summary>
public sealed record IdentifyRequest(string DeviceKey, int? ZoneIndex, int? LedIndex, int DurationMs = 3000);

/// <summary>Espectro de audio que envía la interfaz para el efecto Música (16 bandas, 0-1).</summary>
public sealed record AudioSpectrumDto(float[] Bands, float Level);
