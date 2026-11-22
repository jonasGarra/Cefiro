using System.Reflection;
using System.Security.Principal;
using Cefiro.Core.Hardware;
using Cefiro.Ipc.Contracts;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Cefiro.Hardware.Lhm;

/// <summary>Backend real basado en LibreHardwareMonitor (con el driver PawnIO).</summary>
public sealed class LhmHardwareBackend : IHardwareBackend
{
    /// <summary>Los módulos de RAM (temperatura por SMBus) se leen cada N ciclos: son lentos y cambian poco.</summary>
    private const int SlowHardwareEvery = 5;

    private readonly ILogger _logger;
    private readonly List<IHardware> _fastHardware = [];
    private readonly List<IHardware> _slowHardware = [];
    private readonly List<ISensor> _sensors = [];
    private readonly List<SensorDescriptor> _descriptors = [];
    private readonly List<LhmFanOutput> _fanOutputs = [];
    private Computer? _computer;
    private long _updates;
    private BackendInfoDto _info = new("LibreHardwareMonitor", false, false, null, null, null, null, []);

    public LhmHardwareBackend(ILogger<LhmHardwareBackend> logger)
    {
        _logger = logger;
    }

    public string Name { get; } = $"LibreHardwareMonitor {LibraryVersion()}";

    public IReadOnlyList<SensorDescriptor> Sensors => _descriptors;
    public IReadOnlyList<IFanOutput> FanOutputs => _fanOutputs;
    public BackendInfoDto Info => _info;

    public void Open()
    {
        if (_computer is not null)
            return;

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = false,
        };

        computer.Open();
        _computer = computer;

        var all = Flatten(computer.Hardware).ToList();
        var hasDiscreteGpu = all.Any(h => h.HardwareType == HardwareType.GpuNvidia)
                             || all.Any(h => h.HardwareType == HardwareType.GpuAmd && !IsIntegratedGpu(h));

        foreach (var hw in all)
        {
            // La gráfica integrada no aporta nada si hay una dedicada (y sus contadores D3D gastan CPU).
            if (hasDiscreteGpu && hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel && IsIntegratedGpu(hw))
                continue;

            if (hw.HardwareType == HardwareType.Motherboard)
                continue; // sus sensores están en el SuperIO (subhardware)

            if (hw.HardwareType == HardwareType.Memory && hw.Identifier.ToString().StartsWith("/vram", StringComparison.Ordinal))
                continue;

            if (hw.HardwareType == HardwareType.Memory && hw.Identifier.ToString().StartsWith("/memory/dimm", StringComparison.Ordinal))
                _slowHardware.Add(hw);
            else
                _fastHardware.Add(hw);
        }

        // Dos lecturas para que los valores sean válidos antes de construir el catálogo.
        UpdateAll();
        Thread.Sleep(500);
        UpdateAll();

        var motherboard = all.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
        var superIo = all.FirstOrDefault(h => h.HardwareType == HardwareType.SuperIO);

        foreach (var hw in _fastHardware.Concat(_slowHardware))
        {
            var device = hw.HardwareType == HardwareType.SuperIO && motherboard is not null ? motherboard.Name : hw.Name;
            foreach (var sensor in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
            {
                if (!SensorNaming.Include(hw, sensor) || !SensorNaming.HasPlausibleValue(sensor))
                    continue;

                var kind = SensorNaming.Kind(sensor.SensorType)!.Value;
                _sensors.Add(sensor);
                _descriptors.Add(new SensorDescriptor(
                    sensor.Identifier.ToString(),
                    SensorNaming.Translate(hw, sensor),
                    sensor.Name,
                    device,
                    SensorNaming.Category(hw.HardwareType),
                    kind,
                    SensorNaming.Unit(kind),
                    hw.HardwareType.ToString()));
            }

            if (hw.HardwareType is HardwareType.SuperIO or HardwareType.GpuNvidia or HardwareType.GpuAmd)
            {
                foreach (var control in hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control is not null))
                {
                    var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Index == control.Index)
                              ?? hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Name == control.Name);
                    _fanOutputs.Add(new LhmFanOutput(hw, control, fan, hw.Name));
                }
            }
        }

        // Si una ejecución anterior se cerró de golpe, deja todo otra vez en manos de la BIOS.
        foreach (var output in _fanOutputs)
        {
            try
            {
                output.ResetToBiosAtStartup();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo restablecer {Header} a la BIOS al arrancar", output.Header);
            }
        }

        var warnings = new List<string>();
        var isAdmin = IsAdministrator();
        var pawnIo = PawnIoInstalled();
        if (!isAdmin)
            warnings.Add("Sin permisos de administrador: no se pueden leer los sensores de la placa ni controlar ventiladores.");
        if (!pawnIo)
            warnings.Add("Falta el driver PawnIO: instálalo para leer la placa y controlar sus ventiladores.");
        if (superIo is null)
            warnings.Add("No se ha encontrado el chip de sensores de la placa base.");

        var gpu = all.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                  ?? all.FirstOrDefault(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel);

        _info = new BackendInfoDto(
            Name,
            isAdmin,
            pawnIo,
            motherboard?.Name,
            superIo?.Name,
            all.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu)?.Name,
            gpu?.Name,
            warnings);

        foreach (var w in warnings)
            _logger.LogWarning("{Warning}", w);
    }

    public void Update()
    {
        if (_computer is null)
            return;

        foreach (var hw in _fastHardware)
            hw.Update();

        if (_updates % SlowHardwareEvery == 0)
        {
            foreach (var hw in _slowHardware)
                hw.Update();
        }

        _updates++;
    }

    private void UpdateAll()
    {
        foreach (var hw in _fastHardware.Concat(_slowHardware))
            hw.Update();
    }

    public void ReadValues(Span<float?> destination)
    {
        var count = Math.Min(destination.Length, _sensors.Count);
        for (var i = 0; i < count; i++)
        {
            var v = _sensors[i].Value;
            destination[i] = v is { } f && float.IsFinite(f) ? f : null;
        }
    }

    public void ReleaseAllFans()
    {
        foreach (var output in _fanOutputs)
        {
            try
            {
                output.ReleaseToBios();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo devolver {Header} a la BIOS", output.Header);
            }
        }
    }

    public void Dispose()
    {
        if (_computer is null)
            return;

        ReleaseAllFans();
        try { _computer.Close(); }
        catch (Exception ex) { _logger.LogError(ex, "Error cerrando LibreHardwareMonitor"); }
        _computer = null;
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> hardware)
    {
        foreach (var hw in hardware)
        {
            yield return hw;
            foreach (var sub in Flatten(hw.SubHardware))
                yield return sub;
        }
    }

    private static bool IsIntegratedGpu(IHardware hw) =>
        hw.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
        || hw.Name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
        || (hw.HardwareType == HardwareType.GpuIntel && !hw.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase));

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool PawnIoInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key is not null;
    }

    private static string LibraryVersion() =>
        typeof(Computer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(Computer).Assembly.GetName().Version?.ToString() ?? "?";
}
