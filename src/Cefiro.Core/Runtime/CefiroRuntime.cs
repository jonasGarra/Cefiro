using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Cefiro.Core.Fans;
using Cefiro.Core.Hardware;
using Cefiro.Core.Platform;
using Cefiro.Core.Sensors;
using Cefiro.Ipc;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging;

namespace Cefiro.Core.Runtime;

/// <summary>
/// Núcleo de Céfiro. Un único hilo ("bucle de hardware") es el que toca el hardware: lee los
/// sensores, calcula y aplica los ventiladores y vigila la seguridad. El resto (peticiones de la
/// interfaz, eventos) trabaja sobre copias o encola acciones en ese hilo.
/// </summary>
public sealed partial class CefiroRuntime : IRequestHandler, IEventSource, IAsyncDisposable
{
    private readonly IHardwareBackend _backend;
    private readonly Lighting.ILightingBackend? _lightingBackend;
    private readonly RuntimeOptions _options;
    private readonly ILogger _logger;
    private readonly ProcessStats _processStats = new();
    private readonly ConcurrentQueue<Action> _hardwareQueue = new();
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<object?>>> _routes = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _firstOpenAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly JsonStore _store;

    private Thread? _thread;
    private volatile bool _hardwareReady;
    private SensorDescriptor[] _sensors = [];
    private Dictionary<string, int> _sensorIndex = new(StringComparer.Ordinal);
    private float?[] _values = [];
    private SensorHistory? _history;
    private volatile CatalogDto? _catalog;
    private volatile StatusDto _status = new();
    private volatile IReadOnlyList<ConflictDto> _conflicts = [];
    private volatile string? _lastError;
    private double _lastUpdateMs;
    private DateTime _lastConflictScan = DateTime.MinValue;
    private DateTime _lastOpenAttempt = DateTime.MinValue;
    private long _tick;
    private int _consecutiveFailures;

    public CefiroRuntime(IHardwareBackend backend, Lighting.ILightingBackend? lightingBackend, RuntimeOptions options, ILogger<CefiroRuntime> logger)
    {
        _backend = backend;
        _lightingBackend = lightingBackend;
        _options = options;
        _logger = logger;
        _store = new JsonStore(Path.Combine(options.DataDirectory, "config"), logger);

        Route<HelloRequest, HelloResponse>(Methods.Hello, (_, _) => Task.FromResult(new HelloResponse(
            _options.ServiceVersion, IpcConstants.ProtocolVersion, _options.DemoMode, Environment.MachineName)));
        Route<object, CatalogDto>(Methods.GetCatalog, (_, _) => Task.FromResult(
            _catalog ?? throw new InvalidOperationException("El hardware todavía se está iniciando.")));
        Route<HistoryRequest, HistoryResponse>(Methods.GetHistory, (req, _) => Task.FromResult(GetHistory(req)));
        Route<object, StatusDto>(Methods.GetStatus, (_, _) => Task.FromResult(_status));

        RegisterFanRoutes();
        RegisterLightingRoutes();
        RegisterProfileRoutes();
        LoadProfiles();
    }

    public event EventHandler<IpcEvent>? EventPublished;

    /// <summary>Si devuelve false no se serializan eventos (no hay nadie escuchando).</summary>
    public Func<bool> HasListeners { get; set; } = static () => true;

    public bool HardwareReady => _hardwareReady;

    public RuntimeOptions Options => _options;

    internal JsonStore Store => _store;

    /// <summary>Arranca el bucle de hardware y espera al primer intento de abrir el hardware.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_thread is not null)
            return;

        _thread = new Thread(HardwareLoop)
        {
            Name = "Céfiro · bucle de hardware",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();

        await _firstOpenAttempt.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        StartLighting();
    }

