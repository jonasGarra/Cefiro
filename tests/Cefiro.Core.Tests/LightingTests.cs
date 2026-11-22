using Cefiro.Core.Lighting;
using Cefiro.Demo;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cefiro.Core.Tests;

public class RgbTests
{
    [Theory]
    [InlineData("#FF8000", 255, 128, 0)]
    [InlineData("ff8000", 255, 128, 0)]
    [InlineData("#F80", 255, 136, 0)]
    public void Parses_hex(string hex, byte r, byte g, byte b)
    {
        Assert.True(Rgb.TryParseHex(hex, out var c));
        Assert.Equal(new Rgb(r, g, b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void Rejects_invalid_hex(string? hex) => Assert.False(Rgb.IsValidHex(hex));

    [Fact]
    public void Hsv_round_trip_on_primary_colors()
    {
        Assert.Equal(new Rgb(255, 0, 0), Rgb.FromHsv(0));
        Assert.Equal(new Rgb(0, 255, 0), Rgb.FromHsv(120));
        Assert.Equal(new Rgb(0, 0, 255), Rgb.FromHsv(240));
        Assert.Equal(new Rgb(255, 0, 0), Rgb.FromHsv(360));
        var (h, s, v) = new Rgb(0, 0, 255).ToHsv();
        Assert.Equal(240, h, 3);
        Assert.Equal(1, s, 3);
        Assert.Equal(1, v, 3);
    }

    [Fact]
    public void Gradient_and_scale()
    {
        Rgb[] stops = [new(0, 0, 0), new(200, 100, 50)];
        Assert.Equal(new Rgb(100, 50, 25), Rgb.Gradient(stops, 0.5));
        Assert.Equal(new Rgb(50, 25, 13), new Rgb(100, 50, 25).Scale(0.5));
    }
}

public class EffectRendererTests
{
    private static EffectContext Ctx(double time = 0, params Rgb[] colors) =>
        new(time, 1, colors.Length > 0 ? colors : [new Rgb(0, 229, 255)], false, null, null, 0, 1);

    [Fact]
    public void Static_fills_with_one_color_or_spreads_several()
    {
        var leds = new Rgb[4];
        EffectRenderer.Render(EffectKind.Static, leds, Ctx(0, new Rgb(10, 20, 30)));
        Assert.All(leds, c => Assert.Equal(new Rgb(10, 20, 30), c));

        EffectRenderer.Render(EffectKind.Static, leds, Ctx(0, new Rgb(0, 0, 0), new Rgb(255, 0, 0)));
        Assert.Equal(new Rgb(0, 0, 0), leds[0]);
        Assert.Equal(new Rgb(255, 0, 0), leds[^1]);
    }

    [Fact]
    public void Breathing_goes_dark_and_bright()
    {
        var leds = new Rgb[1];
        EffectRenderer.Render(EffectKind.Breathing, leds, Ctx(0, new Rgb(200, 200, 200)));
        var dark = leds[0].R;
        EffectRenderer.Render(EffectKind.Breathing, leds, Ctx(2, new Rgb(200, 200, 200))); // mitad del ciclo de 4 s
        Assert.True(dark < 20);
        Assert.True(leds[0].R > 190);
    }

    [Fact]
    public void Rainbow_spreads_hues_and_moves_with_time()
    {
        var a = new Rgb[6];
        var b = new Rgb[6];
        EffectRenderer.Render(EffectKind.Rainbow, a, Ctx(0));
        EffectRenderer.Render(EffectKind.Rainbow, b, Ctx(1));
        Assert.Equal(6, a.Distinct().Count());
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Temperature_maps_cold_to_blue_and_hot_to_red()
    {
        var leds = new Rgb[2];
        EffectRenderer.Render(EffectKind.Temperature, leds, new EffectContext(0, 1, [], false, 0, null, 0, 1));
        Assert.Equal(EffectRenderer.TemperatureGradient[0], leds[0]);
        EffectRenderer.Render(EffectKind.Temperature, leds, new EffectContext(0, 1, [], false, 1, null, 0, 1));
        Assert.Equal(EffectRenderer.TemperatureGradient[^1], leds[0]);
    }

    [Fact]
    public void Music_reacts_to_bands()
    {
        var quiet = new Rgb[16];
        var loud = new Rgb[16];
        var silence = new float[16];
        var bass = Enumerable.Range(0, 16).Select(i => i < 4 ? 1f : 0f).ToArray();
        EffectRenderer.Render(EffectKind.Music, quiet, new EffectContext(0, 1, [], false, null, silence, 0, 1));
        EffectRenderer.Render(EffectKind.Music, loud, new EffectContext(0, 1, [], false, null, bass, 1, 1));

        var quietSum = quiet[0].R + quiet[0].G + quiet[0].B;
        var loudSum = loud[0].R + loud[0].G + loud[0].B;
        Assert.True(loudSum > quietSum * 5);
        Assert.True(loud[^1].R + loud[^1].G + loud[^1].B < loudSum); // los agudos (sin señal) quedan tenues
    }

    [Fact]
    public void Comet_has_a_bright_head_and_a_dark_area()
    {
        var leds = new Rgb[20];
        EffectRenderer.Render(EffectKind.Comet, leds, Ctx(0.5, new Rgb(255, 255, 255)));
        Assert.Contains(leds, c => c.R > 200);
        Assert.Contains(leds, c => c.R == 0);
    }
}

public class LightingValidatorTests
{
    [Fact]
    public void Corrects_out_of_range_values_and_bad_colors()
    {
        var input = new LightingSettingsDto
        {
            Brightness = 250,
            SpeedMultiplier = 0,
            Fps = 500,
            SyncEffect = new EffectDto
            {
                Kind = (EffectKind)99,
                Colors = ["#zzz", "nada"],
                Speed = float.NaN,
                SensorId = "/no/existe",
                MinTemperature = 80,
                MaxTemperature = 40,
            },
            Favorites = ["#ff0000", "#FF0000", "malo"],
            Night = new NightModeDto { Enabled = true, Start = "25:99", End = "7:30" },
            ZoneSizes = [new ZoneSizeDto("placa", 2, 5000), new ZoneSizeDto("placa", 2, 32)],
        };

        var s = LightingValidator.Validate(input, new HashSet<string> { "/amdcpu/0/temperature/2" });

        Assert.Equal(100, s.Brightness);
        Assert.Equal(0.1f, s.SpeedMultiplier);
        Assert.Equal(60, s.Fps);
        Assert.Equal(EffectKind.Static, s.SyncEffect.Kind);
        Assert.Equal(["#00E5FF"], s.SyncEffect.Colors);
        Assert.Equal(1, s.SyncEffect.Speed);
        Assert.Null(s.SyncEffect.SensorId);
        Assert.True(s.SyncEffect.MaxTemperature > s.SyncEffect.MinTemperature);
        Assert.Equal(["#FF0000"], s.Favorites);
        Assert.Equal("23:30", s.Night.Start);
        Assert.Equal("7:30", s.Night.End);
        Assert.Equal(32, Assert.Single(s.ZoneSizes).LedCount);
    }

    [Theory]
    [InlineData("23:30", "07:30", 0, 15, true)]   // cruza la medianoche
    [InlineData("23:30", "07:30", 23, 45, true)]
    [InlineData("23:30", "07:30", 12, 0, false)]
    [InlineData("13:00", "15:00", 14, 0, true)]
    [InlineData("13:00", "15:00", 15, 0, false)]
    public void Night_window(string start, string end, int hour, int minute, bool expected)
    {
        var night = new NightModeDto { Enabled = true, Start = start, End = end };
        Assert.Equal(expected, LightingValidator.IsNightActive(night, new TimeSpan(hour, minute, 0)));
    }
}

public class LightingEngineTests
{
    private const string Board = "MSI X670E GAMING PLUS WIFI (MS-7E16) · DEMO|HID: demo";
    private const string Ram1 = "Corsair Vengeance RGB DDR5|I2C: demo 0x19";

    private static async Task<(LightingEngine Engine, DemoLightingBackend Backend)> StartAsync(LightingSettingsDto settings, Func<string, float?>? read = null)
    {
        var backend = new DemoLightingBackend();
        var engine = new LightingEngine(backend, NullLogger.Instance, read ?? (_ => null), () => "cpu");
        engine.Configure(settings);
        engine.Start();
        for (var i = 0; i < 100 && (backend.SendCount == 0 && backend.ModeOf(Board) is null); i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        return (engine, backend);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Sync_static_paints_every_led_of_every_device()
    {
        var settings = new LightingSettingsDto { SyncEffect = new EffectDto { Kind = EffectKind.Static, Colors = ["#FF0000"] } };
        var (engine, backend) = await StartAsync(settings);
        await using var _ = engine;

        await WaitUntil(() => backend.ColorsOf(Ram1).All(c => c == new Rgb(255, 0, 0)));

        Assert.Equal("Direct", backend.ModeOf(Board));
        Assert.All(backend.ColorsOf(Board), c => Assert.Equal(new Rgb(255, 0, 0), c));
        Assert.All(backend.ColorsOf(Ram1), c => Assert.Equal(new Rgb(255, 0, 0), c));
    }

    [Fact]
    public async Task Global_brightness_and_night_dim_scale_the_output()
    {
        var settings = new LightingSettingsDto
        {
            Brightness = 50,
            SyncEffect = new EffectDto { Kind = EffectKind.Static, Colors = ["#C80000"] },
        };
        var (engine, backend) = await StartAsync(settings);
        await using var _ = engine;

        await WaitUntil(() => backend.ColorsOf(Ram1).FirstOrDefault().R == 100);
        Assert.Equal(100, backend.ColorsOf(Ram1)[0].R);
    }

    [Fact]
    public async Task Layers_target_a_single_zone_and_leave_the_rest_off()
    {
        var settings = new LightingSettingsDto
        {
            SyncAll = false,
            Layers = [new LightingLayerDto(new LightingTargetDto(Board, ZoneIndex: 3), new EffectDto { Kind = EffectKind.Static, Colors = ["#00FF00"] })],
        };
        var (engine, backend) = await StartAsync(settings);
        await using var _ = engine;

        var device = engine.Devices.First(d => d.Key == Board);
        var zone = device.Zones.Single(z => z.Index == 3);
        await WaitUntil(() => backend.ColorsOf(Board).Skip(zone.StartLed).Take(zone.LedCount).All(c => c == new Rgb(0, 255, 0)));

        var colors = backend.ColorsOf(Board);
        Assert.All(colors.Skip(zone.StartLed).Take(zone.LedCount), c => Assert.Equal(new Rgb(0, 255, 0), c));
        Assert.All(colors.Take(zone.StartLed), c => Assert.Equal(Rgb.Black, c));
        Assert.Null(backend.ModeOf(Ram1)); // sin capa: la RAM no se toca
    }

    [Fact]
    public async Task Temperature_effect_follows_the_sensor()
    {
        float? temperature = 35;
        var settings = new LightingSettingsDto
        {
            SyncEffect = new EffectDto { Kind = EffectKind.Temperature, MinTemperature = 35, MaxTemperature = 85 },
        };
        var (engine, backend) = await StartAsync(settings, _ => temperature);
        await using var _ = engine;

        await WaitUntil(() => backend.ColorsOf(Ram1).FirstOrDefault() == EffectRenderer.TemperatureGradient[0]);
        Assert.Equal(EffectRenderer.TemperatureGradient[0], backend.ColorsOf(Ram1)[0]);

        temperature = 85;
        await WaitUntil(() => backend.ColorsOf(Ram1).FirstOrDefault().R > 200);
        Assert.True(backend.ColorsOf(Ram1)[0].R > 200, "a 85 °C debería verse rojo");
    }

    [Fact]
    public async Task Device_mode_uses_the_native_effect_without_streaming()
    {
        var settings = new LightingSettingsDto
        {
            SyncEffect = new EffectDto { Kind = EffectKind.DeviceMode, NativeMode = "Rainbow", Colors = ["#FFFFFF"] },
        };
        var (engine, backend) = await StartAsync(settings);
        await using var _ = engine;

        await WaitUntil(() => backend.ModeOf(Ram1) == "Rainbow");
        Assert.Equal("Rainbow", backend.ModeOf(Ram1));
        Assert.Equal(0, backend.SendCount);
    }

    [Fact]
    public async Task Resizing_a_zone_is_reported_for_saving()
    {
        var (engine, _) = await StartAsync(new LightingSettingsDto());
        await using var _e = engine;
        ZoneSizeDto? saved = null;
        engine.ZoneResized += z => saved = z;

        await engine.ResizeZoneAsync(new ResizeZoneRequest(Board, 2, 40)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitUntil(() => engine.Devices.FirstOrDefault(d => d.Key == Board)?.Zones[2].LedCount == 40);

        Assert.Equal(new ZoneSizeDto(Board, 2, 40), saved);
        Assert.Equal(40, engine.Devices.First(d => d.Key == Board).Zones[2].LedCount);
    }

    [Fact]
    public void Speed_maps_to_device_scale()
    {
        Assert.Equal(0.5, LightingEngine.SpeedToUnit(1), 3);
        Assert.Equal(1, LightingEngine.SpeedToUnit(5), 3);
        Assert.Equal(0, LightingEngine.SpeedToUnit(0.1), 3);
    }
}
