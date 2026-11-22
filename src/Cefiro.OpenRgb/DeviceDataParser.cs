namespace Cefiro.OpenRgb;

/// <summary>
/// Interpreta el bloque de NET_PACKET_ID_REQUEST_CONTROLLER_DATA según la versión de protocolo negociada.
/// El formato sigue RGBController::GetDeviceDescriptionData() de OpenRGB 1.1.
/// </summary>
public static class DeviceDataParser
{
    /// <param name="payload">Carga útil completa de la respuesta (empieza por data_size).</param>
    /// <param name="protocolVersion">Versión enviada en la petición.</param>
    /// <param name="deviceId">ID que se usará como pkt_dev_id para este dispositivo.</param>
    public static OpenRgbDevice Parse(ReadOnlySpan<byte> payload, uint protocolVersion, uint deviceId)
    {
        var r = new WireReader(payload);

        var dataSize = r.ReadUInt32();
        if (dataSize > payload.Length)
        {
            throw new OpenRgbProtocolException(
                $"data_size ({dataSize}) mayor que el paquete recibido ({payload.Length}).");
        }

        var rawType = r.ReadInt32();
        var type = rawType >= 0 && rawType <= (int)DeviceType.Unknown ? (DeviceType)rawType : DeviceType.Unknown;

        var name = r.ReadString16();
        var vendor = protocolVersion >= 1 ? r.ReadString16() : "";
        var description = r.ReadString16();
        var version = r.ReadString16();
        var serial = r.ReadString16();
        var location = r.ReadString16();

        var numModes = r.ReadUInt16();
        var activeMode = r.ReadInt32();
        var modes = new OpenRgbMode[numModes];
        for (var i = 0; i < numModes; i++)
            modes[i] = ReadMode(ref r, protocolVersion, i);

        var numZones = r.ReadUInt16();
        var zones = new OpenRgbZone[numZones];
        var ledCursor = 0;
        for (var i = 0; i < numZones; i++)
        {
            zones[i] = ReadZone(ref r, protocolVersion, i, ledCursor);
            ledCursor += (int)zones[i].LedsCount;
        }

        var numLeds = r.ReadUInt16();
        var leds = new OpenRgbLed[numLeds];
        for (var i = 0; i < numLeds; i++)
        {
            var ledName = r.ReadString16();
            var ledValue = protocolVersion < 6 ? r.ReadUInt32() : 0u;
            leds[i] = new OpenRgbLed { Name = ledName, Value = ledValue };
        }

        var numColors = r.ReadUInt16();
        var colors = new OpenRgbColor[numColors];
        for (var i = 0; i < numColors; i++)
            colors[i] = r.ReadColor();

        var ledDisplayNames = Array.Empty<string>();
        var flags = ControllerFlags.None;
        if (protocolVersion >= 5)
        {
            var numNames = r.ReadUInt16();
            ledDisplayNames = new string[numNames];
            for (var i = 0; i < numNames; i++)
                ledDisplayNames[i] = r.ReadString16();

            flags = (ControllerFlags)r.ReadUInt32();
        }

        var displayName = "";
        var configuration = "";
        if (protocolVersion >= 6)
        {
            displayName = r.ReadString16();
            configuration = r.ReadString32();
        }

        return new OpenRgbDevice
        {
            Id = deviceId,
            Type = type,
            Name = name,
            Vendor = vendor,
            Description = description,
            Version = version,
            Serial = serial,
            Location = location,
            DisplayName = displayName,
            ActiveMode = activeMode,
            Modes = modes,
            Zones = zones,
            Leds = leds,
            Colors = colors,
            LedDisplayNames = ledDisplayNames,
            Flags = flags,
            Configuration = configuration,
        };
    }

