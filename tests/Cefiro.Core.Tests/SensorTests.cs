using Cefiro.Core.Fans;
using Cefiro.Core.Hardware;
using Cefiro.Core.Sensors;
using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Tests;

public class SensorHistoryTests
{
    [Fact]
    public void Last60Seconds_returns_newest_values_at_the_end()
    {
        var history = new SensorHistory(2);
        for (var i = 0; i < 100; i++)
            history.Add(i * 1000, [i, null]);

        var series = history.GetSeries(0, HistoryRange.Last60Seconds, out var interval);

        Assert.Equal(1000, interval);
        Assert.Equal(60, series.Length);
        Assert.Equal(99f, series[^1]);
        Assert.Equal(40f, series[0]);
        Assert.All(history.GetSeries(1, HistoryRange.Last60Seconds, out _), v => Assert.Null(v));
    }

    [Fact]
    public void Series_is_padded_with_nulls_until_enough_data()
    {
        var history = new SensorHistory(1);
        history.Add(0, [5f]);
        history.Add(1000, [6f]);

        var series = history.GetSeries(0, HistoryRange.Last10Minutes, out _);

        Assert.Equal(SensorHistory.FineCapacity, series.Length);
        Assert.Null(series[0]);
        Assert.Equal(5f, series[^2]);
        Assert.Equal(6f, series[^1]);
    }

    [Fact]
    public void Hour_view_averages_every_five_seconds_and_ignores_gaps()
    {
        var history = new SensorHistory(1);
        float?[] samples = [10, 20, null, 30, 40, 100, 100, 100, 100, 100];
        for (var i = 0; i < samples.Length; i++)
            history.Add(i * 1000, [samples[i]]);

        var series = history.GetSeries(0, HistoryRange.LastHour, out var interval);

        Assert.Equal(5000, interval);
        Assert.Equal(25f, series[^2]); // (10+20+30+40)/4: el hueco no cuenta
        Assert.Equal(100f, series[^1]);
    }

    [Fact]
    public void Ring_buffer_wraps_without_losing_order()
    {
        var history = new SensorHistory(1);
        for (var i = 0; i < SensorHistory.FineCapacity * 2 + 7; i++)
            history.Add(i, [i]);

        var series = history.GetSeries(0, HistoryRange.Last10Minutes, out _);
        for (var k = 1; k < series.Length; k++)
            Assert.Equal(series[k - 1]!.Value + 1, series[k]!.Value);
    }

    [Fact]
    public void Non_finite_values_are_stored_as_missing()
    {
        var history = new SensorHistory(1);
        history.Add(0, [float.NaN]);
        history.Add(1, [float.PositiveInfinity]);
        Assert.All(history.GetSeries(0, HistoryRange.Last60Seconds, out _), v => Assert.Null(v));
    }
}

public class KeySensorResolverTests
{
    private static SensorDescriptor D(string id, string raw, DeviceCategory cat, SensorKind kind, string hw) =>
        new(id, raw, raw, "dev", cat, kind, "", hw);

