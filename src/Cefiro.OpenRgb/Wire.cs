using System.Buffers.Binary;
using System.Text;

namespace Cefiro.OpenRgb;

/// <summary>Lector little-endian con comprobación de límites para los bloques del protocolo.</summary>
internal ref struct WireReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public WireReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public int Position => _position;
    public int Remaining => _data.Length - _position;

    private void Ensure(int count)
    {
        if (count < 0 || count > _data.Length - _position)
        {
            throw new OpenRgbProtocolException(
                $"Paquete truncado: se necesitaban {count} bytes en la posición {_position} y el bloque mide {_data.Length}.");
        }
    }

    public byte ReadByte()
    {
        Ensure(1);
        return _data[_position++];
    }

    public ushort ReadUInt16()
    {
        Ensure(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
        _position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    public int ReadInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadInt32LittleEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    /// <summary>Cadena precedida de su longitud en un ushort (la longitud incluye el '\0' final).</summary>
    public string ReadString16() => ReadChars(ReadUInt16());

    /// <summary>Cadena precedida de su longitud en un uint (la longitud incluye el '\0' final).</summary>
    public string ReadString32()
    {
        var length = ReadUInt32();
        if (length > int.MaxValue)
            throw new OpenRgbProtocolException("Longitud de cadena fuera de rango.");
        return ReadChars((int)length);
    }

    private string ReadChars(int length)
    {
        if (length == 0)
            return "";

        Ensure(length);
        var raw = _data.Slice(_position, length);
        _position += length;

        var end = raw.IndexOf((byte)0);
        if (end < 0)
            end = raw.Length;

        return Encoding.UTF8.GetString(raw[..end]);
    }

    public OpenRgbColor ReadColor() => OpenRgbColor.FromWire(ReadUInt32());

    public void Skip(int count)
    {
        Ensure(count);
        _position += count;
    }
}

/// <summary>Constructor de cargas útiles (payloads) little-endian sobre un array que crece.</summary>
internal sealed class WireWriter
{
    private byte[] _buffer;
    private int _length;

    public WireWriter(int initialCapacity = 256)
    {
        _buffer = new byte[Math.Max(16, initialCapacity)];
    }

    public int Length => _length;

    private Span<byte> Reserve(int count)
    {
        if (_length + count > _buffer.Length)
        {
            var newSize = Math.Max(_buffer.Length * 2, _length + count);
            Array.Resize(ref _buffer, newSize);
        }

        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public WireWriter UInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);
        return this;
    }

    public WireWriter UInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);
        return this;
    }

    public WireWriter Int32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);
        return this;
    }

    public WireWriter Byte(byte value)
    {
        Reserve(1)[0] = value;
        return this;
    }

    public WireWriter Color(OpenRgbColor color) => UInt32(color.ToWire());

    /// <summary>Escribe una cadena con prefijo ushort de longitud (incluido el '\0').</summary>
    public WireWriter String16(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length + 1 > ushort.MaxValue)
            throw new ArgumentException("Cadena demasiado larga para el protocolo.", nameof(value));

        UInt16((ushort)(bytes.Length + 1));
        RawBytes(bytes);
        return Byte(0);
    }

    /// <summary>Escribe una cadena con prefijo uint de longitud (incluido el '\0').</summary>
    public WireWriter String32(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        UInt32((uint)(bytes.Length + 1));
        RawBytes(bytes);
        return Byte(0);
    }

    /// <summary>Escribe una cadena terminada en '\0' sin prefijo de longitud.</summary>
    public WireWriter NullTerminated(string value)
    {
        RawBytes(Encoding.UTF8.GetBytes(value));
        return Byte(0);
    }

    public WireWriter RawBytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Reserve(bytes.Length));
        return this;
    }

    /// <summary>Sobrescribe un uint ya escrito (p. ej. el campo data_size del principio).</summary>
    public void PatchUInt32(int offset, uint value)
    {
        if (offset < 0 || offset + 4 > _length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(offset, 4), value);
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();
}
