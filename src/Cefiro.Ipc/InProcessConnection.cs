using System.Text.Json;

namespace Cefiro.Ipc;

/// <summary>
/// Conexión dentro del mismo proceso (modo demo). Pasa todo por JSON igual que el pipe,
/// para que la interfaz ejercite exactamente el mismo camino que con el servicio real.
/// </summary>
public sealed class InProcessConnection : ICefiroConnection
{
    private readonly IRequestHandler _handler;
    private readonly IEventSource _source;
    private bool _started;

    public InProcessConnection(IRequestHandler handler, IEventSource source)
    {
        _handler = handler;
        _source = source;
    }

    public bool IsConnected => _started;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<IpcEvent>? EventReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
            return Task.CompletedTask;

        _started = true;
        _source.EventPublished += OnEventPublished;
        ConnectionChanged?.Invoke(this, true);
        return Task.CompletedTask;
    }

    private void OnEventPublished(object? sender, IpcEvent e)
    {
        try { EventReceived?.Invoke(this, new IpcEvent(e.Name, Clone(e.Payload))); }
        catch { /* igual que en el pipe: un manejador no corta la conexión */ }
    }

    public async Task<JsonElement?> InvokeAsync(string method, JsonElement? payload, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _handler.HandleAsync(method, Clone(payload), cancellationToken).ConfigureAwait(false);
            return Clone(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IpcRemoteException(method, ex.Message);
        }
    }

    private static JsonElement? Clone(JsonElement? element) =>
        element is { } e ? JsonDocument.Parse(e.GetRawText()).RootElement.Clone() : null;

    public ValueTask DisposeAsync()
    {
        _source.EventPublished -= OnEventPublished;
        _started = false;
        return ValueTask.CompletedTask;
    }
}
