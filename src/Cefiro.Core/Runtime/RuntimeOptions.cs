namespace Cefiro.Core.Runtime;

public sealed class RuntimeOptions
{
    /// <summary>Carpeta de configuración y datos (por defecto %ProgramData%\Cefiro).</summary>
    public string DataDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Cefiro");

    public bool DemoMode { get; init; }

    /// <summary>Periodo del bucle de hardware (sensores + ventiladores).</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Cada cuántos ciclos se recalcula y se publica el estado.</summary>
    public int StatusEveryTicks { get; init; } = 5;

    /// <summary>Cada cuánto se buscan programas en conflicto.</summary>
    public TimeSpan ConflictScanInterval { get; init; } = TimeSpan.FromSeconds(30);

    public string ServiceVersion { get; init; } =
        typeof(RuntimeOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
