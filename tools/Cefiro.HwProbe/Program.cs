using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using Cefiro.OpenRgb;
using LibreHardwareMonitor.Hardware;

// Céfiro HwProbe: vuelca lo que ven LibreHardwareMonitor y OpenRGB en este PC.
// Uso: Cefiro.HwProbe.exe --out <fichero> [--label <texto>] [--test-fans] [--skip-lhm] [--skip-openrgb]
// --test-fans prueba el control SOLO de los ventiladores de caja (SYS_FAN) y de la GPU.
// Nunca toca CPU_FAN (radiador) ni PUMP_FAN (bomba).

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var outPath = GetArg(args, "--out") ?? Path.Combine(AppContext.BaseDirectory, "hwprobe-report.txt");
var label = GetArg(args, "--label") ?? "admin";
var testFans = args.Contains("--test-fans");
var skipLhm = args.Contains("--skip-lhm");
var skipOpenRgb = args.Contains("--skip-openrgb");

using var report = new Report(outPath);
report.Line($"Céfiro HwProbe — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
report.Line($"Etiqueta: {label} | Usuario: {Environment.UserName} | Administrador: {IsAdministrator()} | " +
            $"Sesión: {Process.GetCurrentProcess().SessionId} | .NET {Environment.Version}");
report.Line("");

if (!skipLhm)
{
    try
    {
        LhmProbe.Run(report, testFans, Path.ChangeExtension(outPath, ".lhm-report.txt"));
    }
    catch (Exception ex)
    {
        report.Line($"!! Error en LibreHardwareMonitor: {ex}");
    }
}

if (!skipOpenRgb)
{
    try
    {
        await OpenRgbProbe.RunAsync(report);
    }
    catch (Exception ex)
    {
        report.Line($"!! Error en OpenRGB: {ex}");
    }
}

report.Line("");
report.Line("FIN");
return 0;

static string? GetArg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

sealed class Report : IDisposable
{
    private readonly StreamWriter _writer;

    public Report(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Line(string text)
    {
        Console.WriteLine(text);
        _writer.WriteLine(text);
    }

    public void Dispose() => _writer.Dispose();
}

static class LhmProbe
{
    public static void Run(Report report, bool testFans, string fullReportPath)
    {
        report.Line("==================== LibreHardwareMonitor ====================");
        report.Line($"Versión de la librería: {typeof(Computer).Assembly.GetName().Version} " +
                    $"({typeof(Computer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion})");
        report.Line($"Driver PawnIO instalado: {PawnIoInstalled()}");

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = false,
        };

        var sw = Stopwatch.StartNew();
        computer.Open();
        report.Line($"Computer.Open(): {sw.ElapsedMilliseconds} ms");

        try
        {
            UpdateAll(computer);
            Thread.Sleep(1500);
            sw.Restart();
            UpdateAll(computer);
            report.Line($"Una actualización completa de sensores: {sw.ElapsedMilliseconds} ms");
            report.Line("");

            foreach (var hw in computer.Hardware)
                DumpHardware(report, hw, 0);

            File.WriteAllText(fullReportPath, computer.GetReport(), new UTF8Encoding(false));
            report.Line($"Informe completo de LHM guardado en: {fullReportPath}");

            if (testFans)
                FanTest.Run(report, computer);
        }
        finally
        {
            computer.Close(); // LHM devuelve los controles a su estado por defecto al cerrar
        }
    }

    private static bool PawnIoInstalled()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key is not null;
    }

    public static void UpdateAll(Computer computer)
    {
        foreach (var hw in computer.Hardware)
            UpdateHardware(hw);
    }

    private static void UpdateHardware(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware)
            UpdateHardware(sub);
    }

    private static void DumpHardware(Report report, IHardware hw, int depth)
    {
        var pad = new string(' ', depth * 2);
        report.Line($"{pad}[{hw.HardwareType}] {hw.Name}   ({hw.Identifier})");

        foreach (var sensor in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
        {
            var value = sensor.Value.HasValue ? sensor.Value.Value.ToString("0.##") : "—";
            var line = $"{pad}   {sensor.SensorType,-12} #{sensor.Index,-2} {sensor.Name,-34} = {value,-10} " +
                       $"[min {Fmt(sensor.Min)} / max {Fmt(sensor.Max)}]  {sensor.Identifier}";

            if (sensor.Control is { } control)
            {
                line += $"  ⇒ CONTROL modo={control.ControlMode} rango={control.MinSoftwareValue}-{control.MaxSoftwareValue}%";
            }

            report.Line(line);
        }

        foreach (var sub in hw.SubHardware)
            DumpHardware(report, sub, depth + 1);

        report.Line("");
    }

    private static string Fmt(float? v) => v.HasValue ? v.Value.ToString("0.##") : "—";
}

