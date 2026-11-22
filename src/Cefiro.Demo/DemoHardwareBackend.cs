using System.Diagnostics;
using Cefiro.Core.Hardware;
using Cefiro.Ipc.Contracts;

namespace Cefiro.Demo;

/// <summary>
/// PC simulado con la misma forma que el tuyo (7700X, RTX 5070, NCT6687D, bomba en CPU_FAN1,
/// radiador en PUMP_FAN1 y caja en SYS_FAN4). Física sencilla: la carga calienta, los ventiladores enfrían.
/// </summary>
public sealed class DemoHardwareBackend : IHardwareBackend
{
    private const string Board = "MSI X670E GAMING PLUS WIFI (MS-7E16) · DEMO";
    private const string Cpu = "AMD Ryzen 7 7700X · DEMO";
    private const string Gpu = "NVIDIA GeForce RTX 5070 · DEMO";
    private const string Ram = "Memoria del sistema · DEMO";

    private readonly Random _random = new(1234);
    private readonly Stopwatch _clock = new();
    private readonly List<SensorDescriptor> _sensors = [];
    private readonly Dictionary<string, float?> _values = new(StringComparer.Ordinal);
    private readonly List<DemoFan> _fans = [];
    private double _lastSeconds;

    private double _cpuLoad = 5;
    private double _gpuLoad = 2;
    private double _cpuTemp = 42;
    private double _gpuTemp = 33;
    private double _liquidTemp = 29;
    private double _caseTemp = 27;

    public string Name => "Simulador de Céfiro (modo demo)";
    public IReadOnlyList<SensorDescriptor> Sensors => _sensors;
    public IReadOnlyList<IFanOutput> FanOutputs => _fans;

    public BackendInfoDto Info { get; } = new("Simulador (modo demo)", true, true, Board, "Nuvoton NCT6687D (simulado)", Cpu, Gpu,
        ["Modo demo: los datos son simulados y no se toca el hardware."]);

