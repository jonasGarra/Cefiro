using System.Text.RegularExpressions;
using Cefiro.Ipc.Contracts;
using LibreHardwareMonitor.Hardware;

namespace Cefiro.Hardware.Lhm;

/// <summary>Filtra y traduce al español los sensores de LibreHardwareMonitor.</summary>
internal static partial class SensorNaming
{
    public static DeviceCategory Category(HardwareType type) => type switch
    {
        HardwareType.Cpu => DeviceCategory.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => DeviceCategory.Gpu,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => DeviceCategory.Motherboard,
        HardwareType.Memory => DeviceCategory.Memory,
        _ => DeviceCategory.Other,
    };

    public static SensorKind? Kind(SensorType type) => type switch
    {
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Load => SensorKind.Load,
        SensorType.Clock => SensorKind.Clock,
        SensorType.Power => SensorKind.Power,
        SensorType.Voltage => SensorKind.Voltage,
        SensorType.Fan => SensorKind.Fan,
        SensorType.Control => SensorKind.Control,
        SensorType.Data => SensorKind.Data,
        _ => null,
    };

    public static string Unit(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Load => "%",
        SensorKind.Clock => "MHz",
        SensorKind.Power => "W",
        SensorKind.Voltage => "V",
        SensorKind.Fan => "RPM",
        SensorKind.Control => "%",
        SensorKind.Data => "B",
        _ => "",
    };

    /// <summary>¿Merece la pena mostrar y guardar este sensor?</summary>
    public static bool Include(IHardware hardware, ISensor sensor)
    {
        if (Kind(sensor.SensorType) is null)
            return false;

        var name = sensor.Name;
        var id = hardware.Identifier.ToString();

        if (name.StartsWith("D3D ", StringComparison.Ordinal))
            return false;

        if (hardware.HardwareType == HardwareType.Memory)
        {
            if (id.StartsWith("/vram", StringComparison.Ordinal))
                return false; // memoria virtual: no aporta

            if (id.StartsWith("/memory/dimm", StringComparison.Ordinal))
                return sensor.SensorType == SensorType.Temperature && name.StartsWith("DIMM", StringComparison.Ordinal);
        }

        if (hardware.HardwareType == HardwareType.Cpu)
        {
            if (sensor.SensorType == SensorType.Voltage && name.EndsWith(" VID", StringComparison.Ordinal))
                return false;
            if (sensor.SensorType == SensorType.Clock && name.StartsWith("Core #", StringComparison.Ordinal) && name.EndsWith("(Effective)", StringComparison.Ordinal))
                return false;
        }

        if (hardware.HardwareType == HardwareType.SuperIO && sensor.SensorType == SensorType.Voltage && name == "VRef")
            return false;

        return true;
    }

    /// <summary>Descarta sensores que no existen físicamente (la placa los expone a 0).</summary>
    public static bool HasPlausibleValue(ISensor sensor) => sensor.SensorType switch
    {
        SensorType.Temperature => sensor.Value is > 0,
        SensorType.Voltage => sensor.Value is > 0,
        _ => true,
    };

