namespace Cefiro.OpenRgb;

/// <summary>Color tal y como viaja en el protocolo: 0x00BBGGRR (bytes R, G, B, 0).</summary>
public readonly record struct OpenRgbColor(byte R, byte G, byte B)
{
    public static readonly OpenRgbColor Black = new(0, 0, 0);

    public uint ToWire() => (uint)(R | (G << 8) | (B << 16));

    public static OpenRgbColor FromWire(uint value) =>
        new((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Mapa de matriz (alto x ancho) con el índice de LED de cada celda (0xFFFFFFFF = vacía).</summary>
public sealed class MatrixMap
{
    public const uint NoLed = 0xFFFFFFFF;

    public required uint Height { get; init; }
    public required uint Width { get; init; }
    public required uint[] Map { get; init; }
}

public sealed class OpenRgbMode
{
    public int Index { get; init; }
    public required string Name { get; init; }

    /// <summary>Valor interno del modo. Solo existe en protocolos &lt; 6 y hay que devolverlo en UpdateMode/SaveMode.</summary>
    public int Value { get; set; }

    public ModeFlags Flags { get; init; }
    public uint SpeedMin { get; init; }
    public uint SpeedMax { get; init; }
    public uint BrightnessMin { get; init; }
    public uint BrightnessMax { get; init; }
    public uint ColorsMin { get; init; }
    public uint ColorsMax { get; init; }

    public uint Speed { get; set; }
    public uint Brightness { get; set; }
    public ModeDirection Direction { get; set; }
    public ModeColorMode ColorMode { get; set; }
    public OpenRgbColor[] Colors { get; set; } = [];

    public bool HasSpeed => Flags.HasFlag(ModeFlags.HasSpeed);
    public bool HasBrightness => Flags.HasFlag(ModeFlags.HasBrightness);
    public bool HasPerLedColor => Flags.HasFlag(ModeFlags.HasPerLedColor);

    public OpenRgbMode Clone() => new()
    {
        Index = Index,
        Name = Name,
        Value = Value,
        Flags = Flags,
        SpeedMin = SpeedMin,
        SpeedMax = SpeedMax,
        BrightnessMin = BrightnessMin,
        BrightnessMax = BrightnessMax,
        ColorsMin = ColorsMin,
        ColorsMax = ColorsMax,
        Speed = Speed,
        Brightness = Brightness,
        Direction = Direction,
        ColorMode = ColorMode,
        Colors = (OpenRgbColor[])Colors.Clone(),
    };

    public override string ToString() => Name;
}

public sealed class OpenRgbSegment
{
    public required string Name { get; init; }
    public ZoneType Type { get; init; }
    public uint StartIndex { get; init; }
    public uint LedsCount { get; init; }
    public MatrixMap? Matrix { get; init; }
    public uint Flags { get; init; }
}

public sealed class OpenRgbZone
{
    public int Index { get; init; }
    public required string Name { get; init; }
    public ZoneType Type { get; init; }
    public uint LedsMin { get; init; }
    public uint LedsMax { get; init; }
    public uint LedsCount { get; init; }
    public MatrixMap? Matrix { get; init; }
    public IReadOnlyList<OpenRgbSegment> Segments { get; init; } = [];
    public ZoneFlags Flags { get; init; }

    /// <summary>Modo activo propio de la zona (protocolo 6+). -1 = usa el modo del dispositivo.</summary>
    public int ActiveMode { get; init; } = -1;

    public IReadOnlyList<OpenRgbMode> Modes { get; init; } = [];
    public string DisplayName { get; init; } = "";

    /// <summary>Índice del primer LED de esta zona dentro de la lista de LEDs del dispositivo.</summary>
    public int StartLedIndex { get; init; }

    public string BestName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    /// <summary>Zona cuyo número de LEDs se puede ajustar (p. ej. cabezales ARGB de la placa).</summary>
    public bool IsResizable => LedsMin != LedsMax;

    public override string ToString() => $"{BestName} ({LedsCount} LEDs)";
}

public sealed class OpenRgbLed
{
    public required string Name { get; init; }

    /// <summary>Valor interno del LED (solo protocolos &lt; 6).</summary>
    public uint Value { get; init; }
}

/// <summary>Copia en el cliente de un RGBController del servidor.</summary>
public sealed class OpenRgbDevice
{
    /// <summary>
    /// Identificador que se usa como pkt_dev_id: ID único (protocolo 6+) o índice (protocolos 0-5).
    /// </summary>
    public uint Id { get; init; }

    public DeviceType Type { get; init; }
    public required string Name { get; init; }
    public string Vendor { get; init; } = "";
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Location { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int ActiveMode { get; set; }
    public IReadOnlyList<OpenRgbMode> Modes { get; init; } = [];
    public IReadOnlyList<OpenRgbZone> Zones { get; init; } = [];
    public IReadOnlyList<OpenRgbLed> Leds { get; init; } = [];
    public OpenRgbColor[] Colors { get; init; } = [];
    public IReadOnlyList<string> LedDisplayNames { get; init; } = [];
    public ControllerFlags Flags { get; init; }
    public string Configuration { get; init; } = "";

    public string BestName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    /// <summary>
    /// Modo para enviar colores LED a LED en tiempo real. Por convención en OpenRGB se llama "Direct";
    /// si no existe, se usa el primer modo con colores por LED.
    /// </summary>
    public int FindDirectModeIndex()
    {
        for (var i = 0; i < Modes.Count; i++)
        {
            if (string.Equals(Modes[i].Name, "Direct", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        for (var i = 0; i < Modes.Count; i++)
        {
            if (Modes[i].HasPerLedColor)
                return i;
        }

        return -1;
    }

    public override string ToString() => $"{BestName} [{Type}, {Leds.Count} LEDs]";
}
