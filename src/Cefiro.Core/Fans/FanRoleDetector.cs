using Cefiro.Core.Hardware;
using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Fans;

/// <summary>
/// Deduce qué hay conectado a cada salida. No se fía solo del nombre del conector:
/// una bomba de líquida gira mucho más rápido que cualquier ventilador de 120 mm con la misma señal.
/// En tu PC, CPU_FAN1 da 2521 RPM con un 54 % (bomba) y PUMP_FAN1 da 1102 RPM (radiador).
/// </summary>
public static class FanRoleDetector
{
    /// <summary>RPM por encima de las que una salida se considera bomba (un RS120 llega a 2100 RPM al 100 %).</summary>
    public const float PumpRpmThreshold = 2300;

    /// <param name="knownPumps">Salidas que ya se vieron girar como una bomba en algún momento (la marca no se pierde).</param>
    public static IReadOnlyDictionary<string, (FanRole Role, string Evidence)> Detect(
        IReadOnlyList<IFanOutput> outputs,
        IReadOnlyDictionary<string, string>? knownPumps = null)
    {
        var result = new Dictionary<string, (FanRole, string)>();

        // 1) Salidas que por sus RPM solo pueden ser una bomba (ahora o en algún momento anterior).
        var pumpBySpeed = outputs
            .Where(o => !o.IsGpu && (LooksLikePump(o) || knownPumps?.ContainsKey(o.Id) == true))
            .ToList();

        foreach (var output in outputs)
        {
            if (output.IsGpu)
            {
                result[output.Id] = (FanRole.Gpu, "Ventiladores de la tarjeta gráfica.");
                continue;
            }

            if (pumpBySpeed.Contains(output))
            {
                var evidence = LooksLikePump(output)
                    ? Evidence(output.Rpm, output.OutputPercent)
                    : knownPumps![output.Id];
                result[output.Id] = (FanRole.Pump, evidence);
                continue;
            }

            var header = output.Header.ToUpperInvariant();
            if (header.StartsWith("PUMP", StringComparison.Ordinal))
            {
                // Si la bomba está en otro conector y aquí hay algo girando como un ventilador,
                // lo más probable es que aquí estén los ventiladores del radiador.
                if (pumpBySpeed.Count > 0 && output.Rpm is > 0)
                {
                    result[output.Id] = (FanRole.Radiator,
                        $"La bomba se ha detectado en {pumpBySpeed[0].Header}; aquí gira algo a {output.Rpm:0} RPM, " +
                        "como un ventilador: seguramente los del radiador.");
                }
                else
                {
                    result[output.Id] = (FanRole.Pump, "Conector de bomba de la placa.");
                }

                continue;
            }

            if (header.StartsWith("CPU", StringComparison.Ordinal))
            {
                result[output.Id] = (FanRole.Radiator,
                    "Conector de la CPU: con refrigeración líquida suelen ir aquí los ventiladores del radiador.");
                continue;
            }

            result[output.Id] = (FanRole.Case, output.Rpm is > 0
                ? "Conector de sistema con ventiladores de caja."
                : "Conector de sistema (ahora mismo no gira nada).");
        }

        return result;
    }

    /// <summary>Firma de bomba: muchas RPM sin que la señal sea alta.</summary>
    public static bool LooksLikePump(IFanOutput output) =>
        !output.IsGpu && output.Rpm is { } rpm && rpm > PumpRpmThreshold && (output.OutputPercent ?? 100) < 90;

    public static string Evidence(float? rpm, float? percent) =>
        $"Gira a {rpm:0} RPM con un {percent:0} % de señal: ningún ventilador de 120 mm llega a eso, así que es la bomba.";
}
