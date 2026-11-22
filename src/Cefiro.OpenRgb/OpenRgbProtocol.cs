using System.Buffers.Binary;

namespace Cefiro.OpenRgb;

/// <summary>
/// Constantes del SDK de red de OpenRGB.
/// Fuente: OpenRGB 1.1 (NetworkProtocol.h y Documentation/OpenRGBSDK.md).
/// </summary>
public static class OpenRgbProtocol
{
    /// <summary>Puerto por defecto ("ORGB" en un teclado telefónico).</summary>
    public const int DefaultPort = 6742;

    public const string DefaultHost = "127.0.0.1";

    /// <summary>Versión máxima del protocolo que implementa este cliente (OpenRGB 1.x).</summary>
    public const uint MaxSupportedVersion = 6;

    /// <summary>Tamaño fijo de la cabecera NetPacketHeader.</summary>
    public const int HeaderSize = 16;

    /// <summary>Tamaño máximo de paquete que acepta OpenRGB (OPENRGB_SDK_MAX_PACKET_SIZE).</summary>
    public const int MaxPacketSize = 8 * 1024 * 1024;

    internal static ReadOnlySpan<byte> Magic => "ORGB"u8;
}

/// <summary>Identificadores de paquete (pkt_id).</summary>
public enum PacketId : uint
{
    RequestControllerCount = 0,
    RequestControllerData = 1,
    Ack = 10,
    RequestProtocolVersion = 40,
    SetClientName = 50,
    SetServerName = 51,
    SetClientFlags = 52,
    SetServerFlags = 53,
    SetClientHostname = 54,
    SetServerHostname = 55,
    DeviceListUpdated = 100,
    DetectionStarted = 101,
    DetectionProgressChanged = 102,
    DetectionComplete = 103,
    RequestRescanDevices = 140,
    LogManagerLoggedEntry = 304,
    ResizeZone = 1000,
    ClearSegments = 1001,
    AddSegment = 1002,
    ConfigureZone = 1003,
    ConfigureDevice = 1004,
    SetHidden = 1005,
    UpdateLeds = 1050,
    UpdateZoneLeds = 1051,
    UpdateSingleLed = 1052,
    SetCustomMode = 1100,
    UpdateMode = 1101,
    SaveMode = 1102,
    UpdateZoneMode = 1103,
    SignalUpdate = 1150,
}

/// <summary>Códigos de estado de un ACK (protocolo 6+).</summary>
public enum PacketStatus : uint
{
    Ok = 0,
    ErrorGeneric = 1,
    ErrorUnsupported = 2,
    ErrorNotAllowed = 3,
    ErrorInvalidId = 4,
    ErrorInvalidData = 5,
}

[Flags]
public enum ClientFlags : uint
{
    None = 0,
    SupportsRgbController = 1u << 0,
    SupportsLogManager = 1u << 1,
    SupportsProfileManager = 1u << 2,
    SupportsPluginManager = 1u << 3,
    SupportsSettingsManager = 1u << 4,
    RequestLocalClient = 1u << 16,
}

[Flags]
public enum ServerFlags : uint
{
    None = 0,
    SupportsRgbController = 1u << 0,
    SupportsLogManager = 1u << 1,
    SupportsProfileManager = 1u << 2,
    SupportsPluginManager = 1u << 3,
    SupportsSettingsManager = 1u << 4,
    SupportsDetection = 1u << 5,
    SupportsDeviceInfo = 1u << 6,
    LocalClient = 1u << 16,
}

/// <summary>Tipo de dispositivo (device_type). El orden lo fija la API de OpenRGB.</summary>
public enum DeviceType
{
    Motherboard,
    Dram,
    Gpu,
    Cooler,
    LedStrip,
    Keyboard,
    Mouse,
    Mousemat,
    Headset,
    HeadsetStand,
    Gamepad,
    Light,
    Speaker,
    Virtual,
    Storage,
    Case,
    Microphone,
    Accessory,
    Keypad,
    Laptop,
    Monitor,
    Unknown,
}

public enum ZoneType : uint
{
    Single,
    Linear,
    Matrix,
    LinearLoop,
    MatrixLoopX,
    MatrixLoopY,
    Segmented,
}

