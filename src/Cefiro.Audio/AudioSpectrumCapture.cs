// WasapiLoopbackCapture está marcada como obsoleta en NAudio 3, pero sigue siendo la forma
// documentada de escuchar toda la salida del sistema; se mantiene a propósito.
#pragma warning disable CS0618

using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Cefiro.Audio;

/// <summary>
/// Escucha lo que suena por la salida de audio predeterminada (sin micrófono) y emite el espectro
/// unas 30 veces por segundo. Si se cambia de altavoces/auriculares, se reengancha solo.
/// </summary>
public sealed class AudioSpectrumCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double _interval;
    private WasapiLoopbackCapture? _capture;
    private SpectrumAnalyzer? _analyzer;
    private readonly object _analyzerGate = new();
    private double _lastEmit;
    private double _lastData;
    private volatile bool _running;
    private Timer? _restartTimer;
    private Timer? _emitTimer;
    private long _callbacks;
    private long _frames;
    private double _firstCallback = -1;
    private double _lastCallback;

    /// <summary>Datos de diagnóstico de la captura (llamadas, frecuencia, formato).</summary>
    public string Diagnostics
    {
        get
        {
            var span = _lastCallback - _firstCallback;
            var rate = span > 0 ? (_callbacks - 1) / span : 0;
            return $"{_callbacks} bloques de audio ({rate:0.0}/s), {_frames} muestras, formato {_capture?.WaveFormat?.ToString() ?? "—"}";
        }
    }

    public AudioSpectrumCapture(int framesPerSecond = 30)
    {
        _interval = 1.0 / Math.Clamp(framesPerSecond, 5, 60);
    }

    /// <summary>Bandas (16, de 0 a 1) y nivel global. Se lanza en un hilo de audio.</summary>
    public event Action<float[], float>? SpectrumReady;

    public event Action<string>? Error;

    public bool IsRunning => _running;

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
                return;
            _running = true;
            StartCapture();
            var period = TimeSpan.FromSeconds(_interval);
            _emitTimer = new Timer(OnEmitTimer, null, period, period);
        }
    }

    private void StartCapture()
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            _analyzer = new SpectrumAnalyzer(capture.WaveFormat.SampleRate);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
            _capture = capture;
        }
        catch (Exception ex)
        {
            Error?.Invoke($"No se puede escuchar el audio del sistema: {ex.Message}");
            ScheduleRestart();
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        var analyzer = _analyzer;
        if (capture is null || analyzer is null || e.BytesRecorded == 0)
            return;

        var format = capture.WaveFormat;
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = e.BytesRecorded / (bytesPerSample * channels);
        if (frames <= 0)
            return;

        var callbackTime = _clock.Elapsed.TotalSeconds;
        if (_firstCallback < 0)
            _firstCallback = callbackTime;
        _lastCallback = callbackTime;
        _callbacks++;
        _frames += frames;

        var mono = new float[frames];
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                      || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var offset = (f * channels + c) * bytesPerSample;
                sum += isFloat
                    ? BitConverter.ToSingle(e.Buffer, offset)
                    : bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, offset) / 32768.0 : 0;
            }

            mono[f] = (float)(sum / channels);
        }

        lock (_analyzerGate)
        {
            analyzer.Push(mono);
            _lastData = callbackTime;
        }
    }

    /// <summary>
    /// Emite el espectro a ritmo fijo (no al ritmo irregular en que llegan los bloques de audio).
    /// Si no llega audio (en silencio WASAPI no envía nada), se rellena con silencio para que
    /// las luces se apaguen en vez de quedarse congeladas.
    /// </summary>
    private void OnEmitTimer(object? state)
    {
        var analyzer = _analyzer;
        if (analyzer is null || !_running)
            return;

        float[] bands;
        float level;
        lock (_analyzerGate)
        {
            var now = _clock.Elapsed.TotalSeconds;
            var dt = _lastEmit > 0 ? now - _lastEmit : _interval;
            _lastEmit = now;

            if (now - _lastData > 0.15)
            {
                var silence = (int)Math.Min(analyzer.SampleRate * dt, SpectrumAnalyzer.FftSize);
                if (silence > 0)
                    analyzer.Push(new float[silence]);
            }

            (bands, level) = analyzer.Compute(Math.Min(dt, 0.5));
        }

        try { SpectrumReady?.Invoke(bands, level); }
        catch { /* un consumidor que falla no para la captura */ }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            DisposeCapture();
            if (_running)
            {
                if (e.Exception is not null)
                    Error?.Invoke($"Se ha perdido el audio del sistema: {e.Exception.Message}");
                ScheduleRestart();
            }
        }
    }

    private void ScheduleRestart()
    {
        _restartTimer?.Dispose();
        _restartTimer = new Timer(_ =>
        {
            lock (_gate)
            {
                if (_running && _capture is null)
                    StartCapture();
            }
        }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _emitTimer?.Dispose();
            _emitTimer = null;
            _restartTimer?.Dispose();
            _restartTimer = null;
            try { _capture?.StopRecording(); } catch { /* ya parada */ }
            DisposeCapture();
        }
    }

    private void DisposeCapture()
    {
        if (_capture is null)
            return;

        _capture.DataAvailable -= OnData;
        _capture.RecordingStopped -= OnStopped;
        try { _capture.Dispose(); } catch { /* ignorado */ }
        _capture = null;
    }

    public void Dispose() => Stop();

    /// <summary>Nombre de la salida de audio predeterminada (para mostrarlo en la interfaz).</summary>
    public static string? DefaultOutputName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
        }
        catch
        {
            return null;
        }
    }
}
