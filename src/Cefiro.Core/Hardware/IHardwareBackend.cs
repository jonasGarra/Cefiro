using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Hardware;

/// <summary>Descripción de un sensor que ofrece el backend de hardware.</summary>
/// <param name="RawName">Nombre original (en inglés) del backend, para detectar sensores clave.</param>
/// <param name="HardwareType">Tipo de hardware del backend (Cpu, GpuNvidia, SuperIO…).</param>
public sealed record SensorDescriptor(
    string Id,
    string Name,
    string RawName,
    string Device,
    DeviceCategory Category,
    SensorKind Kind,
    string Unit,
    string HardwareType)
{
    public SensorDto ToDto() => new(Id, Name, Device, Category, Kind, Unit);
}

/// <summary>
/// Una salida de ventilador controlable (conector de la placa o ventiladores de la GPU).
/// Las implementaciones NO validan nada: eso lo hace siempre el SafetyGuard antes de llamar.
/// Solo se usa desde el hilo del bucle de hardware.
/// </summary>
public interface IFanOutput
{
    /// <summary>ID estable (el del sensor de control).</summary>
    string Id { get; }

    /// <summary>Conector físico: CPU_FAN1, PUMP_FAN1, SYS_FAN1… o "GPU 1".</summary>
    string Header { get; }

    string Device { get; }
    string DefaultName { get; }
    bool IsGpu { get; }
    string? RpmSensorId { get; }
    string ControlSensorId { get; }

    /// <summary>Mínimo que acepta el hardware (p. ej. 30 % en la GPU).</summary>
    float HardwareMinimumPercent { get; }

    float HardwareMaximumPercent { get; }

    float? Rpm { get; }

    /// <summary>Salida PWM actual leída del hardware (%).</summary>
    float? OutputPercent { get; }

    bool IsSoftwareControlled { get; }

    void SetPercent(float percent);

    /// <summary>Devuelve la salida a la BIOS / al driver (modo automático).</summary>
    void ReleaseToBios();
}

/// <summary>Origen de los datos de hardware (LibreHardwareMonitor real o simulado en modo demo).</summary>
public interface IHardwareBackend : IDisposable
{
    string Name { get; }

    void Open();

    /// <summary>Refresca los sensores. Solo desde el hilo del bucle de hardware.</summary>
    void Update();

    IReadOnlyList<SensorDescriptor> Sensors { get; }

    /// <summary>Copia los valores actuales en el mismo orden que <see cref="Sensors"/>.</summary>
    void ReadValues(Span<float?> destination);

    IReadOnlyList<IFanOutput> FanOutputs { get; }

    BackendInfoDto Info { get; }

    /// <summary>Devuelve todas las salidas a la BIOS. Debe funcionar incluso a medio fallo.</summary>
    void ReleaseAllFans();
}
