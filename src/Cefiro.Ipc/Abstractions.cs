using System.Text.Json;

namespace Cefiro.Ipc;

/// <summary>Evento enviado por el servicio.</summary>
public sealed record IpcEvent(string Name, JsonElement? Payload);

/// <summary>Error devuelto por el servicio al ejecutar un método.</summary>
public sealed class IpcRemoteException(string method, string message) : Exception(message)
{
    public string Method { get; } = method;
}

/// <summary>Lado servidor: ejecuta un método y devuelve el resultado ya serializado.</summary>
public interface IRequestHandler
{
    Task<JsonElement?> HandleAsync(string method, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>Lado servidor: origen de los eventos que se difunden a los clientes.</summary>
public interface IEventSource
{
    event EventHandler<IpcEvent>? EventPublished;
}

/// <summary>Lado cliente: conexión con el núcleo (named pipe o en proceso para el modo demo).</summary>
public interface ICefiroConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Cambio de estado de la conexión (true = conectado).</summary>
    event EventHandler<bool>? ConnectionChanged;

    event EventHandler<IpcEvent>? EventReceived;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<JsonElement?> InvokeAsync(string method, JsonElement? payload, CancellationToken cancellationToken = default);
}
