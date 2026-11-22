using Cefiro.App.Infrastructure;
using Cefiro.Audio;
using Cefiro.Ipc.Contracts;

namespace Cefiro.App.Services;

/// <summary>
/// Escucha el sonido del PC y envía el espectro al servicio para el efecto Música (unas 30 veces por
/// segundo). La captura la hace la interfaz porque el servicio corre en otra sesión de Windows y no
/// puede oír el audio del usuario. Solo está activa mientras algún efecto Música lo necesita.
/// </summary>
public sealed class AudioBridge : IDisposable
{
    private readonly CefiroSession _session;
    private readonly object _gate = new();
    private AudioSpectrumCapture? _capture;
    private int _inFlight;

    public AudioBridge(CefiroSession session)
    {
        _session = session;
    }

    /// <summary>Nivel de sonido (0-1). Se lanza desde el hilo de captura.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Error de la captura (sin altavoces, dispositivo perdido…). Desde el hilo de captura.</summary>
    public event Action<string>? Error;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _capture is not null;
        }
    }

    public static string? OutputName => AudioSpectrumCapture.DefaultOutputName();

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (active && _capture is null)
            {
                try
                {
                    var capture = new AudioSpectrumCapture(30);
                    capture.SpectrumReady += OnSpectrum;
                    capture.Error += OnError;
                    capture.Start();
                    _capture = capture;
                    UiLog.Info($"Audio para el efecto Música: escuchando \"{OutputName}\"");
                }
                catch (Exception ex)
                {
                    UiLog.Error("No se pudo empezar a escuchar el audio", ex);
                    Error?.Invoke($"No se puede escuchar el audio del sistema: {ex.Message}");
                }
            }
            else if (!active && _capture is not null)
            {
                _capture.SpectrumReady -= OnSpectrum;
                _capture.Error -= OnError;
                _capture.Dispose();
                _capture = null;
                LevelChanged?.Invoke(0);
            }
        }
    }

    private void OnError(string message)
    {
        UiLog.Error(message);
        Error?.Invoke(message);
    }

    private void OnSpectrum(float[] bands, float level)
    {
        LevelChanged?.Invoke(level);

        // Si el envío anterior aún no ha terminado se salta este (no se acumula retraso).
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;

        _ = SendAsync(bands, level);
    }

    private async Task SendAsync(float[] bands, float level)
    {
        try
        {
            await _session.Api.CallAsync(Methods.AudioSpectrum, new AudioSpectrumDto(bands, level)).ConfigureAwait(false);
        }
        catch
        {
            // Servicio reiniciándose o desconectado: se reintenta con el siguiente espectro.
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    public void Dispose() => SetActive(false);
}
