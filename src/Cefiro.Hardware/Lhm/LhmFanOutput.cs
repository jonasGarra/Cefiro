using Cefiro.Core.Hardware;
using LibreHardwareMonitor.Hardware;

namespace Cefiro.Hardware.Lhm;

/// <summary>Salida de ventilador de LHM: un conector de la placa o los ventiladores de la GPU.</summary>
internal sealed class LhmFanOutput : IFanOutput
{
    private readonly IHardware _hardware;
    private readonly ISensor _control;
    private readonly ISensor? _fan;
    private float? _lastSet;

    public LhmFanOutput(IHardware hardware, ISensor control, ISensor? fan, string device)
    {
        _hardware = hardware;
        _control = control;
        _fan = fan;
        Device = device;
        Id = control.Identifier.ToString();
        ControlSensorId = Id;
        RpmSensorId = fan?.Identifier.ToString();
        IsGpu = hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        Header = SensorNaming.HeaderName(control.Name);
        DefaultName = IsGpu ? $"Ventiladores GPU ({Header.Replace("GPU ", "", StringComparison.Ordinal)})" : Header;
    }

    public string Id { get; }
    public string Header { get; }
    public string Device { get; }
    public string DefaultName { get; }
    public bool IsGpu { get; }
    public string? RpmSensorId { get; }
    public string ControlSensorId { get; }
    public bool IsSuperIo => _hardware.HardwareType == HardwareType.SuperIO;

    public float HardwareMinimumPercent => _control.Control?.MinSoftwareValue ?? 0;
    public float HardwareMaximumPercent => _control.Control?.MaxSoftwareValue ?? 100;

    public float? Rpm => _fan?.Value;
    public float? OutputPercent => _control.Value;

    public bool IsSoftwareControlled => _control.Control?.ControlMode == ControlMode.Software;

    public void SetPercent(float percent)
    {
        var control = _control.Control ?? throw new InvalidOperationException($"{Header} no admite control.");
        var value = Math.Clamp(percent, HardwareMinimumPercent, HardwareMaximumPercent);

        // Solo se escribe si cambia algo (menos tráfico en el bus del chip).
        if (IsSoftwareControlled && _lastSet is { } last && Math.Abs(last - value) < 0.5f)
            return;

        control.SetSoftware(value);
        _lastSet = value;
    }

    public void ReleaseToBios()
    {
        var control = _control.Control;
        if (control is null)
            return;

        if (IsSuperIo)
            SuperIoBiosRestore.ForceAutoOnRestore(_hardware);

        control.SetDefault();
        _lastSet = null;
    }

    /// <summary>
    /// Al arrancar: toma el control con el valor actual y lo suelta enseguida. Así, si una ejecución
    /// anterior se cerró de golpe dejando el conector en manual, vuelve a quedar en manos de la BIOS.
    /// </summary>
    public void ResetToBiosAtStartup()
    {
        var control = _control.Control;
        if (control is null)
            return;

        if (IsSuperIo)
        {
            var current = Math.Clamp(_control.Value ?? 60, Math.Max(HardwareMinimumPercent, 30), HardwareMaximumPercent);
            control.SetSoftware(current);
            SuperIoBiosRestore.ForceAutoOnRestore(_hardware);
        }

        control.SetDefault();
        _lastSet = null;
    }
}
