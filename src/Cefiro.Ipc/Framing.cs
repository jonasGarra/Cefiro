using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cefiro.Ipc;

/// <summary>Tipo de mensaje del sobre.</summary>
public enum EnvelopeKind
{
    Request,
    Response,
    Event,
}

/// <summary>Sobre de cada mensaje: petición, respuesta o evento.</summary>
public sealed class Envelope
{
    [JsonPropertyName("k")] public EnvelopeKind Kind { get; set; }

    /// <summary>Identificador para emparejar petición y respuesta (0 en eventos).</summary>
    [JsonPropertyName("id")] public long Id { get; set; }

    /// <summary>Nombre del método o del evento.</summary>
    [JsonPropertyName("m")] public string? Name { get; set; }

    [JsonPropertyName("p")] public JsonElement? Payload { get; set; }

    /// <summary>Mensaje de error (solo en respuestas fallidas).</summary>
    [JsonPropertyName("e")] public string? Error { get; set; }
}

/// <summary>Trama: 4 bytes little-endian con la longitud + JSON UTF-8.</summary>
public static class Framing
{
    public const int MaxMessageSize = 16 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, Envelope envelope, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, CefiroJson.Options);
        await WriteRawAsync(stream, body, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteRawAsync(Stream stream, byte[] body, CancellationToken cancellationToken)
    {
        if (body.Length > MaxMessageSize)
            throw new InvalidOperationException($"Mensaje demasiado grande ({body.Length} bytes).");

        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static byte[] Serialize(Envelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, CefiroJson.Options);

    /// <summary>Lee un mensaje completo. Devuelve null si el otro extremo cerró la conexión.</summary>
    public static async Task<Envelope?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        if (!await ReadExactOrEofAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length <= 0 || length > MaxMessageSize)
            throw new InvalidDataException($"Longitud de mensaje inválida: {length}.");

        var body = new byte[length];
        if (!await ReadExactOrEofAsync(stream, body, cancellationToken).ConfigureAwait(false))
            return null;

        return JsonSerializer.Deserialize<Envelope>(body, CefiroJson.Options)
               ?? throw new InvalidDataException("Mensaje vacío.");
    }

    private static async Task<bool> ReadExactOrEofAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                    return false;
                throw new EndOfStreamException("La conexión se cerró a mitad de un mensaje.");
            }

            read += n;
        }

        return true;
    }
}