    [Fact]
    public void Picks_the_sensors_of_this_pc_and_prefers_the_discrete_gpu()
    {
        // Nombres reales vistos en la Fase 0 (LHM 0.9.7-pre785).
        SensorDescriptor[] sensors =
        [
            D("/amdcpu/0/temperature/11", "CCD1 (Tdie)", DeviceCategory.Cpu, SensorKind.Temperature, "Cpu"),
            D("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", DeviceCategory.Cpu, SensorKind.Temperature, "Cpu"),
            D("/amdcpu/0/load/0", "CPU Total", DeviceCategory.Cpu, SensorKind.Load, "Cpu"),
            D("/amdcpu/0/power/0", "Package", DeviceCategory.Cpu, SensorKind.Power, "Cpu"),
            D("/gpu-amd/0/temperature/0", "GPU Core", DeviceCategory.Gpu, SensorKind.Temperature, "GpuAmd"),
            D("/gpu-nvidia/0/temperature/0", "GPU Core", DeviceCategory.Gpu, SensorKind.Temperature, "GpuNvidia"),
            D("/gpu-nvidia/0/temperature/2", "GPU Hot Spot", DeviceCategory.Gpu, SensorKind.Temperature, "GpuNvidia"),
            D("/gpu-nvidia/0/power/0", "GPU Package", DeviceCategory.Gpu, SensorKind.Power, "GpuNvidia"),
            D("/ram/load/0", "Memory", DeviceCategory.Memory, SensorKind.Load, "Memory"),
            D("/memory/dimm/1/temperature/0", "DIMM #1", DeviceCategory.Memory, SensorKind.Temperature, "Memory"),
            D("/lpc/nct6687d/0/temperature/1", "System", DeviceCategory.Motherboard, SensorKind.Temperature, "SuperIO"),
            D("/lpc/nct6687d/0/temperature/2", "VRM MOS", DeviceCategory.Motherboard, SensorKind.Temperature, "SuperIO"),
            D("/lpc/nct6687d/0/temperature/3", "PCH", DeviceCategory.Motherboard, SensorKind.Temperature, "SuperIO"),
        ];

        var keys = KeySensorResolver.Resolve(sensors);

        Assert.Equal("/amdcpu/0/temperature/2", keys.CpuTemperature);
        Assert.Equal("/amdcpu/0/load/0", keys.CpuLoad);
        Assert.Equal("/amdcpu/0/power/0", keys.CpuPower);
        Assert.Equal("/gpu-nvidia/0/temperature/0", keys.GpuTemperature);
        Assert.Equal("/gpu-nvidia/0/temperature/2", keys.GpuHotSpot);
        Assert.Equal("/gpu-nvidia/0/power/0", keys.GpuPower);
        Assert.Equal("/ram/load/0", keys.RamLoad);
        Assert.Equal("/memory/dimm/1/temperature/0", keys.RamTemperature);
        Assert.Equal("/lpc/nct6687d/0/temperature/1", keys.BoardTemperature);
        Assert.Equal("/lpc/nct6687d/0/temperature/2", keys.VrmTemperature);
        Assert.Equal("/lpc/nct6687d/0/temperature/3", keys.ChipsetTemperature);
    }
}

public class FanRoleDetectorTests
{
    internal sealed class FakeOutput(string header, float? rpm, float? output, bool gpu = false) : IFanOutput
    {
        public string Id => header;
        public string Header => header;
        public string Device => "test";
        public string DefaultName => header;
        public bool IsGpu => gpu;
        public string? RpmSensorId => null;
        public string ControlSensorId => header;
        public float HardwareMinimumPercent => gpu ? 30 : 0;
        public float HardwareMaximumPercent => 100;
        public float? Rpm => rpm;
        public float? OutputPercent => output;
        public bool IsSoftwareControlled => false;
        public float? LastSet { get; private set; }
        public bool Released { get; private set; }
        public void SetPercent(float percent) => LastSet = percent;
        public void ReleaseToBios() => Released = true;
    }

    [Fact]
    public void Detects_pump_on_cpu_header_and_radiator_on_pump_header_like_this_pc()
    {
        // Lecturas reales de la Fase 0.
        IFanOutput[] outputs =
        [
            new FakeOutput("CPU_FAN1", 2521, 54),
            new FakeOutput("PUMP_FAN1", 1102, 53),
            new FakeOutput("SYS_FAN1", 0, 50),
            new FakeOutput("SYS_FAN4", 1171, 55),
            new FakeOutput("GPU 1", 0, 0, gpu: true),
        ];

        var roles = FanRoleDetector.Detect(outputs);

        Assert.Equal(FanRole.Pump, roles["CPU_FAN1"].Role);
        Assert.Equal(FanRole.Radiator, roles["PUMP_FAN1"].Role);
        Assert.Equal(FanRole.Case, roles["SYS_FAN1"].Role);
        Assert.Equal(FanRole.Case, roles["SYS_FAN4"].Role);
        Assert.Equal(FanRole.Gpu, roles["GPU 1"].Role);
        Assert.Contains("2521", roles["CPU_FAN1"].Evidence);
    }

    [Fact]
    public void Standard_wiring_keeps_header_based_roles()
    {
        IFanOutput[] outputs =
        [
            new FakeOutput("CPU_FAN1", 1100, 50),
            new FakeOutput("PUMP_FAN1", 2700, 100),
        ];

        var roles = FanRoleDetector.Detect(outputs);

        Assert.Equal(FanRole.Radiator, roles["CPU_FAN1"].Role);
        Assert.Equal(FanRole.Pump, roles["PUMP_FAN1"].Role);
    }
}
