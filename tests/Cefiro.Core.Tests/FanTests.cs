using Cefiro.Core.Fans;
using Cefiro.Core.Hardware;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cefiro.Core.Tests;

/// <summary>Salida de ventilador simulada: registra lo que se le pide y puede fallar a propósito.</summary>
internal sealed class SimFan(string id, bool gpu = false, float hwMin = 0) : IFanOutput
{
    public string Id { get; } = id;
    public string Header => Id;
    public string Device => "sim";
    public string DefaultName => Id;
    public bool IsGpu { get; } = gpu;
    public string? RpmSensorId { get; set; } = id + "/rpm";
    public string ControlSensorId => Id;
    public float HardwareMinimumPercent { get; } = hwMin;
    public float HardwareMaximumPercent => 100;
    public float? Rpm { get; set; } = 1000;
    public float? OutputPercent { get; set; } = 50;
    public bool IsSoftwareControlled { get; private set; }
    public List<float> History { get; } = [];
    public int Releases { get; private set; }
    public bool ThrowOnSet { get; set; }

    /// <summary>Si se define, las RPM siguen a la señal (para la calibración).</summary>
    public Func<float, float>? RpmFromPercent { get; set; }

    public void SetPercent(float percent)
    {
        if (ThrowOnSet)
            throw new IOException("fallo simulado del bus");
        History.Add(percent);
        OutputPercent = percent;
        IsSoftwareControlled = true;
        if (RpmFromPercent is not null)
            Rpm = RpmFromPercent(percent);
    }

    public void ReleaseToBios()
    {
        Releases++;
        IsSoftwareControlled = false;
    }
}

public class FanCurveTests
{
    private static readonly CurvePointDto[] Curve = [new(30, 20), new(50, 40), new(70, 80), new(85, 100)];

    [Theory]
    [InlineData(10, 20)]   // por debajo del primer punto
    [InlineData(30, 20)]
    [InlineData(40, 30)]   // mitad entre 30→20 y 50→40
    [InlineData(60, 60)]
    [InlineData(77.5, 90)]
    [InlineData(85, 100)]
    [InlineData(110, 100)] // por encima del último
    public void Evaluate_interpolates_linearly(float temperature, float expected) =>
        Assert.Equal(expected, FanCurve.Evaluate(Curve, temperature), 3);

    [Fact]
    public void Evaluate_is_safe_without_data() =>
        Assert.Equal(100, FanCurve.Evaluate([], 40));

    [Fact]
    public void Evaluate_nan_temperature_means_full_speed() =>
        Assert.Equal(100, FanCurve.Evaluate(Curve, float.NaN));

    [Fact]
    public void Normalize_sorts_dedupes_clamps_and_forces_monotonic_curve()
    {
        CurvePointDto[] messy = [new(70, 50), new(30, 60), new(50, 200), new(50, 40), new(-20, 10), new(float.NaN, 5)];

        var curve = FanCurve.Normalize(messy, Curve);

        Assert.Equal([0f, 30f, 50f, 70f], curve.Select(p => p.Temperature));
        // 10 → 60 → 40(subido a 60) → 50(subido a 60): nunca baja al subir la temperatura.
        Assert.Equal([10f, 60f, 60f, 60f], curve.Select(p => p.Percent));
    }

    [Fact]
    public void Normalize_falls_back_when_not_enough_points()
    {
        Assert.Same(Curve, FanCurve.Normalize([new(40, 50)], Curve));
        Assert.Same(Curve, FanCurve.Normalize(null, Curve));
    }
}

