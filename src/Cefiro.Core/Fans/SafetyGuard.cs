using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Fans;

/// <summary>
/// Última barrera antes del hardware. Todo valor que se envía a un ventilador pasa por aquí.
/// Límites duros que ningún ajuste puede saltarse:
///  - Bomba: nunca por debajo del 60 % y nunca parada.
///  - Radiador: nunca por debajo del 30 % y nunca parado.
///  - GPU: nunca por debajo del mínimo de la tarjeta (30 % en tu RTX 5070).
///  - Caja: puede pararse (0 %) solo si se pide expresamente.
/// </summary>
public static class SafetyGuard
{
    public const float PumpFloorPercent = 60;
    public const float RadiatorFloorPercent = 30;

    public static float FloorFor(FanRole role, float hardwareMinimum) => role switch
    {
        FanRole.Pump => Math.Max(PumpFloorPercent, hardwareMinimum),
        FanRole.Radiator => Math.Max(RadiatorFloorPercent, hardwareMinimum),
        FanRole.Gpu => hardwareMinimum,
        _ => hardwareMinimum,
    };

    public static bool CanStop(FanRole role) => role == FanRole.Case;

    /// <summary>
    /// Devuelve el % seguro que se puede enviar, o null si el valor pedido no es válido
    /// (NaN, infinito): en ese caso la salida debe volver a la BIOS.
    /// </summary>
    public static float? Validate(FanRole role, float requested, float hardwareMinimum, float hardwareMaximum, out string? note)
    {
        note = null;
        if (!float.IsFinite(requested))
        {
            note = "Valor no válido: se devuelve el control a la BIOS";
            return null;
        }

        var value = Math.Clamp(requested, 0, 100);
        var floor = FloorFor(role, hardwareMinimum);

        if (value <= 0 && CanStop(role))
            return 0;

        if (value < floor)
        {
            note = role switch
            {
                FanRole.Pump => $"Bomba protegida: mínimo {floor:0} %",
                FanRole.Radiator => $"Radiador protegido: mínimo {floor:0} %",
                _ => $"Mínimo del hardware: {floor:0} %",
            };
            value = floor;
        }

        var max = hardwareMaximum > 0 ? Math.Min(hardwareMaximum, 100) : 100;
        return MathF.Round(Math.Min(value, max), 1);
    }
}

/// <summary>
/// Modo de emergencia: si la CPU o la GPU llegan a su temperatura crítica, todos los ventiladores
/// al 100 %. Se sale cuando ambas llevan RecoveryHoldSeconds seguidos por debajo del límite menos el margen.
/// </summary>
public sealed class EmergencyMonitor
{
    private DateTime? _calmSince;

    public bool Active { get; private set; }
    public string? Reason { get; private set; }

    /// <returns>true si el estado ha cambiado (entrada o salida).</returns>
    public bool Update(float? cpuTemperature, float? gpuTemperature, SafetySettingsDto safety, DateTime now)
    {
        var cpuHot = cpuTemperature >= safety.CpuCriticalCelsius;
        var gpuHot = gpuTemperature >= safety.GpuCriticalCelsius;

        if (cpuHot || gpuHot)
        {
            _calmSince = null;
            var reason = cpuHot
                ? $"CPU a {cpuTemperature:0} °C (límite {safety.CpuCriticalCelsius:0} °C)"
                : $"GPU a {gpuTemperature:0} °C (límite {safety.GpuCriticalCelsius:0} °C)";

            if (!Active)
            {
                Active = true;
                Reason = reason;
                return true;
            }

            Reason = reason;
            return false;
        }

        if (!Active)
            return false;

        // Para salir hay que estar claramente por debajo, y con lecturas válidas de ambas.
        var cpuCalm = cpuTemperature is { } c && c <= safety.CpuCriticalCelsius - safety.RecoveryMarginCelsius;
        var gpuCalm = gpuTemperature is null || gpuTemperature <= safety.GpuCriticalCelsius - safety.RecoveryMarginCelsius;

        if (cpuCalm && gpuCalm)
        {
            _calmSince ??= now;
            if (now - _calmSince.Value >= TimeSpan.FromSeconds(safety.RecoveryHoldSeconds))
            {
                Active = false;
                Reason = null;
                _calmSince = null;
                return true;
            }
        }
        else
        {
            _calmSince = null;
        }

        return false;
    }
}
