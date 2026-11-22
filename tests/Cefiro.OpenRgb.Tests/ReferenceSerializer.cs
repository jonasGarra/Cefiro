using System.Buffers.Binary;
using System.Text;

namespace Cefiro.OpenRgb.Tests;

/// <summary>
/// Serializador independiente que reproduce, campo a campo, las tablas de
/// Documentation/OpenRGBSDK.md (Device/Mode/Zone/Segment/LED Data). Sirve para
/// comprobar el lector con una implementación distinta de la del propio cliente.
/// </summary>
internal sealed class ReferenceSerializer(uint protocol)
{
    private readonly List<byte> _bytes = [];

    public byte[] SerializeControllerDataReply(OpenRgbDevice d)
    {
        _bytes.Clear();
        U32(0); // data_size (incluye estos 4 bytes)
        I32((int)d.Type);
        S16(d.Name);
        if (protocol >= 1) S16(d.Vendor);
        S16(d.Description);
        S16(d.Version);
        S16(d.Serial);
        S16(d.Location);

        U16((ushort)d.Modes.Count);
        I32(d.ActiveMode);
        foreach (var m in d.Modes) Mode(m);

        U16((ushort)d.Zones.Count);
        foreach (var z in d.Zones) Zone(z);

        U16((ushort)d.Leds.Count);
        foreach (var l in d.Leds)
        {
            S16(l.Name);
            if (protocol < 6) U32(l.Value);
        }

        U16((ushort)d.Colors.Length);
        foreach (var c in d.Colors) U32(c.ToWire());

        if (protocol >= 5)
        {
            U16((ushort)d.LedDisplayNames.Count);
            foreach (var n in d.LedDisplayNames) S16(n);
            U32((uint)d.Flags);
        }

        if (protocol >= 6)
        {
            S16(d.DisplayName);
            var cfg = Encoding.UTF8.GetBytes(d.Configuration);
            U32((uint)cfg.Length + 1);
            _bytes.AddRange(cfg);
            _bytes.Add(0);
        }

        var result = _bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)result.Length);
        return result;
    }

    private void Mode(OpenRgbMode m)
    {
        S16(m.Name);
        if (protocol < 6) I32(m.Value);
        U32((uint)m.Flags);
        U32(m.SpeedMin);
        U32(m.SpeedMax);
        if (protocol >= 3) { U32(m.BrightnessMin); U32(m.BrightnessMax); }
        U32(m.ColorsMin);
        U32(m.ColorsMax);
        U32(m.Speed);
        if (protocol >= 3) U32(m.Brightness);
        U32((uint)m.Direction);
        U32((uint)m.ColorMode);
        U16((ushort)m.Colors.Length);
        foreach (var c in m.Colors) U32(c.ToWire());
    }

    private void Zone(OpenRgbZone z)
    {
        S16(z.Name);
        U32((uint)z.Type);
        U32(z.LedsMin);
        U32(z.LedsMax);
        U32(z.LedsCount);
        Matrix(z.Matrix);
        if (protocol >= 4)
        {
            U16((ushort)z.Segments.Count);
            foreach (var s in z.Segments)
            {
                S16(s.Name);
                U32((uint)s.Type);
                U32(s.StartIndex);
                U32(s.LedsCount);
                if (protocol >= 6)
                {
                    Matrix(s.Matrix);
                    U32(s.Flags);
                }
            }
        }

        if (protocol >= 5) U32((uint)z.Flags);
        if (protocol >= 6)
        {
            I32(z.ActiveMode);
            U16((ushort)z.Modes.Count);
            foreach (var m in z.Modes) Mode(m);
            S16(z.DisplayName);
        }
    }

    private void Matrix(MatrixMap? map)
    {
        if (map is null)
        {
            U16(0);
            return;
        }

        U16((ushort)(8 + map.Map.Length * 4));
        U32(map.Height);
        U32(map.Width);
        foreach (var v in map.Map) U32(v);
    }

    private void S16(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        U16((ushort)(b.Length + 1));
        _bytes.AddRange(b);
        _bytes.Add(0);
    }

    private void U16(ushort v) { _bytes.Add((byte)v); _bytes.Add((byte)(v >> 8)); }
    private void U32(uint v) { for (var i = 0; i < 4; i++) _bytes.Add((byte)(v >> (8 * i))); }
    private void I32(int v) => U32(unchecked((uint)v));
}
