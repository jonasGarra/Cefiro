namespace Cefiro.OpenRgb;

/// <summary>Construcción de las cargas útiles de los paquetes que envía el cliente.</summary>
internal static class Payloads
{
    public static byte[] UInt32(uint value) => new WireWriter(4).UInt32(value).ToArray();

    public static byte[] NullTerminated(string value) => new WireWriter().NullTerminated(value).ToArray();

    /// <summary>UPDATELEDS: data_size, num_colors, colores.</summary>
    public static byte[] UpdateLeds(ReadOnlySpan<OpenRgbColor> colors)
    {
        if (colors.Length > ushort.MaxValue)
            throw new ArgumentException("Demasiados colores para un paquete.", nameof(colors));

        var w = new WireWriter(4 + 2 + colors.Length * 4);
        w.UInt32(0); // data_size, se rellena al final
        w.UInt16((ushort)colors.Length);
        foreach (var c in colors)
            w.Color(c);
        w.PatchUInt32(0, (uint)w.Length);
        return w.ToArray();
    }

    /// <summary>UPDATEZONELEDS: data_size, zone_idx, num_colors, colores.</summary>
    public static byte[] UpdateZoneLeds(int zoneIndex, ReadOnlySpan<OpenRgbColor> colors)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zoneIndex);
        if (colors.Length > ushort.MaxValue)
            throw new ArgumentException("Demasiados colores para un paquete.", nameof(colors));

        var w = new WireWriter(4 + 4 + 2 + colors.Length * 4);
        w.UInt32(0);
        w.UInt32((uint)zoneIndex);
        w.UInt16((ushort)colors.Length);
        foreach (var c in colors)
            w.Color(c);
        w.PatchUInt32(0, (uint)w.Length);
        return w.ToArray();
    }

    /// <summary>UPDATESINGLELED: led_idx, color (sin data_size).</summary>
    public static byte[] UpdateSingleLed(int ledIndex, OpenRgbColor color)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ledIndex);
        return new WireWriter(8).Int32(ledIndex).Color(color).ToArray();
    }

    /// <summary>RESIZEZONE: zone_idx, new_size.</summary>
    public static byte[] ResizeZone(int zoneIndex, int newSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zoneIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(newSize);
        return new WireWriter(8).Int32(zoneIndex).Int32(newSize).ToArray();
    }

    /// <summary>UPDATEMODE / SAVEMODE: data_size, mode_idx, bloque de modo.</summary>
    public static byte[] UpdateMode(int modeIndex, OpenRgbMode mode, uint protocolVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(modeIndex);
        var w = new WireWriter();
        w.UInt32(0);
        w.Int32(modeIndex);
        WriteMode(w, mode, protocolVersion);
        w.PatchUInt32(0, (uint)w.Length);
        return w.ToArray();
    }

    /// <summary>UPDATEZONEMODE (protocolo 6+): data_size, zone_idx, mode_idx, bloque de modo.</summary>
    public static byte[] UpdateZoneMode(int zoneIndex, int modeIndex, OpenRgbMode mode, uint protocolVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zoneIndex);
        var w = new WireWriter();
        w.UInt32(0);
        w.Int32(zoneIndex);
        w.Int32(modeIndex);
        WriteMode(w, mode, protocolVersion);
        w.PatchUInt32(0, (uint)w.Length);
        return w.ToArray();
    }

    /// <summary>Bloque "Mode Data" con el mismo formato que GetModeDescriptionData().</summary>
    public static void WriteMode(WireWriter w, OpenRgbMode mode, uint protocolVersion)
    {
        w.String16(mode.Name);
        if (protocolVersion < 6)
            w.Int32(mode.Value);

        var flags = (uint)mode.Flags;
        if (protocolVersion < 6)
            flags &= ~(uint)ModeFlags.HasDirectionDiagonal;
        w.UInt32(flags);

        w.UInt32(mode.SpeedMin);
        w.UInt32(mode.SpeedMax);
        if (protocolVersion >= 3)
        {
            w.UInt32(mode.BrightnessMin);
            w.UInt32(mode.BrightnessMax);
        }

        w.UInt32(mode.ColorsMin);
        w.UInt32(mode.ColorsMax);
        w.UInt32(mode.Speed);
        if (protocolVersion >= 3)
            w.UInt32(mode.Brightness);

        var direction = (uint)mode.Direction;
        if (protocolVersion < 6 && direction > (uint)ModeDirection.Vertical)
            direction = (uint)ModeDirection.Left;
        w.UInt32(direction);

        w.UInt32((uint)mode.ColorMode);

        if (mode.Colors.Length > ushort.MaxValue)
            throw new ArgumentException("Demasiados colores en el modo.", nameof(mode));
        w.UInt16((ushort)mode.Colors.Length);
        foreach (var c in mode.Colors)
            w.Color(c);
    }
}