/// <summary>
/// Prueba de control: SYS_FAN y GPU al 35 % y al 70 % y vuelta a la BIOS/automático.
/// Se aborta si la CPU o la GPU están por encima de 80 °C.
/// </summary>
static class FanTest
{
    private const float MaxSafeTemperature = 80f;

    public static void Run(Report report, Computer computer)
    {
        report.Line("==================== Prueba de control de ventiladores ====================");

        var cpuTemp = FindTemperature(computer, HardwareType.Cpu, "Tctl", "Tdie", "Package", "Core");
        var gpuTemp = FindTemperature(computer, HardwareType.GpuNvidia, "GPU Core");
        report.Line($"Temperaturas antes de empezar: CPU {cpuTemp?.ToString("0.0") ?? "?"} °C, GPU {gpuTemp?.ToString("0.0") ?? "?"} °C");

        if (cpuTemp is null or > MaxSafeTemperature || gpuTemp > MaxSafeTemperature)
        {
            report.Line("!! Prueba cancelada: temperatura desconocida o demasiado alta.");
            return;
        }

        var targets = new List<(IHardware Hw, ISensor Control, ISensor? Fan)>();
        foreach (var hw in AllHardware(computer))
        {
            var isSuperIo = hw.HardwareType == HardwareType.SuperIO;
            var isGpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd;
            if (!isSuperIo && !isGpu)
                continue;

            foreach (var control in hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control is not null))
            {
                // Seguridad: nunca el cabezal de la bomba ni el de la CPU (radiador).
                if (isSuperIo && !control.Name.StartsWith("System Fan", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Index == control.Index)
                          ?? hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Name == control.Name);

                // Cabezales de caja sin ventilador (0 RPM): no se prueban.
                if (isSuperIo && (fan?.Value ?? 0) < 100)
                {
                    report.Line($"   (omitido {control.Name}: {fan?.Value ?? 0:0} RPM, parece vacío)");
                    continue;
                }

                targets.Add((hw, control, fan));
            }
        }

        var touched = new List<(IHardware Hw, ISensor Control)>();
        try
        {
            foreach (var (hw, control, fan) in targets)
            {
                report.Line($"-- {hw.Name} / {control.Name} (fan: {fan?.Name ?? "sin sensor de RPM"})");
                var before = Sample(computer, fan, control, 2);
                report.Line($"   BIOS/auto:  {Describe(before)}");

                touched.Add((hw, control));
                control.Control!.SetSoftware(35);
                var at35 = Sample(computer, fan, control, 8);
                report.Line($"   35 %:       {Describe(at35)}");

                control.Control!.SetSoftware(70);
                var at70 = Sample(computer, fan, control, 8);
                report.Line($"   70 %:       {Describe(at70)}");

                var forced = hw.HardwareType == HardwareType.SuperIO && SuperIoBiosRestore.ForceAutoOnRestore(hw, report);
                control.Control!.SetDefault();
                var after = Sample(computer, fan, control, 8);
                report.Line($"   Devuelto:   {Describe(after)}  (modo={control.Control.ControlMode}, auto forzado={forced})");

                var works = at70.Rpm > at35.Rpm + 150 || (fan is null && at70.Duty > at35.Duty);
                report.Line($"   RESULTADO: {(works ? "el control FUNCIONA" : "SIN efecto visible")}");
            }
        }
        finally
        {
            foreach (var (hw, control) in touched)
            {
                try
                {
                    if (hw.HardwareType == HardwareType.SuperIO)
                        SuperIoBiosRestore.ForceAutoOnRestore(hw, report: null);
                    control.Control?.SetDefault();
                }
                catch (Exception ex)
                {
                    report.Line($"!! No se pudo devolver {control.Name} a la BIOS: {ex.Message}");
                }
            }
        }
    }

    private readonly record struct Reading(float Rpm, float Duty);

    private static Reading Sample(Computer computer, ISensor? fan, ISensor control, int seconds)
    {
        Reading last = default;
        for (var i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            LhmProbe.UpdateAll(computer);
            last = new Reading(fan?.Value ?? 0, control.Value ?? -1);
        }

        return last;
    }

    private static string Describe(Reading r) => $"{r.Rpm,5:0} RPM, salida {r.Duty:0} %";

    private static float? FindTemperature(Computer computer, HardwareType type, params string[] names)
    {
        foreach (var hw in AllHardware(computer).Where(h => h.HardwareType == type))
        {
            foreach (var name in names)
            {
                var s = hw.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature
                                                      && x.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                                                      && x.Value.HasValue);
                if (s is not null)
                    return s.Value;
            }
        }

        return null;
    }

    private static IEnumerable<IHardware> AllHardware(Computer computer)
    {
        foreach (var hw in computer.Hardware)
        {
            yield return hw;
            foreach (var sub in hw.SubHardware)
                yield return sub;
        }
    }
}