[Flags]
public enum ModeFlags : uint
{
    None = 0,
    HasSpeed = 1u << 0,
    HasDirectionLeftRight = 1u << 1,
    HasDirectionUpDown = 1u << 2,
    HasDirectionHorizontalVertical = 1u << 3,
    HasBrightness = 1u << 4,
    HasPerLedColor = 1u << 5,
    HasModeSpecificColor = 1u << 6,
    HasRandomColor = 1u << 7,
    ManualSave = 1u << 8,
    AutomaticSave = 1u << 9,
    RequiresEntireDevice = 1u << 10,
    HasDirectionDiagonal = 1u << 11,
}

public enum ModeDirection : uint
{
    Left = 0,
    Right = 1,
    Up = 2,
    Down = 3,
    Horizontal = 4,
    Vertical = 5,
    UpLeft = 6,
    UpRight = 7,
    DownLeft = 8,
    DownRight = 9,
}

public enum ModeColorMode : uint
{
    None = 0,
    PerLed = 1,
    ModeSpecific = 2,
    Random = 3,
}

[Flags]
public enum ZoneFlags : uint
{
    None = 0,
    ConfigurableSizeEffectsOnly = 1u << 0,
    ConfigurableSize = 1u << 1,
    ConfigurableName = 1u << 2,
    ConfigurableType = 1u << 3,
    ConfigurableMatrixMap = 1u << 4,
    ConfigurableSegments = 1u << 5,
    ConfigurableDeviceSpecific = 1u << 6,
    ConfiguredSize = 1u << 12,
    ConfiguredName = 1u << 13,
    ConfiguredType = 1u << 14,
    ConfiguredMatrixMap = 1u << 15,
    ConfiguredSegments = 1u << 16,
    ConfiguredDeviceSpecific = 1u << 17,
    GeometryMayChange = 1u << 24,
}

[Flags]
public enum ControllerFlags : uint
{
    None = 0,
    Local = 1u << 0,
    Remote = 1u << 1,
    Virtual = 1u << 2,
    Hidden = 1u << 3,
    ResetBeforeUpdate = 1u << 8,
    ConfigurableName = 1u << 16,
    ConfigurableDeviceSpecific = 1u << 17,
    ConfiguredName = 1u << 24,
    ConfiguredDeviceSpecific = 1u << 25,
}

/// <summary>Motivo de NET_PACKET_ID_RGBCONTROLLER_SIGNALUPDATE (protocolo 6+).</summary>
public enum UpdateReason : uint
{
    UpdateLeds,
    UpdateMode,
    SaveMode,
    ConfigureZone,
    ClearSegments,
    AddSegment,
    Hidden,
    Unhidden,
    SetDeviceSpecificConfiguration,
    SetDeviceSpecificZoneConfiguration,
    ConfigureDevice,
    DeviceChanged,
}

/// <summary>Cabecera NetPacketHeader: "ORGB" + dev_id + pkt_id + pkt_size (little-endian).</summary>
public readonly record struct PacketHeader(uint DeviceId, PacketId Id, uint Size)
{
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < OpenRgbProtocol.HeaderSize)
            throw new ArgumentException("Búfer demasiado pequeño para la cabecera.", nameof(destination));

        OpenRgbProtocol.Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], DeviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)Id);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], Size);
    }

    public static PacketHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < OpenRgbProtocol.HeaderSize)
            throw new OpenRgbProtocolException("Cabecera incompleta.");

        if (!source[..4].SequenceEqual(OpenRgbProtocol.Magic))
            throw new OpenRgbProtocolException("Cabecera sin la firma \"ORGB\": el flujo está desincronizado.");

        return new PacketHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(source[4..]),
            (PacketId)BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[12..]));
    }
}

/// <summary>Error de formato o de protocolo al hablar con el servidor de OpenRGB.</summary>
public sealed class OpenRgbProtocolException : Exception
{
    public OpenRgbProtocolException(string message) : base(message) { }
    public OpenRgbProtocolException(string message, Exception inner) : base(message, inner) { }
}
