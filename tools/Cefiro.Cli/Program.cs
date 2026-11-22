using System.Globalization;
using System.Text;
using Cefiro.Ipc;
using Cefiro.Ipc.Contracts;

// cefiro-cli: habla con el servicio por el mismo pipe que la interfaz.
//   cefiro-cli status
//   cefiro-cli catalog
//   cefiro-cli monitor [segundos]
//   cefiro-cli history <idSensor> [60s|10m|1h]
//   cefiro-cli fans
//   cefiro-cli fan <conector|id> bios|fixed <%>
//   cefiro-cli preset silent|balanced|performance|maximum
//   cefiro-cli calibrate <conector|id> [...]
// Opción común: --pipe <nombre>

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

var pipe = GetArg(args, "--pipe") ?? IpcConstants.PipeName;
var positional = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || args[i - 1] != "--pipe")).ToArray();
var command = positional.FirstOrDefault() ?? "status";

await using var connection = new PipeConnection(pipe);
var api = new CefiroApi(connection);
await api.StartAsync();

if (!await connection.WaitForConnectionAsync(TimeSpan.FromSeconds(10)))
{
    Console.Error.WriteLine($"No se pudo conectar con el servicio (pipe \"{pipe}\"). ¿Está arrancado?");
    return 2;
}

var hello = await api.HelloAsync("cefiro-cli");
Console.WriteLine($"Conectado a Céfiro {hello.ServiceVersion} en {hello.MachineName}{(hello.DemoMode ? " (MODO DEMO)" : "")}");

