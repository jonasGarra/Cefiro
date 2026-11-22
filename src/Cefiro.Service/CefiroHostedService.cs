using Cefiro.Core.Runtime;
using Cefiro.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cefiro.Service;

public sealed record PipeSettings(string PipeName);

/// <summary>Arranca el núcleo y el servidor del pipe, y los para en orden al detener el servicio.</summary>
public sealed class CefiroHostedService(CefiroRuntime runtime, PipeSettings pipeSettings, ILogger<CefiroHostedService> logger) : IHostedService
{
    private PipeServer? _pipe;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _pipe = new PipeServer(
            pipeSettings.PipeName,
            runtime,
            log: message => logger.LogInformation("{Message}", message),
            logError: (ex, message) => logger.LogError(ex, "{Message}", message));

        runtime.EventPublished += OnEvent;
        runtime.HasListeners = () => _pipe?.ClientCount > 0;

        await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
        _pipe.Start();
        logger.LogInformation("Servicio listo. Hardware disponible: {Ready}", runtime.HardwareReady);
    }

    private void OnEvent(object? sender, IpcEvent e) => _pipe?.Publish(e);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Deteniendo el servicio…");
        runtime.EventPublished -= OnEvent;

        if (_pipe is not null)
            await _pipe.DisposeAsync().ConfigureAwait(false);

        // Para el bucle de hardware y devuelve los ventiladores a la BIOS.
        await runtime.StopAsync().ConfigureAwait(false);
        logger.LogInformation("Servicio detenido.");
    }
}
