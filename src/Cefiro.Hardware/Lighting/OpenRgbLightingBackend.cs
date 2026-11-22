using Cefiro.Core.Lighting;
using Cefiro.OpenRgb;
using Microsoft.Extensions.Logging;

namespace Cefiro.Hardware.Lighting;

/// <summary>Iluminación real a través del servidor SDK de OpenRGB (servicio local, puerto 6742).</summary>
public sealed class OpenRgbLightingBackend(ILogger<OpenRgbLightingBackend> logger) : ILightingBackend
{
    private OpenRgbClient? _client;

    public bool IsConnected => _client?.IsConnected == true;
    public string? ServerName => _client?.ServerName;
    public int ProtocolVersion => (int)(_client?.ProtocolVersion ?? 0);

    public event EventHandler? DevicesChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        var client = new OpenRgbClient("Céfiro");
        client.DeviceListUpdated += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
        client.Disconnected += (_, ex) =>
        {
            logger.LogWarning("Conexión con OpenRGB perdida{Reason}", ex is null ? "" : $": {ex.Message}");
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        };
        client.AckError += (_, e) =>
            logger.LogDebug("OpenRGB rechazó {Packet} en el dispositivo {Id}: {Status}", e.AckedPacket, e.DeviceId, e.Status);

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _client = client;
    }

    public async Task<IReadOnlyList<LightDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var client = Client();
        var devices = await client.RequestAllControllersAsync(cancellationToken).ConfigureAwait(false);
        return devices.Select(Map).ToList();
    }

    private static LightDevice Map(OpenRgbDevice d) => new()
    {
        // Los IDs de OpenRGB cambian al redetectar: la clave estable es nombre + ubicación.
        Key = $"{d.Name}|{d.Location}",
        Name = d.BestName,
        Vendor = d.Vendor,
        Kind = d.Type.ToString(),
        Location = d.Location,
        LedCount = d.Leds.Count,
        Zones = d.Zones.Select(z => new LightZone(
            z.Index, z.BestName, z.Type.ToString(), (int)z.LedsCount, (int)z.LedsMin, (int)z.LedsMax, z.StartLedIndex)).ToList(),
        NativeModes = d.Modes
            .Where(m => !m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase) && !m.Name.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name)
            .ToList(),
        LedNames = d.LedDisplayNames.Count == d.Leds.Count ? d.LedDisplayNames : d.Leds.Select(l => l.Name).ToList(),
        Handle = d,
    };

    public async Task SetDirectModeAsync(LightDevice device, CancellationToken cancellationToken)
    {
        var client = Client();
        var d = Native(device);
        var index = d.FindDirectModeIndex();
        if (index >= 0)
        {
            var mode = d.Modes[index].Clone();
            if (mode.HasPerLedColor)
                mode.ColorMode = ModeColorMode.PerLed;
            await client.UpdateModeAsync(d.Id, mode, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.SetCustomModeAsync(d.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SetNativeModeAsync(LightDevice device, string modeName, IReadOnlyList<Rgb> colors, double speed01, double brightness01, CancellationToken cancellationToken)
    {
        var client = Client();
        var d = Native(device);
        var template = d.Modes.FirstOrDefault(m => m.Name.Equals(modeName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException($"El dispositivo no tiene el modo \"{modeName}\".");
        var mode = template.Clone();

        if (mode.HasSpeed)
            mode.Speed = (uint)Math.Round(mode.SpeedMin + (mode.SpeedMax - (double)mode.SpeedMin) * Math.Clamp(speed01, 0, 1));

        if (mode.HasBrightness)
            mode.Brightness = (uint)Math.Round(mode.BrightnessMin + (mode.BrightnessMax - (double)mode.BrightnessMin) * Math.Clamp(brightness01, 0, 1));

        var wire = colors.Count > 0 ? colors.Select(c => new OpenRgbColor(c.R, c.G, c.B)).ToArray() : [new OpenRgbColor(255, 255, 255)];

        if (mode.Flags.HasFlag(ModeFlags.HasModeSpecificColor) && mode.ColorsMax > 0)
        {
            var count = (int)Math.Clamp(wire.Length, Math.Max(1, mode.ColorsMin), mode.ColorsMax);
            mode.Colors = Enumerable.Range(0, count).Select(i => wire[i % wire.Length]).ToArray();
            mode.ColorMode = ModeColorMode.ModeSpecific;
            await client.UpdateModeAsync(d.Id, mode, cancellationToken).ConfigureAwait(false);
        }
        else if (mode.HasPerLedColor)
        {
            mode.ColorMode = ModeColorMode.PerLed;
            await client.UpdateModeAsync(d.Id, mode, cancellationToken).ConfigureAwait(false);

            // Sin brillo propio en el modo: se atenúa el color enviado.
            var first = colors.Count > 0 ? colors[0] : Rgb.White;
            if (!mode.HasBrightness)
                first = first.Scale(brightness01);
            var fill = Enumerable.Repeat(new OpenRgbColor(first.R, first.G, first.B), d.Leds.Count).ToArray();
            await client.UpdateLedsAsync(d.Id, fill, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.UpdateModeAsync(d.Id, mode, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask SendColorsAsync(LightDevice device, ReadOnlyMemory<Rgb> colors, CancellationToken cancellationToken)
    {
        var client = Client();
        var d = Native(device);
        var span = colors.Span;
        var wire = new OpenRgbColor[span.Length];
        for (var i = 0; i < span.Length; i++)
            wire[i] = new OpenRgbColor(span[i].R, span[i].G, span[i].B);
        return client.UpdateLedsAsync(d.Id, wire, cancellationToken);
    }

    public async Task ResizeZoneAsync(LightDevice device, int zoneIndex, int ledCount, CancellationToken cancellationToken)
    {
        var client = Client();
        await client.ResizeZoneAsync(Native(device).Id, zoneIndex, ledCount, cancellationToken).ConfigureAwait(false);

        // OpenRGB aplica el cambio en su propio hilo; se le da un momento antes de volver a leer.
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
    }

    public int PendingUpdates(LightDevice device) =>
        _client?.GetOutstandingLedUpdates(Native(device).Id) ?? 0;

    private OpenRgbClient Client() =>
        _client is { IsConnected: true } c ? c : throw new InvalidOperationException("No hay conexión con OpenRGB.");

    private static OpenRgbDevice Native(LightDevice device) =>
        device.Handle as OpenRgbDevice ?? throw new InvalidOperationException("Dispositivo sin datos de OpenRGB.");

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
    }
}