public class FanChannelControllerTests
{
    private static FanChannelSettingsDto Settings(
        FanMode mode = FanMode.Curve, float hysteresis = 2, float rampUp = 100, float rampDown = 100,
        float minimum = 20, float start = 35, bool allowStop = false, float fixedPercent = 50) => new()
        {
            ChannelId = "c",
            Name = "c",
            Role = FanRole.Case,
            Mode = mode,
            FixedPercent = fixedPercent,
            Curve = [new CurvePointDto(30, 20), new CurvePointDto(70, 100)], // 2 % por °C
            Source = new TemperatureSourceDto(TemperatureAggregation.Single, ["t"]),
            HysteresisCelsius = hysteresis,
            RampUpPercentPerSecond = rampUp,
            RampDownPercentPerSecond = rampDown,
            MinimumPercent = minimum,
            StartPercent = start,
            AllowStop = allowStop,
        };

    [Fact]
    public void Hysteresis_follows_rises_immediately_but_needs_a_real_drop()
    {
        var c = new FanChannelController();
        var s = Settings(hysteresis: 2);

        Assert.Equal(60, c.Compute(s, 50, 60, 1).Target);  // 50 °C → 60 %
        Assert.Equal(64, c.Compute(s, 52, 60, 1).Target);  // sube: reacciona
        Assert.Equal(64, c.Compute(s, 51, 60, 1).Target);  // baja 1 °C: se ignora
        Assert.Equal(64, c.Compute(s, 50.5f, 60, 1).Target); // baja 1,5 °C: se ignora
        Assert.Equal(60, c.Compute(s, 50, 60, 1).Target);  // baja 2 °C: ahora sí
    }

    [Fact]
    public void Ramp_limits_how_fast_the_output_changes()
    {
        var c = new FanChannelController();
        var s = Settings(rampUp: 10, rampDown: 3);

        c.Compute(s, 30, 20, 1);                       // arranca en 20 %
        Assert.Equal(30, c.Compute(s, 70, 20, 1).Command); // quiere 100, sube 10 %/s
        Assert.Equal(40, c.Compute(s, 70, 30, 1).Command);
        Assert.Equal(60, c.Compute(s, 70, 40, 2).Command); // 2 s → 20 %

        // Bajada más lenta: 3 %/s.
        Assert.Equal(57, c.Compute(s, 30, 60, 1).Command);
    }

    [Fact]
    public void Minimum_is_enforced_unless_stopping_is_allowed()
    {
        var below = Settings(minimum: 30);
        Assert.Equal(30, new FanChannelController().Compute(below, 30, 30, 1).Target); // curva pide 20 %

        var stoppable = Settings(minimum: 30, allowStop: true);
        var d = new FanChannelController().Compute(stoppable, 30, 30, 1);
        Assert.Equal(0, d.Target);
        Assert.Equal(0, d.Command);
    }

    [Fact]
    public void Stopped_fan_gets_a_start_kick()
    {
        var c = new FanChannelController();
        var s = Settings(minimum: 25, start: 40, allowStop: true);

        Assert.Equal(0, c.Compute(s, 25, 0, 1).Command);    // la curva pide 20 % < mínimo 25 % → parado
        var kick = c.Compute(s, 35, 0, 1);                   // la curva pide 30 %
        Assert.Equal(40, kick.Command);                      // empujón al 40 %
        Assert.Equal("Arrancando", kick.Note);
        Assert.Equal(40, c.Compute(s, 35, 40, 1).Command);   // el empujón dura unos 2 s
        Assert.Equal(40, c.Compute(s, 35, 40, 1).Command);
        Assert.Equal(30, c.Compute(s, 35, 40, 1).Command);   // después, al objetivo
    }

    [Fact]
    public void Fixed_mode_ignores_temperature()
    {
        var s = Settings(mode: FanMode.Fixed, fixedPercent: 55);
        Assert.Equal(55, new FanChannelController().Compute(s, null, 55, 1).Command);
    }
}

public class SafetyGuardTests
{
    [Theory]
    [InlineData(0, 60)]
    [InlineData(20, 60)]
    [InlineData(59.9, 60)]
    [InlineData(80, 80)]
    [InlineData(150, 100)]
    public void Pump_never_goes_below_60_or_stops(float requested, float expected) =>
        Assert.Equal(expected, SafetyGuard.Validate(FanRole.Pump, requested, 0, 100, out _));