/// <summary>
/// En los Nuvoton NCT6683D/6686D/6687D, LHM guarda el bit de "modo manual" que había al tomar el
/// control y lo repone al soltarlo. Si un proceso anterior se cerró de golpe, ese bit ya estaba en
/// manual y la "restauración" lo dejaría en manual. Aquí se fuerza el valor inicial a automático (0)
/// para que soltar el control devuelva siempre el ventilador a la curva de la BIOS.
/// </summary>
static class SuperIoBiosRestore
{
    public static bool ForceAutoOnRestore(IHardware superIoHardware, Report? report)
    {
        try
        {
            var superIo = superIoHardware.GetType()
                .GetField("_superIO", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(superIoHardware);

            if (superIo is null || superIo.GetType().Name != "Nct677X")
                return false;

            var chip = superIo.GetType().GetProperty("Chip")?.GetValue(superIo)?.ToString() ?? "";
            if (chip is not ("NCT6683D" or "NCT6686D" or "NCT6687D"))
                return false;

            if (superIo.GetType().GetField("_initialFanControlMode", BindingFlags.Instance | BindingFlags.NonPublic)?
                    .GetValue(superIo) is not byte[] initialModes)
                return false;

            Array.Clear(initialModes);
            return true;
        }
        catch (Exception ex)
        {
            report?.Line($"!! No se pudo forzar el modo automático: {ex.Message}");
            return false;
        }
    }
}

static class OpenRgbProbe
{
    public static async Task RunAsync(Report report)
    {
        report.Line("==================== OpenRGB (SDK) ====================");

        await using var client = new OpenRgbClient("Céfiro HwProbe");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        try
        {
            await client.ConnectAsync(cts.Token);
        }
        catch (Exception ex)
        {
            report.Line($"!! No se pudo conectar con el servidor de OpenRGB en 127.0.0.1:6742: {ex.Message}");
            return;
        }

        report.Line($"Conectado. Servidor: \"{client.ServerName}\" | protocolo servidor {client.ServerProtocolVersion} | " +
                    $"negociado {client.ProtocolVersion} | flags {client.ServerFlags}");

        // Redetección para que el resultado no dependa de lo que hubiera abierto al arrancar el servicio.
        if (client.ProtocolVersion >= 5)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.DetectionCompleted += (_, _) => done.TrySetResult();
            client.DeviceListUpdated += (_, _) => { };
            report.Line("Pidiendo una nueva detección de dispositivos...");
            await client.RescanDevicesAsync(cts.Token);
            var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(45), cts.Token));
            report.Line(finished == done.Task ? "Detección terminada." : "La detección no avisó de su final en 45 s; se continúa.");
            await Task.Delay(1000, cts.Token);
        }

        var devices = await client.RequestAllControllersAsync(cts.Token);
        report.Line($"Dispositivos: {devices.Count}");
        report.Line("");

        foreach (var d in devices)
        {
            report.Line($"[{d.Type}] {d.BestName}   (id {d.Id})");
            report.Line($"   Fabricante: {d.Vendor} | Descripción: {d.Description} | Versión: {d.Version}");
            report.Line($"   Ubicación: {d.Location} | Flags: {d.Flags}");
            report.Line($"   Modo activo: {(d.ActiveMode >= 0 && d.ActiveMode < d.Modes.Count ? d.Modes[d.ActiveMode].Name : d.ActiveMode.ToString())} | " +
                        $"Modo directo: {(d.FindDirectModeIndex() is var di && di >= 0 ? d.Modes[di].Name : "NINGUNO")}");
            report.Line($"   Modos ({d.Modes.Count}): {string.Join(", ", d.Modes.Select(m => $"{m.Name}{FlagsShort(m)}"))}");
            report.Line($"   LEDs totales: {d.Leds.Count}");

            foreach (var z in d.Zones)
            {
                report.Line($"   Zona {z.Index}: \"{z.BestName}\" tipo={z.Type} LEDs={z.LedsCount} (mín {z.LedsMin}, máx {z.LedsMax})" +
                            $" redimensionable={z.IsResizable} flags={z.Flags} segmentos={z.Segments.Count} modos propios={z.Modes.Count}");
            }

            report.Line("");
        }
    }

    private static string FlagsShort(OpenRgbMode m)
    {
        var parts = new List<string>();
        if (m.HasPerLedColor) parts.Add("porLED");
        if (m.HasSpeed) parts.Add("vel");
        if (m.HasBrightness) parts.Add("brillo");
        if (m.Flags.HasFlag(ModeFlags.HasModeSpecificColor)) parts.Add("color");
        return parts.Count == 0 ? "" : $" ({string.Join("/", parts)})";
    }
}
