using System.Text.Json;
using Cefiro.Ipc.Contracts;

namespace Cefiro.Ipc;

/// <summary>
/// Fachada tipada sobre una <see cref="ICefiroConnection"/>. Es lo único que usa la interfaz
/// para hablar con el núcleo, tanto en modo real (pipe) como en modo demo (en proceso).
/// </summary>
public sealed class CefiroApi : IAsyncDisposable
{
    public CefiroApi(ICefiroConnection connection)
    {
        Connection = connection;
        Connection.EventReceived += OnEvent;
    }

    public ICefiroConnection Connection { get; }

    public event EventHandler<SensorFrameDto>? SensorFrame;
    public event EventHandler<FanFrameDto>? FanFrame;
    public event EventHandler<StatusDto>? StatusChanged;
    public event EventHandler<AlertDto>? Alert;
    public event EventHandler<CalibrationStatusDto>? CalibrationChanged;

    /// <summary>Eventos que esta fachada todavía no traduce (iluminación, perfiles…).</summary>
    public event EventHandler<IpcEvent>? OtherEvent;

    public Task StartAsync(CancellationToken cancellationToken = default) => Connection.StartAsync(cancellationToken);

    public Task<HelloResponse> HelloAsync(string client, CancellationToken cancellationToken = default) =>
        CallAsync<HelloResponse>(Methods.Hello, new HelloRequest(client, IpcConstants.ProtocolVersion), cancellationToken);

    public Task<CatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        CallAsync<CatalogDto>(Methods.GetCatalog, null, cancellationToken);

    public Task<HistoryResponse> GetHistoryAsync(HistoryRequest request, CancellationToken cancellationToken = default) =>
        CallAsync<HistoryResponse>(Methods.GetHistory, request, cancellationToken);

    public Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync<StatusDto>(Methods.GetStatus, null, cancellationToken);

    public Task<FanSettingsDto> GetFanSettingsAsync(CancellationToken cancellationToken = default) =>
        CallAsync<FanSettingsDto>(Methods.GetFanSettings, null, cancellationToken);

    /// <summary>Guarda la configuración. El servicio la valida y devuelve la versión aplicada.</summary>
    public Task<FanSettingsDto> SetFanSettingsAsync(FanSettingsDto settings, CancellationToken cancellationToken = default) =>
        CallAsync<FanSettingsDto>(Methods.SetFanSettings, settings, cancellationToken);

    public Task<FanSettingsDto> ApplyFanPresetAsync(ApplyPresetRequest request, CancellationToken cancellationToken = default) =>
        CallAsync<FanSettingsDto>(Methods.ApplyFanPreset, request, cancellationToken);

    public Task<CalibrationStatusDto> StartCalibrationAsync(CalibrationRequest request, CancellationToken cancellationToken = default) =>
        CallAsync<CalibrationStatusDto>(Methods.StartCalibration, request, cancellationToken);

    public Task<CalibrationStatusDto> CancelCalibrationAsync(CancellationToken cancellationToken = default) =>
        CallAsync<CalibrationStatusDto>(Methods.CancelCalibration, null, cancellationToken);

    public Task<CalibrationStatusDto> GetCalibrationAsync(CancellationToken cancellationToken = default) =>
        CallAsync<CalibrationStatusDto>(Methods.GetCalibration, null, cancellationToken);

    /// <summary>Llamada genérica (la usan las fases posteriores: iluminación, perfiles, ajustes).</summary>
    public async Task<TResponse> CallAsync<TResponse>(string method, object? request, CancellationToken cancellationToken = default)
    {
        JsonElement? payload = request is null ? null : JsonSerializer.SerializeToElement(request, request.GetType(), CefiroJson.Options);
        var result = await Connection.InvokeAsync(method, payload, cancellationToken).ConfigureAwait(false);
        return CefiroJson.FromElement<TResponse>(result)
               ?? throw new InvalidOperationException($"Respuesta vacía de \"{method}\".");
    }

    /// <summary>Llamada sin respuesta útil.</summary>
    public async Task CallAsync(string method, object? request, CancellationToken cancellationToken = default)
    {
        JsonElement? payload = request is null ? null : JsonSerializer.SerializeToElement(request, request.GetType(), CefiroJson.Options);
        await Connection.InvokeAsync(method, payload, cancellationToken).ConfigureAwait(false);
    }

    private void OnEvent(object? sender, IpcEvent e)
    {
        switch (e.Name)
        {
            case Events.Sensors:
                Raise(SensorFrame, e);
                break;
            case Events.Fans:
                Raise(FanFrame, e);
                break;
            case Events.Status:
                Raise(StatusChanged, e);
                break;
            case Events.Alert:
                Raise(Alert, e);
                break;
            case Events.Calibration:
                Raise(CalibrationChanged, e);
                break;
            default:
                OtherEvent?.Invoke(this, e);
                break;
        }
    }

    private void Raise<T>(EventHandler<T>? handler, IpcEvent e)
    {
        if (handler is null)
            return;

        var value = CefiroJson.FromElement<T>(e.Payload);
        if (value is not null)
            handler(this, value);
    }

    public async ValueTask DisposeAsync()
    {
        Connection.EventReceived -= OnEvent;
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
