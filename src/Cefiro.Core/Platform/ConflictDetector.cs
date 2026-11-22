using System.Diagnostics;
using Cefiro.Ipc.Contracts;
using Microsoft.Win32;

namespace Cefiro.Core.Platform;

/// <summary>
/// Detecta programas y ajustes que pueden pelearse con Céfiro por el mismo hardware.
/// </summary>
public static class ConflictDetector
{
    private sealed record Rule(string Id, string Name, string Detail, ConflictSeverity Severity, string[] Processes, bool OnlyUserSessions = false);

    private static readonly Rule[] Rules =
    [
        new("icue", "Corsair iCUE",
            "Controla la RAM Vengeance por el mismo bus (SMBus) que OpenRGB: los colores parpadearán o se quedarán fijos. Ciérralo o desinstálalo.",
            ConflictSeverity.Critical, ["iCUE", "Corsair.Service", "CorsairService"]),
        new("mysticlight", "MSI Center · Mystic Light",
            "Escribe en el mismo controlador USB de la placa que OpenRGB: los ventiladores ARGB cambiarán de color solos. Desactiva Mystic Light en MSI Center.",
            ConflictSeverity.Critical, ["Mystic_Light_Service", "LEDKeeper2", "LEDKeeper", "MysticLight"]),
        new("armourycrate", "ASUS Armoury Crate",
            "Su servicio de iluminación puede tomar el control de dispositivos RGB.",
            ConflictSeverity.Critical, ["ArmouryCrate", "LightingService", "AacAmbientLighting"]),
        new("signalrgb", "SignalRGB",
            "Controla los mismos dispositivos RGB que Céfiro. Ciérralo.",
            ConflictSeverity.Critical, ["SignalRgb", "SignalRgbLauncher"]),
        new("lconnect", "Lian Li L-Connect",
            "Puede controlar ventiladores e iluminación a la vez que Céfiro.",
            ConflictSeverity.Warning, ["L-Connect 3", "L-Connect3", "LConnect3"]),
        new("nzxtcam", "NZXT CAM",
            "Puede controlar ventiladores e iluminación a la vez que Céfiro.",
            ConflictSeverity.Warning, ["NZXT CAM"]),
        new("rgbfusion", "Gigabyte RGB Fusion / Control Center",
            "Puede controlar la iluminación a la vez que Céfiro.",
            ConflictSeverity.Warning, ["RGBFusion", "GCC"]),
        new("polychrome", "ASRock Polychrome",
            "Puede controlar la iluminación a la vez que Céfiro.",
            ConflictSeverity.Warning, ["PolychromeRGB", "Polychrome Sync"]),
        new("fancontrol", "FanControl",
            "Controla los mismos ventiladores: los dos programas se pisarán la velocidad.",
            ConflictSeverity.Warning, ["FanControl"]),
        new("afterburner", "MSI Afterburner",
            "Si tiene activada su curva de ventiladores, se pelea con Céfiro por los de la gráfica.",
            ConflictSeverity.Warning, ["MSIAfterburner"]),
        new("openrgbgui", "Ventana de OpenRGB abierta",
            "Céfiro usa el servicio de OpenRGB; una segunda instancia abierta puede detectar y controlar los dispositivos a la vez.",
            ConflictSeverity.Info, ["OpenRGB"], OnlyUserSessions: true),
        new("hwinfo", "HWiNFO",
            "Solo lee sensores, pero su acceso al SMBus puede causar parpadeos en la RAM. Si los ves, desactiva en HWiNFO la lectura del SMBus.",
            ConflictSeverity.Info, ["HWiNFO64", "HWiNFO32"]),
    ];

    /// <summary>Busca procesos en conflicto (se puede llamar desde el servicio).</summary>
    public static IReadOnlyList<ConflictDto> DetectProcesses()
    {
        var found = new List<ConflictDto>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return found;
        }

        try
        {
            foreach (var rule in Rules)
            {
                var match = processes.Any(p =>
                {
                    try
                    {
                        if (!rule.Processes.Any(n => string.Equals(p.ProcessName, n, StringComparison.OrdinalIgnoreCase)))
                            return false;
                        return !rule.OnlyUserSessions || p.SessionId != 0;
                    }
                    catch
                    {
                        return false;
                    }
                });

                if (match)
                    found.Add(new ConflictDto(rule.Id, rule.Name, rule.Detail, rule.Severity));
            }
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }

        return found;
    }

    /// <summary>
    /// Iluminación dinámica de Windows 11. Es un ajuste del usuario (HKCU), así que hay que
    /// comprobarlo desde la interfaz, no desde el servicio (que corre como SYSTEM).
    /// </summary>
    public static ConflictDto? DetectWindowsDynamicLighting()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Lighting");
            if (key?.GetValue("AmbientLightingEnabled") is int enabled && enabled != 0)
            {
                return new ConflictDto("dynamiclighting", "Iluminación dinámica de Windows",
                    "Windows puede cambiar los colores de los dispositivos compatibles por su cuenta. Desactívala en " +
                    "Configuración → Personalización → Iluminación dinámica.",
                    ConflictSeverity.Warning);
            }
        }
        catch
        {
            // Sin acceso al registro: no se informa.
        }

        return null;
    }
}