try
{
    switch (command)
    {
        case "status":
            PrintStatus(await api.GetStatusAsync());
            break;

        case "catalog":
            await PrintCatalogAsync(api);
            break;

        case "monitor":
            var seconds = positional.Length > 1 && int.TryParse(positional[1], out var s) ? s : 10;
            await MonitorAsync(api, seconds);
            break;

        case "history":
            if (positional.Length < 2)
                throw new ArgumentException("Falta el id del sensor.");
            var range = (positional.Length > 2 ? positional[2] : "60s") switch
            {
                "10m" => HistoryRange.Last10Minutes,
                "1h" => HistoryRange.LastHour,
                _ => HistoryRange.Last60Seconds,
            };
            var history = await api.GetHistoryAsync(new HistoryRequest([positional[1]], range));
            foreach (var series in history.Series)
            {
                var values = series.Values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
                Console.WriteLine($"{series.SensorId}: {values.Count} puntos cada {history.IntervalMilliseconds} ms; " +
                                  (values.Count > 0 ? $"mín {values.Min():0.0}, media {values.Average():0.0}, máx {values.Max():0.0}" : "sin datos"));
            }

            break;

        case "fans":
            await PrintFansAsync(api);
            break;

        case "watch-fans":
            await WatchFansAsync(api, positional.Length > 1 && int.TryParse(positional[1], out var ws) ? ws : 5);
            break;

        case "fan":
            await SetFanAsync(api, positional.Skip(1).ToArray());
            break;

        case "preset":
            var preset = Enum.Parse<FanPreset>(positional.ElementAtOrDefault(1) ?? "balanced", ignoreCase: true);
            await api.ApplyFanPresetAsync(new ApplyPresetRequest(preset, null));
            Console.WriteLine($"Modo {preset} aplicado.");
            await PrintFansAsync(api);
            break;

        case "calibrate":
            await CalibrateAsync(api, positional.Skip(1).ToArray());
            break;

        case "safety":
            // safety <cpuCritico> <gpuCritico>
            var current = await api.GetFanSettingsAsync();
            var safety = current.Safety with
            {
                CpuCriticalCelsius = float.Parse(positional[1], CultureInfo.InvariantCulture),
                GpuCriticalCelsius = float.Parse(positional[2], CultureInfo.InvariantCulture),
            };
            var appliedSafety = (await api.SetFanSettingsAsync(current with { Safety = safety })).Safety;
            Console.WriteLine($"Emergencia: CPU {appliedSafety.CpuCriticalCelsius} °C, GPU {appliedSafety.GpuCriticalCelsius} °C");
            break;

        case "lights":
            PrintLighting(await api.CallAsync<LightingStateDto>(Methods.GetLighting, null));
            break;

        case "light":
            // light <efecto> [#color ...] [--speed x] [--brightness y] [--native modo] [--sensor id]
            await SetSyncEffectAsync(api, positional.Skip(1).ToArray(), args);
            break;

        case "zone":
            // zone <texto del dispositivo> <índice de zona> <nº de LEDs>
            var lstate = await api.CallAsync<LightingStateDto>(Methods.GetLighting, null);
            var dev = FindDevice(lstate, positional[1]);
            var resized = await api.CallAsync<LightingStateDto>(Methods.ResizeZone,
                new ResizeZoneRequest(dev.Key, int.Parse(positional[2], CultureInfo.InvariantCulture), int.Parse(positional[3], CultureInfo.InvariantCulture)));
            PrintLighting(resized);
            break;

        case "rescan":
            await api.CallAsync(Methods.RescanLighting, null);
            Console.WriteLine("Pedido a OpenRGB que vuelva a detectar los dispositivos; en unos segundos se reenvían las luces.");
            break;

        case "identify":
            // identify <texto del dispositivo> [zona] [led]
            var istate = await api.CallAsync<LightingStateDto>(Methods.GetLighting, null);
            var idev = FindDevice(istate, positional[1]);
            int? izone = positional.Length > 2 ? int.Parse(positional[2], CultureInfo.InvariantCulture) : null;
            int? iled = positional.Length > 3 ? int.Parse(positional[3], CultureInfo.InvariantCulture) : null;
            await api.CallAsync(Methods.IdentifyLeds, new IdentifyRequest(idev.Key, izone, iled, 6000));
            Console.WriteLine($"Parpadeando {idev.Name}{(izone is null ? "" : $" · zona {izone}")}{(iled is null ? "" : $" · LED {iled}")} durante 6 s.");
            break;

        case "pattern":
            // pattern <texto del dispositivo> <zona> [tamaño de bloque]: bloques de colores para localizar ventiladores
            var pstate = await api.CallAsync<LightingStateDto>(Methods.GetLighting, null);
            var pdev = FindDevice(pstate, positional[1]);
            var pzone = pdev.Zones.Single(z => z.Index == int.Parse(positional[2], CultureInfo.InvariantCulture));
            var block = positional.Length > 3 ? int.Parse(positional[3], CultureInfo.InvariantCulture) : 8;
            string[] palette = ["#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#FFFFFF", "#00FFFF", "#FF8000"];
            var layers = Enumerable.Range(0, pzone.LedCount)
                .Select(i => new LightingLayerDto(
                    new LightingTargetDto(pdev.Key, null, pzone.StartLed + i),
                    new EffectDto { Kind = EffectKind.Static, Colors = [palette[(i / block) % palette.Length]] }))
                .ToList();
            var pattern = await api.CallAsync<LightingStateDto>(Methods.SetLighting, pstate.Settings with { SyncAll = false, Layers = layers, Enabled = true });
            Console.WriteLine($"{pdev.Name} · {pzone.Name}: {pzone.LedCount} LEDs en bloques de {block}: rojo, verde, azul, amarillo, magenta, blanco, cian, naranja.");
            break;

        case "audio-selftest":
            await AudioSelfTestAsync(api);
            break;

        case "music":
            // music [segundos]: escucha el audio del sistema y lo envía al servicio
            await StreamMusicAsync(api, positional.Length > 1 ? int.Parse(positional[1], CultureInfo.InvariantCulture) : 20);
            break;

        case "all-bios":
            var all = await api.GetFanSettingsAsync();
            await api.SetFanSettingsAsync(all with
            {
                Channels = all.Channels.Select(c => c with { Mode = FanMode.Bios }).ToList(),
                Safety = new SafetySettingsDto(),
            });
            Console.WriteLine("Todas las salidas devueltas a la BIOS y seguridad por defecto.");
            break;

        default:
            Console.Error.WriteLine($"Orden desconocida: {command}");
            return 1;
    }
}
catch (IpcRemoteException ex)
{
    Console.Error.WriteLine($"El servicio respondió con un error: {ex.Message}");
    return 3;
}

return 0;