    [Theory]
    [InlineData(0, 30)]
    [InlineData(10, 30)]
    [InlineData(45, 45)]
    public void Radiator_never_goes_below_30_or_stops(float requested, float expected) =>
        Assert.Equal(expected, SafetyGuard.Validate(FanRole.Radiator, requested, 0, 100, out _));

    [Fact]
    public void Case_fans_can_stop_when_asked()
    {
        Assert.Equal(0, SafetyGuard.Validate(FanRole.Case, 0, 0, 100, out _));
        Assert.Equal(15, SafetyGuard.Validate(FanRole.Case, 15, 0, 100, out _));
    }

    [Fact]
    public void Gpu_respects_its_hardware_minimum()
    {
        Assert.Equal(30, SafetyGuard.Validate(FanRole.Gpu, 0, 30, 100, out _));
        Assert.Equal(30, SafetyGuard.Validate(FanRole.Gpu, 10, 30, 100, out _));
        Assert.Equal(70, SafetyGuard.Validate(FanRole.Gpu, 70, 30, 100, out _));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Invalid_values_mean_release_to_bios(float requested)
    {
        Assert.Null(SafetyGuard.Validate(FanRole.Case, requested, 0, 100, out var note));
        Assert.NotNull(note);
    }
}

public class EmergencyMonitorTests
{
    private static readonly SafetySettingsDto Safety = new()
    {
        CpuCriticalCelsius = 90,
        GpuCriticalCelsius = 85,
        RecoveryMarginCelsius = 8,
        RecoveryHoldSeconds = 15,
    };

    [Fact]
    public void Enters_on_critical_cpu_and_leaves_only_after_holding_below_margin()
    {
        var m = new EmergencyMonitor();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);

        Assert.False(m.Update(85, 60, Safety, t0));
        Assert.True(m.Update(90, 60, Safety, t0.AddSeconds(1)));
        Assert.True(m.Active);
        Assert.Contains("CPU", m.Reason);

        // 85 °C: ya no es crítico, pero no está 8 °C por debajo → sigue.
        Assert.False(m.Update(85, 60, Safety, t0.AddSeconds(30)));
        Assert.True(m.Active);

        // Por debajo de 82 °C, pero hay que aguantar 15 s.
        m.Update(80, 60, Safety, t0.AddSeconds(40));
        m.Update(80, 60, Safety, t0.AddSeconds(50));
        Assert.True(m.Active);

        // Un pico corta la espera.
        m.Update(84, 60, Safety, t0.AddSeconds(52));
        m.Update(80, 60, Safety, t0.AddSeconds(53));
        Assert.False(m.Update(80, 60, Safety, t0.AddSeconds(60)));
        Assert.True(m.Active);

        Assert.True(m.Update(80, 60, Safety, t0.AddSeconds(69)));
        Assert.False(m.Active);
    }

    [Fact]
    public void Gpu_can_trigger_and_lost_cpu_sensor_keeps_emergency()
    {
        var m = new EmergencyMonitor();
        var t0 = DateTime.UtcNow;

        Assert.True(m.Update(50, 86, Safety, t0));
        Assert.Contains("GPU", m.Reason);

        // Sin lectura de la CPU no se puede confirmar que todo esté bien: no se sale.
        for (var i = 1; i < 60; i++)
            m.Update(null, 40, Safety, t0.AddSeconds(i));
        Assert.True(m.Active);
    }
}

public class FanEngineTests
{
    private static readonly KeySensorsDto Keys = new() { CpuTemperature = "cpu", GpuTemperature = "gpu" };

    private static FanChannelSettingsDto Channel(string id, FanRole role, FanMode mode, float fixedPercent = 50) => new()
    {
        ChannelId = id,
        Name = id,
        Role = role,
        Mode = mode,
        FixedPercent = fixedPercent,
        Curve = [new CurvePointDto(30, 30), new CurvePointDto(80, 100)],
        Source = new TemperatureSourceDto(TemperatureAggregation.Single, ["cpu"]),
        HysteresisCelsius = 0,
        RampUpPercentPerSecond = 100,
        RampDownPercentPerSecond = 100,
        MinimumPercent = 0,
        StartPercent = 0,
    };