    public void Open()
    {
        if (_clock.IsRunning)
            return;

        Add("/demo/cpu/temperature/0", "Temperatura CPU (Tctl/Tdie)", "Core (Tctl/Tdie)", Cpu, DeviceCategory.Cpu, SensorKind.Temperature, "Cpu");
        Add("/demo/cpu/temperature/1", "Temperatura CCD1", "CCD1 (Tdie)", Cpu, DeviceCategory.Cpu, SensorKind.Temperature, "Cpu");
        Add("/demo/cpu/load/0", "Uso de CPU", "CPU Total", Cpu, DeviceCategory.Cpu, SensorKind.Load, "Cpu");
        Add("/demo/cpu/clock/0", "Frecuencia media", "Cores (Average)", Cpu, DeviceCategory.Cpu, SensorKind.Clock, "Cpu");
        Add("/demo/cpu/power/0", "Consumo de la CPU", "Package", Cpu, DeviceCategory.Cpu, SensorKind.Power, "Cpu");

        Add("/demo/gpu/temperature/0", "Temperatura GPU", "GPU Core", Gpu, DeviceCategory.Gpu, SensorKind.Temperature, "GpuNvidia");
        Add("/demo/gpu/temperature/1", "Punto más caliente", "GPU Hot Spot", Gpu, DeviceCategory.Gpu, SensorKind.Temperature, "GpuNvidia");
        Add("/demo/gpu/load/0", "Uso de GPU", "GPU Core", Gpu, DeviceCategory.Gpu, SensorKind.Load, "GpuNvidia");
        Add("/demo/gpu/clock/0", "Frecuencia GPU", "GPU Core", Gpu, DeviceCategory.Gpu, SensorKind.Clock, "GpuNvidia");
        Add("/demo/gpu/power/0", "Consumo de la GPU", "GPU Package", Gpu, DeviceCategory.Gpu, SensorKind.Power, "GpuNvidia");
        Add("/demo/gpu/data/0", "VRAM usada", "GPU Memory Used", Gpu, DeviceCategory.Gpu, SensorKind.Data, "GpuNvidia");
        Add("/demo/gpu/data/1", "VRAM total", "GPU Memory Total", Gpu, DeviceCategory.Gpu, SensorKind.Data, "GpuNvidia");

        Add("/demo/ram/load/0", "Uso de RAM", "Memory", Ram, DeviceCategory.Memory, SensorKind.Load, "Memory");
        Add("/demo/ram/data/0", "RAM usada", "Memory Used", Ram, DeviceCategory.Memory, SensorKind.Data, "Memory");
        Add("/demo/ram/data/1", "RAM libre", "Memory Available", Ram, DeviceCategory.Memory, SensorKind.Data, "Memory");
        Add("/demo/dimm/1/temperature/0", "Temperatura RAM (ranura 1)", "DIMM #1", "Corsair CMH32GX5M2B6000Z30 (#1)", DeviceCategory.Memory, SensorKind.Temperature, "Memory");
        Add("/demo/dimm/3/temperature/0", "Temperatura RAM (ranura 3)", "DIMM #3", "Corsair CMH32GX5M2B6000Z30 (#3)", DeviceCategory.Memory, SensorKind.Temperature, "Memory");

        Add("/demo/lpc/temperature/1", "Placa base", "System", Board, DeviceCategory.Motherboard, SensorKind.Temperature, "SuperIO");
        Add("/demo/lpc/temperature/2", "VRM", "VRM MOS", Board, DeviceCategory.Motherboard, SensorKind.Temperature, "SuperIO");
        Add("/demo/lpc/temperature/3", "Chipset", "PCH", Board, DeviceCategory.Motherboard, SensorKind.Temperature, "SuperIO");
        Add("/demo/lpc/voltage/0", "+12 V", "+12V", Board, DeviceCategory.Motherboard, SensorKind.Voltage, "SuperIO");

        // Conectores de la placa: la bomba en CPU_FAN1 y el radiador en PUMP_FAN1, como en tu PC.
        AddFan(0, "CPU_FAN1", DemoFanKind.Pump, connected: true);
        AddFan(1, "PUMP_FAN1", DemoFanKind.Rs120, connected: true);
        for (var i = 1; i <= 6; i++)
            AddFan(i + 1, $"SYS_FAN{i}", DemoFanKind.Rs120, connected: i == 4);
        AddFan(10, "GPU 1", DemoFanKind.Gpu, connected: true);
        AddFan(11, "GPU 2", DemoFanKind.Gpu, connected: true);

        // Arranque "en caliente": los rotores ya giran a su velocidad de equilibrio, como en un PC encendido.
        for (var i = 0; i < 30; i++)
        {
            foreach (var fan in _fans)
            {
                fan.StepBios(fan.Kind == DemoFanKind.Gpu ? _gpuTemp : _cpuTemp);
                fan.StepPhysics(1.0, _random);
            }
        }

        _clock.Start();
        Update();
    }

    private void Add(string id, string name, string raw, string device, DeviceCategory category, SensorKind kind, string hwType)
    {
        var unit = kind switch
        {
            SensorKind.Temperature => "°C",
            SensorKind.Load or SensorKind.Control => "%",
            SensorKind.Clock => "MHz",
            SensorKind.Power => "W",
            SensorKind.Voltage => "V",
            SensorKind.Fan => "RPM",
            SensorKind.Data => "B",
            _ => "",
        };
        _sensors.Add(new SensorDescriptor(id, name, raw, device, category, kind, unit, hwType));
        _values[id] = null;
    }

    private void AddFan(int index, string header, DemoFanKind kind, bool connected)
    {
        var isGpu = kind == DemoFanKind.Gpu;
        var rpmId = isGpu ? $"/demo/gpu/fan/{index}" : $"/demo/lpc/fan/{index}";
        var controlId = isGpu ? $"/demo/gpu/control/{index}" : $"/demo/lpc/control/{index}";
        var device = isGpu ? Gpu : "Nuvoton NCT6687D (simulado)";
        var hwType = isGpu ? "GpuNvidia" : "SuperIO";
        var category = isGpu ? DeviceCategory.Gpu : DeviceCategory.Motherboard;

        Add(rpmId, isGpu ? $"Ventilador {header}" : header, isGpu ? $"GPU Fan {index - 9}" : header, isGpu ? Gpu : Board, category, SensorKind.Fan, hwType);
        Add(controlId, isGpu ? $"Salida ventilador {header}" : $"{header} (salida)", header, isGpu ? Gpu : Board, category, SensorKind.Control, hwType);

        _fans.Add(new DemoFan(controlId, rpmId, header, device, kind, connected));
    }

