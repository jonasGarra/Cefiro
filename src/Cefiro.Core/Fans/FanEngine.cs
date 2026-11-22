using Cefiro.Core.Hardware;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging;

namespace Cefiro.Core.Fans;

/// <summary>
/// Motor de ventiladores. Se ejecuta en cada ciclo del bucle de hardware (1 s) y es el único que
/// escribe en las salidas, siempre a través del <see cref="SafetyGuard"/>.
/// Orden de prioridad en cada salida:
///   1. Emergencia (CPU/GPU en temperatura crítica)  → 100 %.
///   2. Sensor de CPU perdido                          → todo a la BIOS.
///   3. Calibración en curso                           → la maneja el calibrador.
///   4. Modo configurado (BIOS, fijo o curva); si la fuente de una curva se pierde → BIOS.
/// </summary>
public sealed class FanEngine
{
    private const float StallRpm = 50;
    private const float StallMinOutput = 30;
    private static readonly TimeSpan StallTime = TimeSpan.FromSeconds(8);

    private readonly ILogger _logger;
    private readonly Dictionary<string, FanChannelController> _controllers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastValidSource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _stalledSince = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stallAlerted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _everSpun = new(StringComparer.Ordinal);
    private readonly EmergencyMonitor _emergency = new();
    private Dictionary<string, FanChannelSettingsDto> _channels = new(StringComparer.Ordinal);
    private FanSettingsDto _settings = new([], [], new SafetySettingsDto());
    private DateTime _lastTick;
    private DateTime _lastCpuReading;
    private bool _cpuLostReported;

    public FanEngine(ILogger logger)
    {
        _logger = logger;
    }

    public bool EmergencyActive => _emergency.Active;
    public string? EmergencyReason => _emergency.Reason;
    public FanSettingsDto Settings => _settings;

    /// <summary>Avisos para la interfaz (tipo, título, mensaje).</summary>
    public event Action<AlertKind, string, string>? Alert;

