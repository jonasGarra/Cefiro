using System.Diagnostics;

namespace Cefiro.Core.Platform;

/// <summary>Consumo de CPU del propio proceso, normalizado como en el Administrador de tareas.</summary>
public sealed class ProcessStats
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastCpu;
    private TimeSpan _lastWall;

    public ProcessStats()
    {
        _lastCpu = _process.TotalProcessorTime;
        _lastWall = _clock.Elapsed;
    }

    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>% de CPU usado desde la última llamada (0-100, sobre todos los núcleos).</summary>
    public double SampleCpuPercent()
    {
        _process.Refresh();
        var cpu = _process.TotalProcessorTime;
        var wall = _clock.Elapsed;
        var cpuDelta = (cpu - _lastCpu).TotalMilliseconds;
        var wallDelta = (wall - _lastWall).TotalMilliseconds;
        _lastCpu = cpu;
        _lastWall = wall;

        if (wallDelta <= 0)
            return 0;

        return Math.Clamp(cpuDelta / (wallDelta * Environment.ProcessorCount) * 100.0, 0, 100);
    }

    public long WorkingSetBytes
    {
        get
        {
            _process.Refresh();
            return _process.WorkingSet64;
        }
    }
}
