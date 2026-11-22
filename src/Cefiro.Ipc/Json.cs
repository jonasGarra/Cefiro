using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cefiro.Ipc;

/// <summary>Opciones JSON comunes a todo Céfiro (IPC, configuración y perfiles).</summary>
public static class CefiroJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    /// <summary>Para ficheros que puede leer una persona (configuración, perfiles exportados).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } e
            ? e.Deserialize<T>(Options)
            : default;
}