    public static string Translate(IHardware hardware, ISensor sensor)
    {
        var n = sensor.Name;
        var type = sensor.SensorType;

        switch (hardware.HardwareType)
        {
            case HardwareType.Cpu:
                if (type == SensorType.Temperature)
                {
                    if (n == "Core (Tctl/Tdie)") return "Temperatura CPU (Tctl/Tdie)";
                    if (CcdRegex().Match(n) is { Success: true } ccd) return $"Temperatura CCD{ccd.Groups[1].Value}";
                    if (n is "Package" or "CPU Package") return "Temperatura CPU (paquete)";
                    if (n == "Core Max") return "Núcleo más caliente";
                    if (n == "Core Average") return "Temperatura media de núcleos";
                }
                else if (type == SensorType.Load)
                {
                    if (n == "CPU Total") return "Uso de CPU";
                    if (n == "CPU Core Max") return "Hilo más cargado";
                    if (CpuThreadRegex().Match(n) is { Success: true } t) return $"Uso hilo {t.Groups[1].Value}";
                }
                else if (type == SensorType.Clock)
                {
                    if (n == "Bus Speed") return "Frecuencia del bus";
                    if (n == "Cores (Average)") return "Frecuencia media";
                    if (n == "Cores (Average Effective)") return "Frecuencia efectiva media";
                    if (CoreRegex().Match(n) is { Success: true } c) return $"Frecuencia núcleo {c.Groups[1].Value}";
                }
                else if (type == SensorType.Power)
                {
                    if (n is "Package" or "CPU Package") return "Consumo de la CPU";
                    if (CoreSmuRegex().Match(n) is { Success: true } p) return $"Consumo núcleo {p.Groups[1].Value}";
                    if (n == "SoC") return "Consumo SoC";
                }
                else if (type == SensorType.Voltage)
                {
                    if (n.StartsWith("Core (SVI", StringComparison.Ordinal)) return "Voltaje de núcleos";
                    if (n.StartsWith("SoC (SVI", StringComparison.Ordinal)) return "Voltaje SoC";
                }

                break;

            case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                switch (type)
                {
                    case SensorType.Temperature:
                        if (n == "GPU Core") return "Temperatura GPU";
                        if (n == "GPU Hot Spot") return "Punto más caliente";
                        if (n == "GPU Memory Junction") return "Unión de memoria";
                        if (HotSpotRegex().Match(n) is { Success: true } hs) return $"Punto caliente {hs.Groups[1].Value}";
                        if (GpuMemRegex().Match(n) is { Success: true } gm) return $"Memoria GPU {gm.Groups[1].Value}";
                        break;
                    case SensorType.Load:
                        if (n == "GPU Core") return "Uso de GPU";
                        if (n == "GPU Memory Controller") return "Controlador de memoria";
                        if (n == "GPU Video Engine") return "Motor de vídeo";
                        if (n == "GPU Bus") return "Bus PCIe";
                        if (n == "GPU Memory") return "VRAM ocupada";
                        if (n == "GPU Power") return "Límite de consumo usado";
                        if (n == "GPU Board Power") return "Consumo de la tarjeta (%)";
                        break;
                    case SensorType.Clock:
                        if (n == "GPU Core") return "Frecuencia GPU";
                        if (n == "GPU Memory") return "Frecuencia de memoria";
                        break;
                    case SensorType.Power:
                        if (n == "GPU Package") return "Consumo de la GPU";
                        break;
                    case SensorType.Voltage:
                        if (n == "GPU Core Voltage") return "Voltaje GPU";
                        break;
                    case SensorType.Data:
                        if (n == "GPU Memory Used") return "VRAM usada";
                        if (n == "GPU Memory Free") return "VRAM libre";
                        if (n == "GPU Memory Total") return "VRAM total";
                        break;
                    case SensorType.Fan:
                        if (GpuFanRegex().Match(n) is { Success: true } f) return $"Ventilador GPU {f.Groups[1].Value}";
                        break;
                    case SensorType.Control:
                        if (GpuFanRegex().Match(n) is { Success: true } fc) return $"Salida ventilador GPU {fc.Groups[1].Value}";
                        break;
                }

                break;

            case HardwareType.Memory:
                if (type == SensorType.Load && n == "Memory") return "Uso de RAM";
                if (n == "Memory Used") return "RAM usada";
                if (n == "Memory Available") return "RAM libre";
                if (type == SensorType.Temperature && DimmRegex().Match(n) is { Success: true } d) return $"Temperatura RAM (ranura {d.Groups[1].Value})";
                break;

            case HardwareType.SuperIO:
                switch (type)
                {
                    case SensorType.Temperature:
                        return n switch
                        {
                            "CPU" => "CPU (sensor de la placa)",
                            "System" => "Placa base",
                            "VRM MOS" => "VRM",
                            "PCH" => "Chipset",
                            "CPU Socket" => "Zócalo de la CPU",
                            "PCIe x1" => "Zona PCIe x1",
                            _ => n.Replace("M2 #", "M.2 n.º ", StringComparison.Ordinal),
                        };
                    case SensorType.Voltage:
                        return n switch
                        {
                            "+12V" => "+12 V",
                            "+5V" => "+5 V",
                            "DIMM" => "Voltaje de la RAM",
                            "CPU Termination" => "Terminación CPU",
                            "VSB" => "3,3 V en reposo",
                            "CMOS Battery" => "Pila CMOS",
                            _ => n.Replace("Voltage #", "Voltaje ", StringComparison.Ordinal),
                        };
                    case SensorType.Fan:
                        return HeaderName(n);
                    case SensorType.Control:
                        return $"{HeaderName(n)} (salida)";
                }

                break;
        }

        return n;
    }

    /// <summary>Nombre del conector físico a partir del nombre de LHM (NCT6687D de MSI).</summary>
    public static string HeaderName(string lhmName)
    {
        if (lhmName == "CPU Fan") return "CPU_FAN1";
        if (lhmName is "Pump Fan" or "Pump Fan #1" or "CPU Pump Fan") return "PUMP_FAN1";
        if (lhmName == "Pump Fan #2") return "PUMP_FAN2";
        if (SystemFanRegex().Match(lhmName) is { Success: true } m) return $"SYS_FAN{m.Groups[1].Value}";
        if (GpuFanRegex().Match(lhmName) is { Success: true } g) return $"GPU {g.Groups[1].Value}";
        return lhmName;
    }

    [GeneratedRegex(@"^CCD(\d+) \(Tdie\)$")] private static partial Regex CcdRegex();
    [GeneratedRegex(@"^CPU Core #(\d+)$")] private static partial Regex CpuThreadRegex();
    [GeneratedRegex(@"^Core #(\d+)$")] private static partial Regex CoreRegex();
    [GeneratedRegex(@"^Core #(\d+) \(SMU\)$")] private static partial Regex CoreSmuRegex();
    [GeneratedRegex(@"^GPU Hot Spot #(\d+)$")] private static partial Regex HotSpotRegex();
    [GeneratedRegex(@"^GPU Memory #(\d+)$")] private static partial Regex GpuMemRegex();
    [GeneratedRegex(@"^GPU Fan (\d+)$")] private static partial Regex GpuFanRegex();
    [GeneratedRegex(@"^DIMM #(\d+)$")] private static partial Regex DimmRegex();
    [GeneratedRegex(@"^System Fan #(\d+)$")] private static partial Regex SystemFanRegex();
}