    public void Update()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastSeconds, 0.05, 5);
        _lastSeconds = now;

        // Carga: reposo con picos, y cada ~2 minutos una "partida" de 40 s.
        var inGame = now % 120 is > 70 and < 110;
        var cpuTarget = inGame ? 55 + Noise(15) : 4 + Math.Max(0, Noise(6));
        var gpuTarget = inGame ? 92 + Noise(5) : 1 + Math.Max(0, Noise(2));
        _cpuLoad += (cpuTarget - _cpuLoad) * Math.Min(1, dt * 0.8);
        _gpuLoad += (gpuTarget - _gpuLoad) * Math.Min(1, dt * 0.6);

        // Curvas "de BIOS" para las salidas que nadie controla.
        foreach (var fan in _fans)
            fan.StepBios(fan.Kind == DemoFanKind.Gpu ? _gpuTemp : _cpuTemp);

        var pump = _fans.First(f => f.Header == "CPU_FAN1");
        var radiator = _fans.First(f => f.Header == "PUMP_FAN1");
        var caseFans = _fans.First(f => f.Header == "SYS_FAN4");
        var gpuFans = _fans.Where(f => f.Kind == DemoFanKind.Gpu).ToList();

        var cpuPower = 30 + _cpuLoad * 1.1;
        var gpuPower = 18 + _gpuLoad * 2.3;

        // Térmica: líquido ← CPU, enfriado por el radiador; CPU ← líquido + potencia; GPU ← aire de la caja.
        var radiatorCooling = 0.15 + radiator.Rpm.GetValueOrDefault() / 2100.0 * 0.85;
        var pumpFlow = Math.Clamp(pump.Rpm.GetValueOrDefault() / 2600.0, 0.05, 1.2);
        _caseTemp += ((24 + gpuPower / 40.0 - caseFans.Rpm.GetValueOrDefault() / 900.0) - _caseTemp) * Math.Min(1, dt * 0.05);
        _liquidTemp += ((_caseTemp + cpuPower / (18 * radiatorCooling)) - _liquidTemp) * Math.Min(1, dt * 0.03);
        _cpuTemp += ((_liquidTemp + cpuPower / (5.5 * pumpFlow)) - _cpuTemp) * Math.Min(1, dt * 0.5);
        var gpuCooling = 0.35 + gpuFans.Average(f => f.Rpm.GetValueOrDefault()) / 2900.0;
        _gpuTemp += ((_caseTemp + gpuPower / (4.5 * gpuCooling)) - _gpuTemp) * Math.Min(1, dt * 0.15);

        Set("/demo/cpu/temperature/0", _cpuTemp + Noise(0.4));
        Set("/demo/cpu/temperature/1", _cpuTemp - 6 + Noise(0.6));
        Set("/demo/cpu/load/0", Math.Clamp(_cpuLoad + Noise(1.5), 0, 100));
        Set("/demo/cpu/clock/0", 3600 + _cpuLoad * 14 + Noise(60));
        Set("/demo/cpu/power/0", cpuPower + Noise(1));

        Set("/demo/gpu/temperature/0", _gpuTemp + Noise(0.3));
        Set("/demo/gpu/temperature/1", _gpuTemp + 9 + _gpuLoad / 12 + Noise(0.5));
        Set("/demo/gpu/load/0", Math.Clamp(_gpuLoad + Noise(1), 0, 100));
        Set("/demo/gpu/clock/0", _gpuLoad > 20 ? 2700 + Noise(40) : 240 + Noise(20));
        Set("/demo/gpu/power/0", gpuPower + Noise(2));
        Set("/demo/gpu/data/0", (inGame ? 8.6e9 : 1.9e9) + Noise(5e7));
        Set("/demo/gpu/data/1", 12.82e9);

        var ramUsed = 14.9e9 + (inGame ? 6e9 : 0) + Noise(1e8);
        Set("/demo/ram/load/0", ramUsed / 33.4e9 * 100);
        Set("/demo/ram/data/0", ramUsed);
        Set("/demo/ram/data/1", 33.4e9 - ramUsed);
        Set("/demo/dimm/1/temperature/0", _caseTemp + 4 + _cpuLoad / 25 + Noise(0.2));
        Set("/demo/dimm/3/temperature/0", _caseTemp + 2 + _cpuLoad / 30 + Noise(0.2));

        Set("/demo/lpc/temperature/1", _caseTemp + 2 + Noise(0.2));
        Set("/demo/lpc/temperature/2", _caseTemp + 4 + cpuPower / 15 + Noise(0.3));
        Set("/demo/lpc/temperature/3", _caseTemp + 10 + Noise(0.3));
        Set("/demo/lpc/voltage/0", 12.02 + Noise(0.01));

        foreach (var fan in _fans)
        {
            fan.StepPhysics(dt, _random);
            _values[fan.RpmSensorId!] = fan.Rpm;
            _values[fan.ControlSensorId] = fan.OutputPercent;
        }
    }

    private void Set(string id, double value) => _values[id] = (float)value;

    private double Noise(double amplitude) => (_random.NextDouble() * 2 - 1) * amplitude;

    public void ReadValues(Span<float?> destination)
    {
        for (var i = 0; i < _sensors.Count && i < destination.Length; i++)
            destination[i] = _values[_sensors[i].Id];
    }

    public void ReleaseAllFans()
    {
        foreach (var fan in _fans)
            fan.ReleaseToBios();
    }

    public void Dispose()
    {
        ReleaseAllFans();
        _clock.Stop();
    }

    private enum DemoFanKind
    {
        Rs120,
        Pump,
        Gpu,
    }

    private sealed class DemoFan(string id, string rpmId, string header, string device, DemoFanKind kind, bool connected) : IFanOutput
    {
        private float _duty = kind == DemoFanKind.Gpu ? 0 : 50;
        private float _rpm;
        private float? _software;

        public DemoFanKind Kind { get; } = kind;
        public string Id { get; } = id;
        public string Header { get; } = header;
        public string Device { get; } = device;
        public string DefaultName => IsGpu ? $"Ventiladores GPU ({Header[4..]})" : Header;
        public bool IsGpu => Kind == DemoFanKind.Gpu;
        public string? RpmSensorId { get; } = rpmId;
        public string ControlSensorId => Id;
        public float HardwareMinimumPercent => IsGpu ? 30 : 0;
        public float HardwareMaximumPercent => 100;
        public float? Rpm => _rpm;
        public float? OutputPercent => _duty;
        public bool IsSoftwareControlled => _software.HasValue;

        public void SetPercent(float percent) => _software = Math.Clamp(percent, HardwareMinimumPercent, 100);

        public void ReleaseToBios() => _software = null;

        /// <summary>Curva "de BIOS" sencilla cuando nadie controla la salida.</summary>
        public void StepBios(double temperature)
        {
            if (_software is { } s)
            {
                _duty = s;
                return;
            }

            _duty = Kind switch
            {
                DemoFanKind.Gpu => temperature < 50 ? 0 : (float)Math.Clamp(30 + (temperature - 50) * 2.2, 30, 100),
                DemoFanKind.Pump => 54,
                _ => (float)Math.Clamp(40 + (temperature - 40) * 1.2, 40, 100),
            };
        }

        public void StepPhysics(double dt, Random random)
        {
            float target;
            if (!connected)
            {
                target = 0;
            }
            else
            {
                target = Kind switch
                {
                    DemoFanKind.Pump => _duty < 20 ? 0 : (float)(1900 + _duty * 11.5),
                    DemoFanKind.Gpu => _duty < 25 ? 0 : (float)(_duty * 30.5),
                    _ => _duty < 18 ? 0 : (float)(420 + (2100 - 420) * Math.Pow((_duty - 18) / 82.0, 1.05)),
                };
            }

            // Inercia mecánica del rotor.
            _rpm += (float)((target - _rpm) * Math.Min(1, dt * 1.5));
            if (target > 0)
                _rpm += (float)((random.NextDouble() * 2 - 1) * 6);
            if (_rpm < 1)
                _rpm = 0;
        }
    }
}
