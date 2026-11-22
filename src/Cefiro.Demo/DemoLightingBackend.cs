using Cefiro.Core.Lighting;

namespace Cefiro.Demo;

/// <summary>
/// Dispositivos RGB simulados con la forma de los tuyos: placa MSI (JRGB1, JRGB2, JRAINBOW1, JRAINBOW2)
/// y dos módulos Vengeance RGB DDR5. Guarda los colores en memoria; no toca nada real.
/// </summary>
public sealed class DemoLightingBackend : ILightingBackend
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Rgb[]> _colors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modes = new(StringComparer.Ordinal);
    private int _jrainbow1 = 32;
    private int _jrainbow2 = 48;
    private bool _connected;

    public bool IsConnected => _connected;
    public string? ServerName => _connected ? "OpenRGB (simulado)" : null;
    public int ProtocolVersion => 6;

    public event EventHandler? DevicesChanged;

    /// <summary>Número de envíos de colores recibidos (para los tests).</summary>
    public int SendCount { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LightDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var board = Device("MSI X670E GAMING PLUS WIFI (MS-7E16) · DEMO|HID: demo", "MSI X670E GAMING PLUS WIFI (MS-7E16) · DEMO", "MSI", "Motherboard",
                [("JRGB1", 1, 1, 1), ("JRGB2", 1, 1, 1), ("JRAINBOW1", _jrainbow1, 0, 200), ("JRAINBOW2", _jrainbow2, 0, 240)],
                ["Static", "Breathing", "Flashing", "Rainbow wave", "Color shift", "Fire"]);
            var ram1 = Device("Corsair Vengeance RGB DDR5|I2C: demo 0x19", "Corsair Vengeance RGB DDR5", "Corsair", "Dram",
                [("Corsair DRAM", 10, 10, 10)], ["Color Shift", "Color Pulse", "Rainbow Wave", "Rainbow"]);
            var ram2 = Device("Corsair Vengeance RGB DDR5|I2C: demo 0x1B", "Corsair Vengeance RGB DDR5", "Corsair", "Dram",
                [("Corsair DRAM", 10, 10, 10)], ["Color Shift", "Color Pulse", "Rainbow Wave", "Rainbow"]);
            return Task.FromResult<IReadOnlyList<LightDevice>>([board, ram1, ram2]);
        }
    }

    private LightDevice Device(string key, string name, string vendor, string kind, (string Name, int Leds, int Min, int Max)[] zones, string[] modes)
    {
        var list = new List<LightZone>();
        var start = 0;
        for (var i = 0; i < zones.Length; i++)
        {
            var z = zones[i];
            list.Add(new LightZone(i, z.Name, z.Leds == 1 && z.Max == 1 ? "Single" : "Linear", z.Leds, z.Min, z.Max, start));
            start += z.Leds;
        }

        if (!_colors.TryGetValue(key, out var colors) || colors.Length != start)
            _colors[key] = new Rgb[start];

        return new LightDevice
        {
            Key = key,
            Name = name,
            Vendor = vendor,
            Kind = kind,
            Location = key.Split('|')[1],
            Zones = list,
            LedCount = start,
            NativeModes = modes,
            LedNames = Enumerable.Range(1, start).Select(i => $"LED {i}").ToList(),
        };
    }

    public Task SetDirectModeAsync(LightDevice device, CancellationToken cancellationToken)
    {
        lock (_gate)
            _modes[device.Key] = "Direct";
        return Task.CompletedTask;
    }

    public Task SetNativeModeAsync(LightDevice device, string mode, IReadOnlyList<Rgb> colors, double speed01, double brightness01, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _modes[device.Key] = mode;
            if (_colors.TryGetValue(device.Key, out var buffer) && colors.Count > 0)
                Array.Fill(buffer, colors[0].Scale(brightness01));
        }

        return Task.CompletedTask;
    }

    public ValueTask SendColorsAsync(LightDevice device, ReadOnlyMemory<Rgb> colors, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_colors.TryGetValue(device.Key, out var buffer) && buffer.Length == colors.Length)
                colors.Span.CopyTo(buffer);
            SendCount++;
        }

        return ValueTask.CompletedTask;
    }

    public Task ResizeZoneAsync(LightDevice device, int zoneIndex, int ledCount, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (zoneIndex == 2) _jrainbow1 = ledCount;
            if (zoneIndex == 3) _jrainbow2 = ledCount;
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public int PendingUpdates(LightDevice device) => 0;

    /// <summary>Colores actuales de un dispositivo (para los tests).</summary>
    public Rgb[] ColorsOf(string key)
    {
        lock (_gate)
            return _colors.TryGetValue(key, out var c) ? (Rgb[])c.Clone() : [];
    }

    public string? ModeOf(string key)
    {
        lock (_gate)
            return _modes.GetValueOrDefault(key);
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }
}