    /// <summary>Salidas que ahora controla otro componente (la calibración).</summary>
    public ISet<string> Excluded { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Aplica una configuración ya validada.</summary>
    public void Configure(FanSettingsDto settings)
    {
        var previous = _channels;
        _settings = settings;
        _channels = settings.Channels.ToDictionary(c => c.ChannelId, StringComparer.Ordinal);

        // Al cambiar de modo se olvida el estado de histéresis/rampa de esa salida.
        foreach (var c in settings.Channels)
        {
            if (previous.TryGetValue(c.ChannelId, out var old) && old.Mode != c.Mode && _controllers.TryGetValue(c.ChannelId, out var ctrl))
                ctrl.Reset();
        }
    }

    public FanFrameDto Tick(
        IReadOnlyList<IFanOutput> outputs,
        Func<string, float?> readSensor,
        KeySensorsDto keys,
        IReadOnlyDictionary<string, string> pumpsSeen,
        DateTime now,
        long timestamp)
    {
        var dt = _lastTick == default ? 1.0 : Math.Clamp((now - _lastTick).TotalSeconds, 0.05, 10);
        _lastTick = now;

        var safety = _settings.Safety;
        var cpu = keys.CpuTemperature is { } cpuId ? readSensor(cpuId) : null;
        var gpu = keys.GpuTemperature is { } gpuId ? readSensor(gpuId) : null;

        // 1) Emergencia.
        if (_emergency.Update(cpu, gpu, safety, now))
        {
            if (_emergency.Active)
            {
                _logger.LogWarning("MODO DE EMERGENCIA: {Reason}. Todos los ventiladores al 100 %", _emergency.Reason);
                Alert?.Invoke(AlertKind.Emergency, "Modo de emergencia",
                    $"{_emergency.Reason}. Todos los ventiladores al 100 % hasta que baje la temperatura.");
            }
            else
            {
                _logger.LogInformation("Fin del modo de emergencia");
                Alert?.Invoke(AlertKind.EmergencyCleared, "Temperatura normalizada",
                    "Se ha salido del modo de emergencia: los ventiladores vuelven a su modo normal.");
            }
        }

        // 2) ¿Seguimos viendo la temperatura de la CPU? Sin ella no se puede vigilar nada.
        if (cpu.HasValue)
        {
            _lastCpuReading = now;
            if (_cpuLostReported)
            {
                _cpuLostReported = false;
                _logger.LogInformation("Sensor de temperatura de la CPU recuperado");
            }
        }

        var cpuLost = keys.CpuTemperature is not null
                      && _lastCpuReading != default
                      && now - _lastCpuReading > TimeSpan.FromSeconds(safety.SensorTimeoutSeconds);
        if (cpuLost && !_cpuLostReported)
        {
            _cpuLostReported = true;
            _logger.LogError("Sin lectura de la temperatura de la CPU desde hace más de {Seconds} s: todo vuelve a la BIOS", safety.SensorTimeoutSeconds);
            Alert?.Invoke(AlertKind.SensorLost, "Sensor de CPU perdido",
                "No se puede leer la temperatura de la CPU: los ventiladores vuelven a la BIOS por seguridad.");
        }

        var states = new List<FanStateDto>(outputs.Count);
        foreach (var output in outputs)
        {
            if (!_channels.TryGetValue(output.Id, out var settings))
                continue;

            var role = pumpsSeen.ContainsKey(output.Id) ? FanRole.Pump : settings.Role;
            var controller = Controller(output.Id);
            string? note = null;
            float? target = null;
            float? sourceTemperature = null;
            var effective = settings.Mode;

            try
            {
                if (_emergency.Active)
                {
                    target = 100;
                    effective = FanMode.Fixed;
                    Apply(output, role, 100, ref note);
                    note = $"EMERGENCIA · {_emergency.Reason}";
                }
                else if (cpuLost)
                {
                    Release(output, controller);
                    effective = FanMode.Bios;
                    note = "Sensor de CPU perdido: control de la BIOS";
                }
                else if (Excluded.Contains(output.Id))
                {
                    note = "Calibrando…";
                }
                else
                {
                    switch (settings.Mode)
                    {
                        case FanMode.Bios:
                            Release(output, controller);
                            break;

                        case FanMode.Fixed:
                        {
                            var decision = controller.Compute(settings, null, output.OutputPercent, dt);
                            target = decision.Target;
                            note = decision.Note;
                            Apply(output, role, decision.Command, ref note);
                            break;
                        }

                        case FanMode.Curve:
                        {
                            sourceTemperature = Aggregate(settings.Source, readSensor);
                            if (sourceTemperature is { } t)
                            {
                                _lastValidSource[output.Id] = now;
                                var decision = controller.Compute(settings, t, output.OutputPercent, dt);
                                target = decision.Target;
                                note = decision.Note;
                                Apply(output, role, decision.Command, ref note);
                            }
                            else if (!_lastValidSource.TryGetValue(output.Id, out var last)
                                     || now - last > TimeSpan.FromSeconds(safety.SensorTimeoutSeconds))
                            {
                                Release(output, controller);
                                effective = FanMode.Bios;
                                note = "Sensor perdido: control devuelto a la BIOS";
                            }
                            else
                            {
                                note = "Esperando al sensor…"; // se mantiene el último valor unos segundos
                            }

                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Un fallo en una salida nunca debe dejarla en un estado desconocido: se suelta siempre,
                // aunque no conste como controlada (la escritura pudo quedarse a medias).
                _logger.LogError(ex, "Error controlando {Header}: se devuelve a la BIOS", output.Header);
                try { output.ReleaseToBios(); } catch { /* ya registrado */ }
                controller.Reset();
                effective = FanMode.Bios;
                note = "Error: control devuelto a la BIOS";
            }

            note = CheckStall(output, role, now) ?? note;
            states.Add(new FanStateDto(output.Id, output.Rpm, output.OutputPercent, target, effective, sourceTemperature, note));
        }

        return new FanFrameDto(timestamp, states, _emergency.Active);
    }

    /// <summary>Devuelve todo a la BIOS (al parar el servicio o ante un fallo).</summary>
    public void ReleaseAll(IReadOnlyList<IFanOutput> outputs)
    {
        foreach (var output in outputs)
        {
            try
            {
                output.ReleaseToBios();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo devolver {Header} a la BIOS", output.Header);
            }
        }

        foreach (var c in _controllers.Values)
            c.Reset();
    }

    public static float? Aggregate(TemperatureSourceDto source, Func<string, float?> readSensor)
    {
        var values = source.SensorIds
            .Select(readSensor)
            .Where(v => v is { } f && float.IsFinite(f) && f > -40 && f < 150)
            .Select(v => v!.Value)
            .ToList();

        if (values.Count == 0)
            return null;

        return source.Aggregation switch
        {
            TemperatureAggregation.Maximum => values.Max(),
            TemperatureAggregation.Average => values.Average(),
            _ => values[0],
        };
    }

    private FanChannelController Controller(string id)
    {
        if (!_controllers.TryGetValue(id, out var controller))
            _controllers[id] = controller = new FanChannelController();
        return controller;
    }

    private static void Apply(IFanOutput output, FanRole role, float requested, ref string? note)
    {
        var safe = SafetyGuard.Validate(role, requested, output.HardwareMinimumPercent, output.HardwareMaximumPercent, out var guardNote);
        if (safe is null)
        {
            output.ReleaseToBios();
            note = guardNote;
            return;
        }

        output.SetPercent(safe.Value);
        if (guardNote is not null)
            note = guardNote;
    }

    private static void Release(IFanOutput output, FanChannelController controller)
    {
        if (output.IsSoftwareControlled)
            output.ReleaseToBios();
        controller.Reset();
    }

    /// <summary>Detecta un ventilador que debería girar y no gira (avisa una vez por episodio).</summary>
    private string? CheckStall(IFanOutput output, FanRole role, DateTime now)
    {
        if (output.RpmSensorId is null || role == FanRole.Gpu)
            return null;

        var rpm = output.Rpm ?? 0;
        if (rpm >= StallRpm)
        {
            _everSpun.Add(output.Id);
            _stalledSince.Remove(output.Id);
            _stallAlerted.Remove(output.Id);
            return null;
        }

        // Conectores vacíos (nunca han girado) no cuentan.
        if (!_everSpun.Contains(output.Id) || (output.OutputPercent ?? 0) < StallMinOutput)
        {
            _stalledSince.Remove(output.Id);
            return null;
        }

        if (!_stalledSince.TryGetValue(output.Id, out var since))
        {
            _stalledSince[output.Id] = now;
            return null;
        }

        if (now - since < StallTime)
            return null;

        if (_stallAlerted.Add(output.Id))
        {
            if (role == FanRole.Pump)
            {
                _logger.LogCritical("¡La bomba ({Header}) no gira!", output.Header);
                Alert?.Invoke(AlertKind.FanStopped, "¡La bomba no gira!",
                    $"{output.Header} marca 0 RPM con un {output.OutputPercent:0} % de señal. Revisa la líquida: la CPU puede sobrecalentarse.");
            }
            else
            {
                _logger.LogWarning("{Header} no gira con un {Percent:0} % de señal", output.Header, output.OutputPercent);
                Alert?.Invoke(AlertKind.FanStopped, "Ventilador parado",
                    $"{output.Header} marca 0 RPM con un {output.OutputPercent:0} % de señal.");
            }
        }

        return role == FanRole.Pump ? "¡LA BOMBA NO GIRA!" : "¡No gira!";
    }
}