    internal static OpenRgbMode ReadMode(ref WireReader r, uint protocolVersion, int index)
    {
        var name = r.ReadString16();
        var value = protocolVersion < 6 ? r.ReadInt32() : 0;
        var flags = (ModeFlags)r.ReadUInt32();
        var speedMin = r.ReadUInt32();
        var speedMax = r.ReadUInt32();

        uint brightnessMin = 0, brightnessMax = 0;
        if (protocolVersion >= 3)
        {
            brightnessMin = r.ReadUInt32();
            brightnessMax = r.ReadUInt32();
        }

        var colorsMin = r.ReadUInt32();
        var colorsMax = r.ReadUInt32();
        var speed = r.ReadUInt32();
        var brightness = protocolVersion >= 3 ? r.ReadUInt32() : 0u;
        var direction = (ModeDirection)r.ReadUInt32();
        var colorMode = (ModeColorMode)r.ReadUInt32();

        var numColors = r.ReadUInt16();
        var colors = new OpenRgbColor[numColors];
        for (var i = 0; i < numColors; i++)
            colors[i] = r.ReadColor();

        return new OpenRgbMode
        {
            Index = index,
            Name = name,
            Value = value,
            Flags = flags,
            SpeedMin = speedMin,
            SpeedMax = speedMax,
            BrightnessMin = brightnessMin,
            BrightnessMax = brightnessMax,
            ColorsMin = colorsMin,
            ColorsMax = colorsMax,
            Speed = speed,
            Brightness = brightness,
            Direction = direction,
            ColorMode = colorMode,
            Colors = colors,
        };
    }

    private static OpenRgbZone ReadZone(ref WireReader r, uint protocolVersion, int index, int startLedIndex)
    {
        var name = r.ReadString16();
        var type = (ZoneType)r.ReadUInt32();
        var ledsMin = r.ReadUInt32();
        var ledsMax = r.ReadUInt32();
        var ledsCount = r.ReadUInt32();
        var matrix = ReadMatrixMap(ref r);

        var segments = Array.Empty<OpenRgbSegment>();
        if (protocolVersion >= 4)
        {
            var numSegments = r.ReadUInt16();
            segments = new OpenRgbSegment[numSegments];
            for (var i = 0; i < numSegments; i++)
                segments[i] = ReadSegment(ref r, protocolVersion);
        }

        var zoneFlags = protocolVersion >= 5 ? (ZoneFlags)r.ReadUInt32() : ZoneFlags.None;

        var activeMode = -1;
        var zoneModes = Array.Empty<OpenRgbMode>();
        var displayName = "";
        if (protocolVersion >= 6)
        {
            activeMode = r.ReadInt32();
            var numModes = r.ReadUInt16();
            zoneModes = new OpenRgbMode[numModes];
            for (var i = 0; i < numModes; i++)
                zoneModes[i] = ReadMode(ref r, protocolVersion, i);
            displayName = r.ReadString16();
        }

        return new OpenRgbZone
        {
            Index = index,
            Name = name,
            Type = type,
            LedsMin = ledsMin,
            LedsMax = ledsMax,
            LedsCount = ledsCount,
            Matrix = matrix,
            Segments = segments,
            Flags = zoneFlags,
            ActiveMode = activeMode,
            Modes = zoneModes,
            DisplayName = displayName,
            StartLedIndex = startLedIndex,
        };
    }

    private static OpenRgbSegment ReadSegment(ref WireReader r, uint protocolVersion)
    {
        var name = r.ReadString16();
        var type = (ZoneType)r.ReadUInt32();
        var start = r.ReadUInt32();
        var count = r.ReadUInt32();

        MatrixMap? matrix = null;
        uint flags = 0;
        if (protocolVersion >= 6)
        {
            matrix = ReadMatrixMap(ref r);
            flags = r.ReadUInt32();
        }

        return new OpenRgbSegment
        {
            Name = name,
            Type = type,
            StartIndex = start,
            LedsCount = count,
            Matrix = matrix,
            Flags = flags,
        };
    }

    /// <summary>ushort con la longitud en bytes; si es 0 no hay mapa.</summary>
    private static MatrixMap? ReadMatrixMap(ref WireReader r)
    {
        var length = r.ReadUInt16();
        if (length == 0)
            return null;

        if (length < 8)
            throw new OpenRgbProtocolException($"Mapa de matriz con longitud inválida ({length}).");

        var height = r.ReadUInt32();
        var width = r.ReadUInt32();
        var cells = (length - 8) / 4;

        if ((ulong)height * width != (ulong)cells)
            throw new OpenRgbProtocolException($"Mapa de matriz incoherente: {height}x{width} frente a {cells} celdas.");

        var map = new uint[cells];
        for (var i = 0; i < cells; i++)
            map[i] = r.ReadUInt32();

        return new MatrixMap { Height = height, Width = width, Map = map };
    }
}