    private static (FanEngine Engine, Dictionary<string, float?> Temps) Create(params FanChannelSettingsDto[] channels)
    {
        var engine = new FanEngine(NullLogger.Instance);
        engine.Configure(new FanSettingsDto(channels, [], new SafetySettingsDto { SensorTimeoutSeconds = 3 }));
        return (engine, new Dictionary<string, float?> { ["cpu"] = 50, ["gpu"] = 40 });
    }

    private static FanFrameDto Tick(FanEngine engine, IReadOnlyList<IFanOutput> outputs, Dictionary<string, float?> temps, DateTime now,
        IReadOnlyDictionary<string, string>? pumps = null) =>
        engine.Tick(outputs, id => temps.GetValueOrDefault(id), Keys, pumps ?? new Dictionary<string, string>(), now, 0);

    [Fact]
    public void Bios_mode_hands_control_back()
    {
        var fan = new SimFan("sys");
        fan.SetPercent(40);
        var (engine, temps) = Create(Channel("sys", FanRole.Case, FanMode.Bios));

        Tick(engine, [fan], temps, DateTime.UtcNow);

        Assert.False(fan.IsSoftwareControlled);
        Assert.Equal(1, fan.Releases);
    }

    [Fact]
    public void Pump_is_clamped_even_if_settings_ask_for_less()
    {
        var pump = new SimFan("pump");
        var (engine, temps) = Create(Channel("pump", FanRole.Pump, FanMode.Fixed, fixedPercent: 20));

        var frame = Tick(engine, [pump], temps, DateTime.UtcNow);

        Assert.Equal(60, pump.History[^1]);
        Assert.Contains("Bomba protegida", frame.Channels[0].Note);
    }

    [Fact]
    public void A_channel_that_spins_like_a_pump_is_protected_as_a_pump()
    {
        var fan = new SimFan("cpu_fan1");
        var (engine, temps) = Create(Channel("cpu_fan1", FanRole.Case, FanMode.Fixed, fixedPercent: 10));
        var pumps = new Dictionary<string, string> { ["cpu_fan1"] = "2521 RPM con 54 %" };

        Tick(engine, [fan], temps, DateTime.UtcNow, pumps);

        Assert.Equal(60, fan.History[^1]);
    }

    [Fact]
    public void Curve_follows_the_source_temperature()
    {
        var fan = new SimFan("sys");
        var (engine, temps) = Create(Channel("sys", FanRole.Case, FanMode.Curve));

        temps["cpu"] = 55; // 30→30 %, 80→100 %: 55 °C = 65 %
        Tick(engine, [fan], temps, DateTime.UtcNow);
        Assert.Equal(65, fan.History[^1], 1);
    }

    [Fact]
    public void Emergency_sets_every_fan_to_100_including_the_gpu()
    {
        var sys = new SimFan("sys");
        var gpu = new SimFan("gpu1", gpu: true, hwMin: 30);
        var (engine, temps) = Create(
            Channel("sys", FanRole.Case, FanMode.Fixed, 30),
            Channel("gpu1", FanRole.Gpu, FanMode.Bios));
        var alerts = new List<AlertKind>();
        engine.Alert += (k, _, _) => alerts.Add(k);

        temps["cpu"] = 95;
        var frame = Tick(engine, [sys, gpu], temps, DateTime.UtcNow);

        Assert.True(frame.EmergencyActive);
        Assert.Equal(100, sys.History[^1]);
        Assert.Equal(100, gpu.History[^1]);
        Assert.Contains(AlertKind.Emergency, alerts);
    }

