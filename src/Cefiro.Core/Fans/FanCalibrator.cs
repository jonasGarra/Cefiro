using Cefiro.Core.Hardware;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging;

namespace Cefiro.Core.Fans;

/// <summary>
/// Calibración automática: mide las RPM a cada % y busca el mínimo al que se para y al que arranca.
/// Se ejecuta paso a paso dentro del bucle de hardware (un paso cada pocos segundos).
/// Seguridad:
///  - Bomba: solo de 100 % a 60 %. Radiador: hasta 30 %. GPU: hasta su mínimo. Solo la caja baja a 0.
///  - Se cancela sola si hay emergencia o si la CPU pasa de 85 °C / la GPU de 83 °C.
/// </summary>
public sealed class FanCalibrator
{
    private const int SettleSeconds = 6;
    private const float StoppedRpm = 60;
    private const float AbortCpuCelsius = 85;
    private const float AbortGpuCelsius = 83;

    private readonly ILogger _logger;
    private readonly Queue<(string Id, FanRole Role)> _pending = new();
    private readonly List<CalibrationResultDto> _results = [];
    private readonly List<CalibrationPointDto> _points = [];
    private (string Id, FanRole Role)? _current;
    private Queue<float> _steps = new();
    private float? _stepPercent;
    private DateTime _stepStarted;
    private readonly List<float> _stepSamples = [];
    private float? _stopPercent;
    private float? _startPercent;
    private bool _searchingStart;
    private int _totalChannels;
    private string? _message;

    public FanCalibrator(ILogger logger)
    {
        _logger = logger;
    }

    public bool Running => _current is not null || _pending.Count > 0;

    public string? CurrentChannel => _current?.Id;

    public CalibrationStatusDto Status => new(
        Running,
        _current?.Id,
        Progress(),
        [.. _results],
        _message);

    /// <summary>Progreso total: salidas terminadas más la parte hecha de la actual.</summary>
    private float Progress()
    {
        if (_totalChannels == 0)
            return 0;

        var partial = 0f;
        if (_current is not null)
        {
            var done = _points.Count + (_searchingStart ? 2 : 0);
            var total = Math.Max(1, done + _steps.Count + 1);
            partial = Math.Clamp((float)done / total, 0, 0.95f);
        }

        return Math.Clamp((_results.Count + partial) / _totalChannels, 0, 1);
    }

    public IReadOnlyList<CalibrationResultDto> Results => _results;

    public void Start(IEnumerable<(string Id, FanRole Role)> channels)
    {
        if (Running)
            throw new InvalidOperationException("Ya hay una calibración en curso.");

        _results.Clear();
        _pending.Clear();
        foreach (var c in channels)
            _pending.Enqueue(c);

        _totalChannels = _pending.Count;
        _message = _totalChannels == 0 ? "No hay salidas que calibrar." : $"Calibrando {_totalChannels} salida(s)…";
    }

    /// <summary>Cancela y devuelve la salida actual al motor de ventiladores.</summary>
    public void Cancel(string reason, FanEngine engine)
    {
        if (_current is { } c)
        {
            engine.Excluded.Remove(c.Id);
            _results.Add(new CalibrationResultDto(c.Id, [.. _points], _stopPercent, _startPercent,
                _points.FirstOrDefault()?.Rpm, $"Cancelada: {reason}"));
        }

        _pending.Clear();
        _current = null;
        _message = $"Calibración cancelada: {reason}";
        _logger.LogWarning("Calibración cancelada: {Reason}", reason);
    }