    /// <summary>Detiene la iluminación y el bucle, y devuelve todos los ventiladores a la BIOS.</summary>
    public async Task StopAsync()
    {
        try
        {
            await StopLightingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deteniendo la iluminación");
        }

        if (_thread is null)
            return;

        _stopSignal.Set();
        await Task.Run(() =>
        {
            if (!_thread.Join(TimeSpan.FromSeconds(15)))
            {
                _logger.LogError("El bucle de hardware no terminó a tiempo; se devuelven los ventiladores a la BIOS desde otro hilo");
                EmergencyRelease();
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Último recurso ante un fallo grave (excepción no controlada): devuelve todo a la BIOS
    /// desde cualquier hilo.
    /// </summary>
    public void EmergencyRelease()
    {
        try
        {
            _backend.ReleaseAllFans();
            _logger.LogWarning("Ventiladores devueltos a la BIOS (liberación de emergencia)");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "No se pudieron devolver los ventiladores a la BIOS");
        }
    }

    /// <summary>Ejecuta una acción en el hilo de hardware y espera su resultado.</summary>
    internal Task<T> RunOnHardwareThreadAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hardwareQueue.Enqueue(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private void HardwareLoop()
    {
        var clock = Stopwatch.StartNew();
        TryOpenHardware();
        _firstOpenAttempt.TrySetResult();

        var lastCycle = Environment.TickCount64;
        while (!_stopSignal.IsSet)
        {
            var cycleStart = clock.Elapsed;

            // GetTickCount64 sigue contando durante la suspensión y la hibernación (también la del "inicio
            // rápido" de Windows al apagar): un salto grande entre dos ciclos significa que el PC ha dormido.
            var nowTicks = Environment.TickCount64;
            if (nowTicks - lastCycle > ResumeGapMilliseconds)
                OnResumedFromSleep(TimeSpan.FromMilliseconds(nowTicks - lastCycle));
            lastCycle = nowTicks;

            RunQueuedActions();

            if (_hardwareReady)
            {
                RunTick();
            }
            else if (DateTime.UtcNow - _lastOpenAttempt > TimeSpan.FromSeconds(30))
            {
                TryOpenHardware();
            }

            if (_tick % _options.StatusEveryTicks == 0)
                RefreshStatus();

            _tick++;

            var remaining = _options.TickInterval - (clock.Elapsed - cycleStart);
            if (remaining > TimeSpan.Zero)
                _stopSignal.Wait(remaining);
        }

        // Cierre ordenado: primero los ventiladores, después el hardware.
        RunQueuedActions();
        try
        {
            OnShutdownFans();
            _backend.ReleaseAllFans();
            _logger.LogInformation("Ventiladores devueltos a la BIOS al detener el servicio");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error devolviendo los ventiladores a la BIOS al detener");
        }

        try { _backend.Dispose(); }
        catch (Exception ex) { _logger.LogError(ex, "Error cerrando el hardware"); }
    }

    /// <summary>Pausa del bucle (en ms) a partir de la cual se considera que el PC ha estado dormido.</summary>
    private const long ResumeGapMilliseconds = 15_000;

    /// <summary>
    /// Vuelta de suspensión o hibernación. El chip de la placa y el controlador USB de las luces se han
    /// reiniciado mientras el servicio seguía "vivo":
    ///  - Ventiladores: se sueltan a la BIOS y en el siguiente ciclo se vuelven a tomar desde cero (si no,
    ///    el chip podría quedarse con la configuración de la BIOS creyendo Céfiro que la controla él).
    ///  - Luces: OpenRGB vuelve a detectar los dispositivos y se reenvía todo.
    /// </summary>
    private void OnResumedFromSleep(TimeSpan gap)
    {
        _logger.LogInformation("El PC vuelve de suspensión o hibernación ({Minutes:0} min sin actividad): se reaplican ventiladores y luces", gap.TotalMinutes);

        if (_hardwareReady && _fanEngine is not null)
        {
            try
            {
                _fanEngine.ReleaseAll(_backend.FanOutputs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error soltando los ventiladores tras la suspensión");
            }
        }

        _lightingEngine?.RequestRescan("vuelta de suspensión o hibernación");
    }

    private void TryOpenHardware()
    {
        _lastOpenAttempt = DateTime.UtcNow;
        try
        {
            var sw = Stopwatch.StartNew();
            _backend.Open();
            _backend.Update();

            _sensors = [.. _backend.Sensors];
            _sensorIndex = _sensors.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
            _values = new float?[_sensors.Length];
            _history = new SensorHistory(_sensors.Length);
            _backend.ReadValues(_values);

            _catalog = BuildCatalog(version: (_catalog?.Version ?? 0) + 1);
            OnHardwareOpened();
            _hardwareReady = true;
            _lastError = null;
            _logger.LogInformation("Hardware listo en {Ms} ms: {Sensors} sensores, {Fans} salidas de ventilador ({Backend})",
                sw.ElapsedMilliseconds, _sensors.Length, _backend.FanOutputs.Count, _backend.Name);
        }
        catch (Exception ex)
        {
            _hardwareReady = false;
            _lastError = $"No se pudo abrir el hardware: {ex.Message}";
            _logger.LogError(ex, "No se pudo abrir el hardware; se reintentará en 30 s");
        }
    }

    /// <summary>
    /// Salidas que en algún momento han girado como una bomba (ID → explicación). Nunca se olvidan
    /// mientras el servicio está en marcha: la protección de bomba no depende de una sola lectura.
    /// </summary>
    private readonly Dictionary<string, string> _pumpsSeen = new(StringComparer.Ordinal);

    /// <summary>Anota las salidas que ahora mismo giran como una bomba. Devuelve true si hay alguna nueva.</summary>
    private bool RecordPumpSignatures()
    {
        var changed = false;
        foreach (var output in _backend.FanOutputs)
        {
            if (!_pumpsSeen.ContainsKey(output.Id) && FanRoleDetector.LooksLikePump(output))
            {
                _pumpsSeen[output.Id] = FanRoleDetector.Evidence(output.Rpm, output.OutputPercent);
                _logger.LogWarning("{Header} gira como una bomba ({Rpm:0} RPM con {Percent:0} %): se protege como bomba",
                    output.Header, output.Rpm, output.OutputPercent);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>En cada ciclo: si aparece una bomba nueva, se rehace el catálogo y se avisa al motor de ventiladores.</summary>
    private void TrackPumpSignatures()
    {
        if (RecordPumpSignatures() && _catalog is { } current)
        {
            _catalog = BuildCatalog(current.Version + 1);
            OnCatalogRolesChanged();
        }
    }

    internal IReadOnlyDictionary<string, string> PumpsSeen => _pumpsSeen;

    private CatalogDto BuildCatalog(int version)
    {
        RecordPumpSignatures();
        var roles = FanRoleDetector.Detect(_backend.FanOutputs, _pumpsSeen);
        var channels = _backend.FanOutputs.Select(o =>
        {
            var (role, evidence) = roles[o.Id];
            return new FanChannelDto(o.Id, o.Header, o.Device, o.DefaultName, role, evidence,
                o.RpmSensorId, o.ControlSensorId, o.HardwareMinimumPercent, o.HardwareMaximumPercent);
        }).ToList();

        return new CatalogDto(
            version,
            _sensors.Select(s => s.ToDto()).ToList(),
            KeySensorResolver.Resolve(_sensors),
            channels,
            _backend.Info);
    }

    private void RunTick()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            _backend.Update();
            _backend.ReadValues(_values);
            _lastUpdateMs = sw.Elapsed.TotalMilliseconds;
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _lastError = $"Error leyendo sensores: {ex.Message}";
            if (_consecutiveFailures is 1 or 10 or 100)
                _logger.LogError(ex, "Error leyendo sensores ({Count} seguidos)", _consecutiveFailures);

            // Sin lecturas no hay datos fiables: se marcan como desconocidos para que la seguridad actúe.
            Array.Fill(_values, null);
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _history!.Add(timestamp, _values);

        TrackPumpSignatures();

        // Ventiladores y seguridad. Siempre se ejecuta, incluso con lecturas nulas.
        TickFans(timestamp);
        CheckTemperatureWarnings();
        MaybeScanPrograms();

        // Copia para el resto de hilos (iluminación, interfaz): el array del bucle no se comparte nunca.
        var snapshot = (float?[])_values.Clone();
        _sensorSnapshot = snapshot;

        // La vista previa de los LEDs solo se calcula si hay alguien mirando.
        if (_lightingEngine is { } lighting)
            lighting.PreviewEnabled = HasListeners();

        if (HasListeners())
        {
            Publish(Events.Sensors, new SensorFrameDto(_catalog?.Version ?? 0, timestamp, snapshot));
        }

        if (DateTime.UtcNow - _lastConflictScan > _options.ConflictScanInterval)
        {
            _lastConflictScan = DateTime.UtcNow;
            _ = Task.Run(ScanConflicts);
        }
    }

    private void ScanConflicts()
    {
        try
        {
            var found = ConflictDetector.DetectProcesses();
            var previous = _conflicts;
            _conflicts = found;

            foreach (var c in found.Where(c => previous.All(p => p.Id != c.Id)))
            {
                _logger.LogWarning("Programa en conflicto detectado: {Name}", c.Name);
                PublishAlert(AlertKind.Conflict, $"{c.Name} está abierto", c.Detail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo buscar programas en conflicto");
        }
    }

    private void RefreshStatus()
    {
        _status = new StatusDto
        {
            HardwareReady = _hardwareReady,
            OpenRgbConnected = LightingConnected,
            EmergencyActive = EmergencyActive,
            EmergencyReason = EmergencyReason,
            ActiveProfileId = ActiveProfileId,
            Conflicts = _conflicts,
            ServiceCpuPercent = Math.Round(_processStats.SampleCpuPercent(), 2),
            ServiceMemoryBytes = _processStats.WorkingSetBytes,
            UptimeSeconds = (long)(DateTime.UtcNow - _processStats.StartedAt).TotalSeconds,
            LastUpdateMilliseconds = Math.Round(_lastUpdateMs, 1),
            LastError = _lastError,
        };

        if (HasListeners())
            Publish(Events.Status, _status);
    }

    private HistoryResponse GetHistory(HistoryRequest? request)
    {
        if (request is null || _history is null)
            return new HistoryResponse(0, SensorHistory.FineIntervalMs, []);

        var series = new List<HistorySeriesDto>(request.SensorIds.Count);
        var interval = SensorHistory.FineIntervalMs;
        foreach (var id in request.SensorIds)
        {
            if (_sensorIndex.TryGetValue(id, out var index))
                series.Add(new HistorySeriesDto(id, _history.GetSeries(index, request.Range, out interval)));
        }

        return new HistoryResponse(_history.LastTimestamp, interval, series);
    }

    /// <summary>Valor actual de un sensor por su ID (solo desde el hilo de hardware).</summary>
    internal float? ReadSensor(string id) =>
        _sensorIndex.TryGetValue(id, out var index) ? _values[index] : null;

    internal IReadOnlyList<SensorDescriptor> SensorDescriptors => _sensors;

    private void RunQueuedActions()
    {
        while (_hardwareQueue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { _logger.LogError(ex, "Error en una acción del bucle de hardware"); }
        }
    }

    internal void Publish(string name, object payload)
    {
        var handler = EventPublished;
        if (handler is null)
            return;

        try
        {
            handler(this, new IpcEvent(name, JsonSerializer.SerializeToElement(payload, payload.GetType(), CefiroJson.Options)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error publicando el evento {Name}", name);
        }
    }

    internal void PublishAlert(AlertKind kind, string title, string message)
    {
        Publish(Events.Alert, new AlertDto(kind, title, message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private void Route<TRequest, TResponse>(string method, Func<TRequest?, CancellationToken, Task<TResponse>> handler)
    {
        _routes[method] = async (payload, ct) => await handler(CefiroJson.FromElement<TRequest>(payload), ct).ConfigureAwait(false);
    }

    public async Task<JsonElement?> HandleAsync(string method, JsonElement? payload, CancellationToken cancellationToken)
    {
        if (!_routes.TryGetValue(method, out var route))
            throw new NotSupportedException($"Método desconocido: {method}");

        var result = await route(payload, cancellationToken).ConfigureAwait(false);
        return result is null ? null : JsonSerializer.SerializeToElement(result, result.GetType(), CefiroJson.Options);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopSignal.Dispose();
    }
}