    [Fact]
    public void Lost_curve_sensor_holds_briefly_then_returns_to_bios()
    {
        var fan = new SimFan("sys");
        var settings = Channel("sys", FanRole.Case, FanMode.Curve) with
        {
            Source = new TemperatureSourceDto(TemperatureAggregation.Single, ["board"]),
        };
        var (engine, temps) = Create(settings);
        temps["board"] = 40;
        var t0 = DateTime.UtcNow;

        Tick(engine, [fan], temps, t0);
        Assert.True(fan.IsSoftwareControlled);

        temps["board"] = null;
        var waiting = Tick(engine, [fan], temps, t0.AddSeconds(2));
        Assert.True(fan.IsSoftwareControlled);
        Assert.Equal("Esperando al sensor…", waiting.Channels[0].Note);

        var lost = Tick(engine, [fan], temps, t0.AddSeconds(5));
        Assert.False(fan.IsSoftwareControlled);
        Assert.Equal(FanMode.Bios, lost.Channels[0].EffectiveMode);
    }

    [Fact]
    public void Losing_the_cpu_temperature_returns_everything_to_bios()
    {
        var fan = new SimFan("sys");
        var (engine, temps) = Create(Channel("sys", FanRole.Case, FanMode.Fixed, 40));
        var t0 = DateTime.UtcNow;

        Tick(engine, [fan], temps, t0);
        Assert.True(fan.IsSoftwareControlled);

        temps["cpu"] = null;
        Tick(engine, [fan], temps, t0.AddSeconds(1));
        Assert.True(fan.IsSoftwareControlled); // todavía dentro del margen

        var frame = Tick(engine, [fan], temps, t0.AddSeconds(5));
        Assert.False(fan.IsSoftwareControlled);
        Assert.Contains("CPU", frame.Channels[0].Note);
    }

    [Fact]
    public void Hardware_error_releases_the_channel_to_bios()
    {
        var fan = new SimFan("sys") { ThrowOnSet = true };
        var (engine, temps) = Create(Channel("sys", FanRole.Case, FanMode.Fixed, 40));

        var frame = Tick(engine, [fan], temps, DateTime.UtcNow);

        Assert.Equal(1, fan.Releases);
        Assert.Equal(FanMode.Bios, frame.Channels[0].EffectiveMode);
    }

    [Fact]
    public void Stopped_pump_raises_a_critical_alert()
    {
        var pump = new SimFan("pump") { Rpm = 2500 };
        var (engine, temps) = Create(Channel("pump", FanRole.Pump, FanMode.Fixed, 100));
        var alerts = new List<string>();
        engine.Alert += (_, title, _) => alerts.Add(title);
        var t0 = DateTime.UtcNow;

        Tick(engine, [pump], temps, t0);        // ha girado: ya cuenta
        pump.Rpm = 0;
        for (var i = 1; i <= 10; i++)
            Tick(engine, [pump], temps, t0.AddSeconds(i));

        Assert.Contains("¡La bomba no gira!", alerts);
    }

    [Fact]
    public void Empty_headers_never_raise_stall_alerts()
    {
        var empty = new SimFan("sys1") { Rpm = 0 };
        var (engine, temps) = Create(Channel("sys1", FanRole.Case, FanMode.Fixed, 60));
        var alerts = 0;
        engine.Alert += (_, _, _) => alerts++;

        for (var i = 0; i < 20; i++)
            Tick(engine, [empty], temps, DateTime.UtcNow.AddSeconds(i));

        Assert.Equal(0, alerts);
    }
}

public class FanSettingsValidatorTests
{
    private static readonly KeySensorsDto Keys = new() { CpuTemperature = "cpu", GpuTemperature = "gpu" };

    private static readonly SensorDescriptor[] Sensors =
    [
        new("cpu", "CPU", "Core (Tctl/Tdie)", "cpu", DeviceCategory.Cpu, SensorKind.Temperature, "°C", "Cpu"),
        new("gpu", "GPU", "GPU Core", "gpu", DeviceCategory.Gpu, SensorKind.Temperature, "°C", "GpuNvidia"),
        new("load", "Uso", "CPU Total", "cpu", DeviceCategory.Cpu, SensorKind.Load, "%", "Cpu"),
    ];

