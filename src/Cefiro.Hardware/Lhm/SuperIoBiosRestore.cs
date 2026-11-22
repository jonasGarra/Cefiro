using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace Cefiro.Hardware.Lhm;

/// <summary>
/// Refuerzo de la vuelta a la BIOS en los Nuvoton NCT6683D/6686D/6687D.
///
/// LHM guarda el bit de "modo manual" que había la primera vez que toma el control de un conector y lo
/// repone al soltarlo. Si un proceso anterior se cerró de golpe con el conector en manual, LHM guardaría
/// "manual" como estado original y al soltar lo dejaría en manual. Aquí se fuerza ese estado guardado a
/// "automático" (0) justo antes de soltar, para que la salida vuelva SIEMPRE a la curva de la BIOS.
///
/// Usa reflexión sobre campos privados de LHM (versión fijada en Directory.Packages.props). Si la
/// estructura interna cambia, no hace nada y devuelve false: LHM sigue restaurando a su manera.
/// Comprobado en tu SYS_FAN4 en la Fase 0 (volvió del 70 % a la curva de la BIOS, 51 %).
/// </summary>
internal static class SuperIoBiosRestore
{
    private static readonly string[] SupportedChips = ["NCT6683D", "NCT6686D", "NCT6687D"];

    public static bool ForceAutoOnRestore(IHardware superIoHardware)
    {
        try
        {
            var superIo = superIoHardware.GetType()
                .GetField("_superIO", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(superIoHardware);

            if (superIo is null || superIo.GetType().Name != "Nct677X")
                return false;

            var chip = superIo.GetType().GetProperty("Chip")?.GetValue(superIo)?.ToString();
            if (chip is null || !SupportedChips.Contains(chip))
                return false;

            if (superIo.GetType().GetField("_initialFanControlMode", BindingFlags.Instance | BindingFlags.NonPublic)?
                    .GetValue(superIo) is not byte[] initialModes)
                return false;

            Array.Clear(initialModes);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
