namespace Cefiro.Core.Lighting;

public sealed record LightZone(int Index, string Name, string Type, int LedCount, int MinLeds, int MaxLeds, int StartLed)
{
    public bool Resizable => MinLeds != MaxLeds;
}

/// <summary>Dispositivo RGB tal y como lo ve el motor de iluminación.</summary>
public sealed class LightDevice
{
    /// <summary>Clave estable entre redetecciones: nombre + ubicación (USB/SMBus).</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }
    public string Vendor { get; init; } = "";

    /// <summary>Tipo (Motherboard, Dram, Gpu…).</summary>
    public string Kind { get; init; } = "";

    public string Location { get; init; } = "";
    public required IReadOnlyList<LightZone> Zones { get; init; }
    public int LedCount { get; init; }
    public IReadOnlyList<string> NativeModes { get; init; } = [];
    public IReadOnlyList<string> LedNames { get; init; } = [];

    /// <summary>Datos propios del backend (p. ej. el OpenRgbDevice).</summary>
    public object? Handle { get; init; }

    /// <summary>La RAM por SMBus es lenta: menos fotogramas por segundo.</summary>
    public int MaxFps => Kind == "Dram" ? 15 : 60;
}

/// <summary>Origen y destino de la iluminación (OpenRGB real o simulado en modo demo).</summary>
public interface ILightingBackend : IAsyncDisposable
{
    bool IsConnected { get; }
    string? ServerName { get; }
    int ProtocolVersion { get; }

    /// <summary>La lista de dispositivos ha cambiado en el servidor (o se ha perdido la conexión).</summary>
    event EventHandler? DevicesChanged;

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LightDevice>> GetDevicesAsync(CancellationToken cancellationToken);

    /// <summary>Pone el dispositivo en su modo de control LED a LED.</summary>
    Task SetDirectModeAsync(LightDevice device, CancellationToken cancellationToken);

    /// <summary>Activa un modo propio del dispositivo.</summary>
    Task SetNativeModeAsync(LightDevice device, string mode, IReadOnlyList<Rgb> colors, double speed01, double brightness01, CancellationToken cancellationToken);

    /// <summary>Envía el color de todos los LEDs del dispositivo.</summary>
    ValueTask SendColorsAsync(LightDevice device, ReadOnlyMemory<Rgb> colors, CancellationToken cancellationToken);

    Task ResizeZoneAsync(LightDevice device, int zoneIndex, int ledCount, CancellationToken cancellationToken);

    /// <summary>Envíos todavía sin confirmar (para no saturar dispositivos lentos).</summary>
    int PendingUpdates(LightDevice device);
}