static string? GetArg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void PrintStatus(StatusDto status)
{
    Console.WriteLine($"Hardware listo: {status.HardwareReady} | OpenRGB: {status.OpenRgbConnected} | Emergencia: {status.EmergencyActive} {status.EmergencyReason}");
    Console.WriteLine($"CPU del servicio: {status.ServiceCpuPercent:0.00} % | Memoria: {status.ServiceMemoryBytes / 1048576.0:0} MB | " +
                      $"Última lectura de sensores: {status.LastUpdateMilliseconds:0.0} ms | Activo: {status.UptimeSeconds} s");
    if (status.LastError is not null)
        Console.WriteLine($"Último error: {status.LastError}");
    foreach (var c in status.Conflicts)
        Console.WriteLine($"  ⚠ {c.Severity}: {c.Name} — {c.Detail}");
}

static async Task PrintCatalogAsync(CefiroApi api)
{
    var catalog = await api.GetCatalogAsync();
    var b = catalog.Backend;
    Console.WriteLine($"Backend: {b.Name} | admin: {b.IsAdministrator} | PawnIO: {b.PawnIoInstalled}");
    Console.WriteLine($"Placa: {b.Motherboard} | Chip: {b.SuperIoChip} | CPU: {b.Cpu} | GPU: {b.Gpu}");
    foreach (var w in b.Warnings)
        Console.WriteLine($"  ⚠ {w}");

    Console.WriteLine($"\nSensores ({catalog.Sensors.Count}):");
    foreach (var group in catalog.Sensors.GroupBy(s => s.Device))
    {
        Console.WriteLine($"  [{group.Key}]");
        foreach (var s in group)
            Console.WriteLine($"     {s.Kind,-12} {s.Name,-36} {s.Unit,-4} {s.Id}");
    }

    Console.WriteLine($"\nSalidas de ventilador ({catalog.FanChannels.Count}):");
    foreach (var f in catalog.FanChannels)
        Console.WriteLine($"  {f.Header,-10} → {f.DetectedRole,-9} (mín. hardware {f.HardwareMinimumPercent:0} %) · {f.RoleEvidence}");

    Console.WriteLine("\nSensores clave:");
    foreach (var p in typeof(KeySensorsDto).GetProperties())
        Console.WriteLine($"  {p.Name,-20} {p.GetValue(catalog.Keys) ?? "—"}");
}

static async Task MonitorAsync(CefiroApi api, int seconds)
{
    var catalog = await api.GetCatalogAsync();
    var index = catalog.Sensors.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
    var keys = catalog.Keys;
    var fanRpm = catalog.FanChannels.Where(f => f.RpmSensorId is not null).ToList();

    string V(float?[] values, string? id, string format, string unit) =>
        id is not null && index.TryGetValue(id, out var i) && values[i] is { } v ? v.ToString(format) + unit : "—";

    var frames = 0;
    var done = new TaskCompletionSource();
    api.SensorFrame += (_, frame) =>
    {
        if (frame.CatalogVersion != catalog.Version)
            return;

        frames++;
        var v = frame.Values;
        var fans = string.Join("  ", fanRpm.Select(f => $"{f.Header} {V(v, f.RpmSensorId, "0", "")}/{V(v, f.ControlSensorId, "0", "%")}"));
        Console.WriteLine($"{DateTimeOffset.FromUnixTimeMilliseconds(frame.Timestamp).LocalDateTime:HH:mm:ss}  " +
                          $"CPU {V(v, keys.CpuTemperature, "0.0", "°C")} {V(v, keys.CpuLoad, "0", "%")} {V(v, keys.CpuPower, "0", "W")}  " +
                          $"GPU {V(v, keys.GpuTemperature, "0.0", "°C")} {V(v, keys.GpuLoad, "0", "%")}  " +
                          $"RAM {V(v, keys.RamLoad, "0", "%")} {V(v, keys.RamTemperature, "0.0", "°C")}  " +
                          $"VRM {V(v, keys.VrmTemperature, "0", "°C")}  |  {fans}");
        if (frames >= seconds)
            done.TrySetResult();
    };

    await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(seconds + 5)));
    Console.WriteLine($"\nRecibidas {frames} tramas en {seconds} s.");
    PrintStatus(await api.GetStatusAsync());
}