    /// <returns>true si el estado ha cambiado y conviene avisar a la interfaz.</returns>
    public bool Tick(IReadOnlyList<IFanOutput> outputs, FanEngine engine, float? cpuTemperature, float? gpuTemperature, DateTime now)
    {
        if (!Running)
            return false;

        if (engine.EmergencyActive)
        {
            Cancel("modo de emergencia", engine);
            return true;
        }

        if (cpuTemperature > AbortCpuCelsius || gpuTemperature > AbortGpuCelsius)
        {
            Cancel($"temperatura alta (CPU {cpuTemperature:0} °C, GPU {gpuTemperature:0} °C)", engine);
            return true;
        }

        if (_current is null)
        {
            if (!BeginNext(outputs, engine, now))
            {
                _message = "Calibración terminada.";
                _logger.LogInformation("Calibración terminada");
                return true;
            }

            return true;
        }

        var output = outputs.FirstOrDefault(o => o.Id == _current.Value.Id);
        if (output is null)
        {
            Finish(engine, "La salida ha desaparecido");
            return true;
        }

        // Muestras de los dos últimos segundos de cada paso.
        var elapsed = (now - _stepStarted).TotalSeconds;
        if (elapsed >= SettleSeconds - 2)
            _stepSamples.Add(output.Rpm ?? 0);

        if (elapsed < SettleSeconds)
            return false;

        var rpm = _stepSamples.Count > 0 ? _stepSamples.Average() : output.Rpm ?? 0;
        var percent = _stepPercent!.Value;

        if (!_searchingStart)
        {
            _points.Add(new CalibrationPointDto(percent, MathF.Round(rpm)));
            if (rpm < StoppedRpm && _stopPercent is null && percent > 0)
                _stopPercent = percent;
        }
        else if (rpm >= StoppedRpm)
        {
            _startPercent = percent;
            _steps.Clear(); // encontrado: ya no hace falta seguir subiendo
        }

        if (_steps.Count == 0 && !_searchingStart && _stopPercent is not null && _current.Value.Role == FanRole.Case)
        {
            // Se ha parado: ahora se busca desde 0 % el % al que vuelve a arrancar.
            _searchingStart = true;
            _steps = new Queue<float>([0, 10, 15, 20, 25, 30, 35, 40, 50]);
        }

        if (_steps.Count == 0)
        {
            Finish(engine, null);
            return true;
        }

        NextStep(output, now);
        _message = $"{output.Header}: {(_searchingStart ? "buscando el arranque" : "midiendo")} al {_stepPercent:0} %";
        return true;
    }

    private bool BeginNext(IReadOnlyList<IFanOutput> outputs, FanEngine engine, DateTime now)
    {
        while (_pending.TryDequeue(out var next))
        {
            var output = outputs.FirstOrDefault(o => o.Id == next.Id);
            if (output is null)
                continue;

            _current = next;
            _points.Clear();
            _stopPercent = null;
            _startPercent = null;
            _searchingStart = false;

            // Nunca por debajo del suelo de seguridad del rol (bomba 60 %, radiador 30 %, GPU su mínimo).
            var floor = SafetyGuard.FloorFor(next.Role, output.HardwareMinimumPercent);
            var canStop = SafetyGuard.CanStop(next.Role);
            var steps = new List<float> { 100, 90, 80, 70, 60, 50, 40, 30, 25, 20, 15, 10, 5, 0 }
                .Where(p => p >= floor || canStop)
                .ToList();
            _steps = new Queue<float>(steps);

            engine.Excluded.Add(next.Id);
            _logger.LogInformation("Calibrando {Header} ({Role}): {Steps}", output.Header, next.Role, string.Join(", ", steps));
            NextStep(output, now);
            _message = $"{output.Header}: midiendo al {_stepPercent:0} %";
            return true;
        }

        _current = null;
        return false;
    }

    private void NextStep(IFanOutput output, DateTime now)
    {
        var percent = _steps.Dequeue();
        _stepPercent = percent;
        _stepStarted = now;
        _stepSamples.Clear();

        var role = _current!.Value.Role;
        var safe = SafetyGuard.Validate(role, percent, output.HardwareMinimumPercent, output.HardwareMaximumPercent, out _);
        if (safe is null)
            output.ReleaseToBios();
        else
            output.SetPercent(safe.Value);
    }

    private void Finish(FanEngine engine, string? problem)
    {
        var current = _current!.Value;
        var maxRpm = _points.Count > 0 ? _points.Max(p => p.Rpm) : (float?)null;
        var status = problem ?? (maxRpm is null or < StoppedRpm ? "Sin giro: ¿hay algo conectado?" : "Completada");

        _results.Add(new CalibrationResultDto(current.Id, [.. _points], _stopPercent, _startPercent, maxRpm, status));
        engine.Excluded.Remove(current.Id);
        _logger.LogInformation("Calibración de {Id}: {Status}, se para en {Stop} %, arranca en {Start} %, máx {Max} RPM",
            current.Id, status, _stopPercent, _startPercent, maxRpm);
        _current = null;
    }
}
