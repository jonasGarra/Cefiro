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

    /// <summary>Cada cuánto se miran los programas abiertos para el cambio automático de perfil.</summary>
    public TimeSpan ProgramScanInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Programas abiertos ("nombre.exe" en minúsculas). Por defecto los procesos reales;
    /// las pruebas lo sustituyen.
    /// </summary>
    public Func<IReadOnlySet<string>>? RunningPrograms { get; init; }

    public string ServiceVersion { get; init; } =
        typeof(RuntimeOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
