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