    private static readonly FanChannelDto[] Channels =
    [
        new("pump", "CPU_FAN1", "sio", "CPU_FAN1", FanRole.Pump, "", "pump/rpm", "pump", 0, 100),
        new("rad", "PUMP_FAN1", "sio", "PUMP_FAN1", FanRole.Radiator, "", "rad/rpm", "rad", 0, 100),
        new("sys", "SYS_FAN4", "sio", "SYS_FAN4", FanRole.Case, "", "sys/rpm", "sys", 0, 100),
        new("gpu1", "GPU 1", "gpu", "GPU", FanRole.Gpu, "", "gpu1/rpm", "gpu1", 30, 100),
    ];

    [Fact]
    public void First_run_defaults_leave_everything_to_the_bios()
    {
        var s = FanSettingsValidator.Validate(null, Channels, Sensors, Keys);

        Assert.Equal(4, s.Channels.Count);
        Assert.All(s.Channels, c => Assert.Equal(FanMode.Bios, c.Mode));
        Assert.Equal(60, s.Channels.Single(c => c.ChannelId == "pump").MinimumPercent);
        Assert.Equal(30, s.Channels.Single(c => c.ChannelId == "rad").MinimumPercent);
        Assert.Equal(TemperatureAggregation.Maximum, s.Channels.Single(c => c.ChannelId == "sys").Source.Aggregation);
    }

    [Fact]
    public void Unsafe_values_are_corrected()
    {
        var input = FanSettingsValidator.Validate(null, Channels, Sensors, Keys);
        var tampered = input with
        {
            Channels = input.Channels.Select(c => c with
            {
                Mode = FanMode.Fixed,
                FixedPercent = 0,
                MinimumPercent = -50,
                AllowStop = true,
                HysteresisCelsius = float.NaN,
                Source = new TemperatureSourceDto(TemperatureAggregation.Single, ["load", "no-existe"]),
            }).Append(new FanChannelSettingsDto { ChannelId = "fantasma", Name = "x" }).ToList(),
            Safety = new SafetySettingsDto { CpuCriticalCelsius = 300, GpuCriticalCelsius = -5 },
        };

        var s = FanSettingsValidator.Validate(tampered, Channels, Sensors, Keys);

        Assert.DoesNotContain(s.Channels, c => c.ChannelId == "fantasma");
        var pump = s.Channels.Single(c => c.ChannelId == "pump");
        Assert.Equal(60, pump.FixedPercent);
        Assert.False(pump.AllowStop);
        Assert.Equal(30, s.Channels.Single(c => c.ChannelId == "rad").FixedPercent);
        Assert.Equal(30, s.Channels.Single(c => c.ChannelId == "gpu1").FixedPercent);

        var sys = s.Channels.Single(c => c.ChannelId == "sys");
        Assert.True(sys.AllowStop);          // la caja sí puede pararse
        Assert.Equal(0, sys.FixedPercent);
        Assert.All(s.Channels, c => Assert.True(float.IsFinite(c.HysteresisCelsius)));
        Assert.All(s.Channels, c => Assert.DoesNotContain("load", c.Source.SensorIds)); // no es una temperatura

        Assert.Equal(100, s.Safety.CpuCriticalCelsius);
        Assert.Equal(60, s.Safety.GpuCriticalCelsius);
    }

    [Fact]
    public void Pump_signature_overrides_the_role_chosen_by_the_user()
    {
        var input = FanSettingsValidator.Validate(null, Channels, Sensors, Keys);
        var wrong = input with
        {
            Channels = input.Channels.Select(c => c.ChannelId == "rad" ? c with { Role = FanRole.Case, MinimumPercent = 0 } : c).ToList(),
        };

        var s = FanSettingsValidator.Validate(wrong, Channels, Sensors, Keys, new Dictionary<string, string> { ["rad"] = "gira como bomba" });

        var rad = s.Channels.Single(c => c.ChannelId == "rad");
        Assert.Equal(FanRole.Pump, rad.Role);
        Assert.Equal(60, rad.MinimumPercent);
    }