static async Task PrintFansAsync(CefiroApi api)
{
    var catalog = await api.GetCatalogAsync();
    var settings = await api.GetFanSettingsAsync();
    foreach (var ch in settings.Channels)
    {
        var info = catalog.FanChannels.FirstOrDefault(f => f.Id == ch.ChannelId);
        var detail = ch.Mode switch
        {
            FanMode.Fixed => $"{ch.FixedPercent:0} %",
            FanMode.Curve => $"curva de {ch.Curve.Count} puntos sobre {string.Join(", ", ch.Source.SensorIds)} ({ch.Source.Aggregation})",
            _ => "BIOS",
        };
        Console.WriteLine($"  {info?.Header ?? ch.ChannelId,-10} \"{ch.Name}\" rol={ch.Role} modo={ch.Mode} {detail} mín={ch.MinimumPercent:0} %");
    }

    Console.WriteLine($"  Seguridad: emergencia CPU {settings.Safety.CpuCriticalCelsius} °C / GPU {settings.Safety.GpuCriticalCelsius} °C");
}

static void PrintLighting(LightingStateDto s)
{
    Console.WriteLine($"OpenRGB: {(s.Connected ? $"conectado a \"{s.ServerName}\" (protocolo {s.ProtocolVersion})" : "desconectado")}" +
                      (s.Error is null ? "" : $" · {s.Error}") + (s.NightActive ? " · MODO NOCHE" : ""));
    foreach (var d in s.Devices)
    {
        Console.WriteLine($"  [{d.Kind}] {d.Name} · {d.LedCount} LEDs · {d.Location}");
        foreach (var z in d.Zones)
            Console.WriteLine($"      zona {z.Index}: {z.Name,-14} {z.LedCount,3} LEDs (desde el LED {z.StartLed}){(z.Resizable ? $"  [ajustable {z.MinLeds}-{z.MaxLeds}]" : "")}");
        if (d.NativeModes.Count > 0)
            Console.WriteLine($"      modos propios: {string.Join(", ", d.NativeModes)}");
    }

    var e = s.Settings.SyncEffect;
    Console.WriteLine($"  Efecto: {(s.Settings.SyncAll ? "sincronizado" : $"{s.Settings.Layers.Count} capas")} · {e.Kind} {string.Join(" ", e.Colors)} · " +
                      $"velocidad {e.Speed} · brillo general {s.Settings.Brightness} %");
}

static LightingDeviceDto FindDevice(LightingStateDto state, string text) =>
    state.Devices.FirstOrDefault(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || d.Key.Contains(text, StringComparison.OrdinalIgnoreCase))
    ?? throw new ArgumentException($"No hay ningún dispositivo que contenga \"{text}\".");

static async Task SetSyncEffectAsync(CefiroApi api, string[] positional, string[] args)
{
    var state = await api.CallAsync<LightingStateDto>(Methods.GetLighting, null);
    var kind = Enum.Parse<EffectKind>(positional.ElementAtOrDefault(0) ?? "Static", ignoreCase: true);
    var colors = positional.Skip(1).Where(p => p.StartsWith('#')).ToList();
    var speed = GetArg(args, "--speed") is { } sp ? float.Parse(sp, CultureInfo.InvariantCulture) : 1f;
    var brightness = GetArg(args, "--brightness") is { } br ? float.Parse(br, CultureInfo.InvariantCulture) : 100f;

    var effect = new EffectDto
    {
        Kind = kind,
        Colors = colors.Count > 0 ? colors : state.Settings.SyncEffect.Colors,
        Speed = speed,
        Brightness = brightness,
        NativeMode = GetArg(args, "--native"),
        SensorId = GetArg(args, "--sensor"),
    };

    var applied = await api.CallAsync<LightingStateDto>(Methods.SetLighting, state.Settings with { SyncAll = true, SyncEffect = effect, Enabled = true });
    PrintLighting(applied);
}

