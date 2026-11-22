using Cefiro.Core.Hardware;
using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Sensors;

/// <summary>
/// Elige los sensores principales del panel a partir de los nombres originales del backend.
/// Prioriza la GPU dedicada (NVIDIA/AMD discreta) sobre la integrada.
/// </summary>
public static class KeySensorResolver
{
    public static KeySensorsDto Resolve(IReadOnlyList<SensorDescriptor> sensors)
    {
        static bool Named(SensorDescriptor s, params string[] names) =>
            names.Any(n => s.RawName.Equals(n, StringComparison.OrdinalIgnoreCase));

        static bool Contains(SensorDescriptor s, string text) =>
            s.RawName.Contains(text, StringComparison.OrdinalIgnoreCase);

        var cpu = sensors.Where(s => s.Category == DeviceCategory.Cpu).ToList();
        var gpuType = sensors.Any(s => s.HardwareType == "GpuNvidia") ? "GpuNvidia"
            : sensors.Any(s => s.HardwareType == "GpuAmd") ? "GpuAmd"
            : sensors.Any(s => s.HardwareType == "GpuIntel") ? "GpuIntel" : null;
        var gpu = sensors.Where(s => s.HardwareType == gpuType).ToList();
        var board = sensors.Where(s => s.HardwareType == "SuperIO").ToList();
        var memory = sensors.Where(s => s.Category == DeviceCategory.Memory).ToList();

        return new KeySensorsDto
        {
            CpuTemperature = (cpu.FirstOrDefault(s => s.Kind == SensorKind.Temperature && Contains(s, "Tctl"))
                              ?? cpu.FirstOrDefault(s => s.Kind == SensorKind.Temperature && Named(s, "CPU Package", "Package", "Core Max"))
                              ?? cpu.FirstOrDefault(s => s.Kind == SensorKind.Temperature))?.Id,
            CpuLoad = cpu.FirstOrDefault(s => s.Kind == SensorKind.Load && Named(s, "CPU Total"))?.Id,
            CpuClock = (cpu.FirstOrDefault(s => s.Kind == SensorKind.Clock && Named(s, "Cores (Average)"))
                        ?? cpu.FirstOrDefault(s => s.Kind == SensorKind.Clock && Contains(s, "Core #1")))?.Id,
            CpuPower = cpu.FirstOrDefault(s => s.Kind == SensorKind.Power && Named(s, "Package", "CPU Package"))?.Id,

            GpuTemperature = gpu.FirstOrDefault(s => s.Kind == SensorKind.Temperature && Named(s, "GPU Core"))?.Id,
            GpuHotSpot = gpu.FirstOrDefault(s => s.Kind == SensorKind.Temperature && Named(s, "GPU Hot Spot"))?.Id,
            GpuLoad = gpu.FirstOrDefault(s => s.Kind == SensorKind.Load && Named(s, "GPU Core"))?.Id,
            GpuClock = gpu.FirstOrDefault(s => s.Kind == SensorKind.Clock && Named(s, "GPU Core"))?.Id,
            GpuPower = (gpu.FirstOrDefault(s => s.Kind == SensorKind.Power && Named(s, "GPU Package"))
                        ?? gpu.FirstOrDefault(s => s.Kind == SensorKind.Power))?.Id,
            GpuMemoryUsed = gpu.FirstOrDefault(s => s.Kind == SensorKind.Data && Named(s, "GPU Memory Used"))?.Id,
            GpuMemoryTotal = gpu.FirstOrDefault(s => s.Kind == SensorKind.Data && Named(s, "GPU Memory Total"))?.Id,

            RamLoad = memory.FirstOrDefault(s => s.Kind == SensorKind.Load && Named(s, "Memory"))?.Id,
            RamUsed = memory.FirstOrDefault(s => s.Kind == SensorKind.Data && Named(s, "Memory Used"))?.Id,
            RamAvailable = memory.FirstOrDefault(s => s.Kind == SensorKind.Data && Named(s, "Memory Available"))?.Id,
            RamTemperature = memory.FirstOrDefault(s => s.Kind == SensorKind.Temperature && Contains(s, "DIMM"))?.Id,

            BoardTemperature = board.FirstOrDefault(s => s.Kind == SensorKind.Temperature && Named(s, "System", "Motherboard"))?.Id,
            VrmTemperature = board.FirstOrDefault(s => s.Kind == SensorKind.Temperature && Contains(s, "VRM"))?.Id,
            ChipsetTemperature = board.FirstOrDefault(s => s.Kind == SensorKind.Temperature && Named(s, "PCH", "Chipset"))?.Id,
        };
    }
}