    [Fact]
    public void Presets_keep_the_gpu_on_the_driver_except_performance_and_maximum()
    {
        var gpu = FanSettingsValidator.Validate(null, Channels, Sensors, Keys).Channels.Single(c => c.ChannelId == "gpu1");

        Assert.Equal(FanMode.Bios, FanDefaults.ApplyPreset(gpu, FanPreset.Silent).Mode);
        Assert.Equal(FanMode.Curve, FanDefaults.ApplyPreset(gpu, FanPreset.Performance).Mode);
        var max = FanDefaults.ApplyPreset(gpu, FanPreset.Maximum);
        Assert.Equal(FanMode.Fixed, max.Mode);
        Assert.Equal(100, max.FixedPercent);
    }
}

public class FanCalibratorTests
{
    [Fact]
    public void Calibrates_a_case_fan_finding_stop_and_start_points()
    {
        // RS120: se para por debajo del 15 % y necesita un 25 % para arrancar.
        var spinning = true;
        var fan = new SimFan("sys")
        {
            RpmFromPercent = p =>
            {
                if (spinning && p < 15) spinning = false;
                if (!spinning && p >= 25) spinning = true;
                return spinning ? 420 + (2100 - 420) * Math.Max(0, p - 15) / 85f : 0;
            },
        };

        var engine = new FanEngine(NullLogger.Instance);
        var calibrator = new FanCalibrator(NullLogger.Instance);
        calibrator.Start([("sys", FanRole.Case)]);

        var t = DateTime.UtcNow;
        for (var i = 0; i < 400 && calibrator.Running; i++)
        {
            t = t.AddSeconds(1);
            calibrator.Tick([fan], engine, 50, 40, t);
        }

        Assert.False(calibrator.Running);
        var r = Assert.Single(calibrator.Results);
        Assert.Equal("Completada", r.Status);
        Assert.Equal(10, r.StopPercent);   // primer paso en el que ya no gira
        Assert.Equal(25, r.StartPercent);
        Assert.Equal(2100, r.MaxRpm!.Value, 0);
        Assert.Empty(engine.Excluded);     // devuelta al motor
    }

    [Fact]
    public void Pump_calibration_never_goes_below_60_percent()
    {
        var pump = new SimFan("pump") { RpmFromPercent = p => 1900 + p * 11.5f };
        var engine = new FanEngine(NullLogger.Instance);
        var calibrator = new FanCalibrator(NullLogger.Instance);
        calibrator.Start([("pump", FanRole.Pump)]);

        var t = DateTime.UtcNow;
        for (var i = 0; i < 400 && calibrator.Running; i++)
        {
            t = t.AddSeconds(1);
            calibrator.Tick([pump], engine, 50, 40, t);
        }

        Assert.All(pump.History, p => Assert.True(p >= 60, $"la bomba recibió {p} %"));
        Assert.Equal([100f, 90f, 80f, 70f, 60f], calibrator.Results[0].Points.Select(p => p.Percent));
    }

    [Fact]
    public void Calibration_aborts_when_the_cpu_gets_hot()
    {
        var fan = new SimFan("rad") { RpmFromPercent = p => p * 20 };
        var engine = new FanEngine(NullLogger.Instance);
        var calibrator = new FanCalibrator(NullLogger.Instance);
        calibrator.Start([("rad", FanRole.Radiator)]);

        var t = DateTime.UtcNow;
        calibrator.Tick([fan], engine, 50, 40, t);
        Assert.True(calibrator.Running);
        Assert.Contains("rad", engine.Excluded);

        calibrator.Tick([fan], engine, 88, 40, t.AddSeconds(1));
        Assert.False(calibrator.Running);
        Assert.Empty(engine.Excluded);
        Assert.StartsWith("Cancelada", calibrator.Results[0].Status);
    }
}