static async Task StreamMusicAsync(CefiroApi api, int seconds)
{
    using var capture = new Cefiro.Audio.AudioSpectrumCapture(30);
    var sent = 0;
    capture.Error += message => Console.Error.WriteLine(message);
    capture.SpectrumReady += (bands, level) =>
    {
        _ = api.CallAsync(Methods.AudioSpectrum, new AudioSpectrumDto(bands, level));
        if (Interlocked.Increment(ref sent) % 30 == 0)
            Console.WriteLine($"  nivel {level:P0}  graves {bands[1]:0.00}  medios {bands[8]:0.00}  agudos {bands[14]:0.00}");
    };

    Console.WriteLine($"Escuchando \"{Cefiro.Audio.AudioSpectrumCapture.DefaultOutputName()}\" durante {seconds} s…");
    capture.Start();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    capture.Stop();
    Console.WriteLine($"Enviados {sent} espectros.");
}

static async Task AudioSelfTestAsync(CefiroApi api)
{
    // Reproduce un tono suave (100 Hz, 15 % de volumen) 4 s, después 3 s de silencio, y comprueba de
    // punta a punta que los LEDs que envía el servicio se iluminan con el sonido y se apagan sin él.
    var gen = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2)
    {
        Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin,
        Frequency = 100,
        Gain = 0.15,
    };
#pragma warning disable CS0618
    using var output = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
#pragma warning restore CS0618
    output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(gen));

    var playing = false;
    var withSound = new List<int>();
    var withoutSound = new List<int>();
    api.OtherEvent += (_, e) =>
    {
        if (e.Name != Events.Lighting || CefiroJson.FromElement<LightingFrameDto>(e.Payload) is not { } frame)
            return;

        // LED 0 de la primera RAM: escucha la banda de graves.
        var ram = frame.Devices.FirstOrDefault(d => d.DeviceKey.Contains("Vengeance", StringComparison.OrdinalIgnoreCase));
        if (ram is null || ram.Colors.Length < 6)
            return;

        var sum = Convert.ToInt32(ram.Colors[..2], 16) + Convert.ToInt32(ram.Colors[2..4], 16) + Convert.ToInt32(ram.Colors[4..6], 16);
        lock (withSound)
            (playing ? withSound : withoutSound).Add(sum);
    };

    using var capture = new Cefiro.Audio.AudioSpectrumCapture(30);
    var count = 0;
    var maxBass = 0f;
    capture.SpectrumReady += (bands, level) =>
    {
        Interlocked.Increment(ref count);
        maxBass = Math.Max(maxBass, bands.Take(4).Max());
        _ = api.CallAsync(Methods.AudioSpectrum, new AudioSpectrumDto(bands, level));
    };

    capture.Start();
    await Task.Delay(1500); // silencio inicial
    lock (withSound) withoutSound.Clear();
    playing = true;
    output.Play();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await Task.Delay(4000);
    output.Stop();
    var soundSeconds = sw.Elapsed.TotalSeconds;
    await Task.Delay(800);
    playing = false;
    await Task.Delay(2500);
    capture.Stop();

    Console.WriteLine($"Espectros: {count} (≈{count / (soundSeconds + 4.8):0.0} por segundo); graves máx {maxBass:0.00}");
    Console.WriteLine($"Captura: {capture.Diagnostics}");
    lock (withSound)
    {
        var on = withSound.Count > 0 ? withSound.Average() : 0;
        var off = withoutSound.Count > 0 ? withoutSound.TakeLast(Math.Max(1, withoutSound.Count / 2)).Average() : 0;
        Console.WriteLine($"Brillo del LED de graves de la RAM: con sonido {on:0} / sin sonido {off:0} (máx 765) · tramas {withSound.Count}/{withoutSound.Count}");
        Console.WriteLine(on > off * 3 && on > 150 ? "RESULTADO: las luces REACCIONAN al sonido" : "RESULTADO: NO reaccionan como deberían");
    }
}

static async Task WatchFansAsync(CefiroApi api, int seconds)
{
    var catalog = await api.GetCatalogAsync();
    var headers = catalog.FanChannels.ToDictionary(f => f.Id, f => f.Header);
    var count = 0;
    var done = new TaskCompletionSource();
    api.FanFrame += (_, frame) =>
    {
        var parts = frame.Channels
            .Where(c => c.EffectiveMode != FanMode.Bios || c.Note is not null || (c.Rpm ?? 0) > 0)
            .Select(c => $"{headers.GetValueOrDefault(c.ChannelId, c.ChannelId)}[{c.EffectiveMode} {c.OutputPercent:0}% {c.Rpm:0}rpm" +
                         (c.SourceTemperature is { } t ? $" {t:0.0}°C" : "") + (c.Note is { } n ? $" · {n}" : "") + "]");
        Console.WriteLine($"{DateTimeOffset.FromUnixTimeMilliseconds(frame.Timestamp).LocalDateTime:HH:mm:ss}{(frame.EmergencyActive ? " EMERGENCIA" : "")}  {string.Join("  ", parts)}");
        if (++count >= seconds)
            done.TrySetResult();
    };

    await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(seconds + 5)));
}

static async Task<FanChannelSettingsDto> FindChannelAsync(CefiroApi api, FanSettingsDto settings, string key)
{
    var catalog = await api.GetCatalogAsync();
    var info = catalog.FanChannels.FirstOrDefault(f =>
        string.Equals(f.Header, key, StringComparison.OrdinalIgnoreCase) || f.Id == key);
    return settings.Channels.FirstOrDefault(c => c.ChannelId == info?.Id)
           ?? throw new ArgumentException($"No existe la salida \"{key}\".");
}

static async Task SetFanAsync(CefiroApi api, string[] args)
{
    if (args.Length < 2)
        throw new ArgumentException("Uso: fan <conector> bios|fixed <%>");

    var settings = await api.GetFanSettingsAsync();
    var channel = await FindChannelAsync(api, settings, args[0]);
    var updated = args[1].ToLowerInvariant() switch
    {
        "bios" => channel with { Mode = FanMode.Bios },
        "fixed" => channel with { Mode = FanMode.Fixed, FixedPercent = float.Parse(args[2], CultureInfo.InvariantCulture) },
        "curve" => channel with { Mode = FanMode.Curve },
        _ => throw new ArgumentException("Modo desconocido: usa bios, fixed o curve."),
    };

    var channels = settings.Channels.Select(c => c.ChannelId == channel.ChannelId ? updated : c).ToList();
    var applied = await api.SetFanSettingsAsync(settings with { Channels = channels });
    var result = applied.Channels.First(c => c.ChannelId == channel.ChannelId);
    Console.WriteLine($"{args[0]}: modo {result.Mode}{(result.Mode == FanMode.Fixed ? $" al {result.FixedPercent:0} %" : "")}" +
                      (Math.Abs(result.FixedPercent - updated.FixedPercent) > 0.1 && result.Mode == FanMode.Fixed
                          ? $" (el servicio lo ajustó desde {updated.FixedPercent:0} % por seguridad)" : ""));
}

static async Task CalibrateAsync(CefiroApi api, string[] headers)
{
    var catalog = await api.GetCatalogAsync();
    var ids = headers.Length == 0
        ? catalog.FanChannels.Select(f => f.Id).ToList()
        : headers.Select(h => catalog.FanChannels.First(f => string.Equals(f.Header, h, StringComparison.OrdinalIgnoreCase)).Id).ToList();

    var done = new TaskCompletionSource<CalibrationStatusDto>();
    api.CalibrationChanged += (_, s) =>
    {
        Console.WriteLine($"  {s.Progress * 100:0} %  {s.Message}");
        if (!s.Running)
            done.TrySetResult(s);
    };

    var start = await api.StartCalibrationAsync(new CalibrationRequest(ids));
    Console.WriteLine($"Calibración iniciada: {start.Message}");
    var final = await done.Task.WaitAsync(TimeSpan.FromMinutes(20));
    foreach (var r in final.Results)
    {
        var header = catalog.FanChannels.FirstOrDefault(f => f.Id == r.ChannelId)?.Header ?? r.ChannelId;
        Console.WriteLine($"\n{header}: {r.Status} | se para por debajo de {r.StopPercent?.ToString("0") ?? "—"} % | " +
                          $"arranca desde {r.StartPercent?.ToString("0") ?? "—"} % | máx {r.MaxRpm?.ToString("0") ?? "—"} RPM");
        Console.WriteLine("   " + string.Join("  ", r.Points.Select(p => $"{p.Percent:0}%={p.Rpm:0}")));
    }
}
