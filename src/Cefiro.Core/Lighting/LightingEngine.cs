using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging;

namespace Cefiro.Core.Lighting;

/// <summary>
/// Motor de iluminación. Bucle propio (asíncrono) que:
///  - mantiene la conexión con el backend (OpenRGB) y se reconecta solo;
///  - ajusta el tamaño de los cabezales ARGB;
///  - pinta los efectos y solo envía lo que cambia (los efectos estáticos no gastan CPU);
///  - respeta un máximo de fotogramas por dispositivo y no satura los lentos (RAM).
/// Todo acceso al backend ocurre en este bucle.
/// </summary>
public sealed class LightingEngine : IAsyncDisposable
{
    private const double ReconnectSeconds = 5;
    private const double KeepAliveSeconds = 10;
    private const double PreviewInterval = 0.1;

    private readonly ILightingBackend _backend;
    private readonly ILogger _logger;
    private readonly Func<string, float?> _readSensor;
    private readonly Func<string?> _defaultTemperatureSensor;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<Func<CancellationToken, Task>> _actions = new();
    private readonly Dictionary<string, DeviceState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Value, double Time)> _smoothedTemperatures = new(StringComparer.Ordinal);

    private volatile LightingSettingsDto _settings = new();
    private volatile IReadOnlyList<LightDevice> _devices = [];
    private volatile bool _devicesDirty = true;
    private volatile bool _settingsDirty = true;
    private volatile string? _lastError;
    private volatile bool _nightActive;
    private volatile AudioSpectrumDto? _audio;
    private double _audioTime = -100;
    private (IdentifyRequest Request, double Until)? _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastConnectError = DateTime.MinValue;

    public LightingEngine(ILightingBackend backend, ILogger logger, Func<string, float?> readSensor, Func<string?> defaultTemperatureSensor)
    {
        _backend = backend;
        _logger = logger;
        _readSensor = readSensor;
        _defaultTemperatureSensor = defaultTemperatureSensor;
        _backend.DevicesChanged += (_, _) => _devicesDirty = true;
    }

    /// <summary>Vista previa de los colores para la interfaz (~10 por segundo, solo si hay alguien escuchando).</summary>
    public event Action<LightingFrameDto>? Frame;

    public bool PreviewEnabled { get; set; }
    public bool Connected => _backend.IsConnected;
    public LightingSettingsDto Settings => _settings;
    public IReadOnlyList<LightDevice> Devices => _devices;

    /// <summary>Tras cambiar el tamaño de una zona (para que el núcleo lo guarde).</summary>
    public event Action<ZoneSizeDto>? ZoneResized;

    public void Start()
    {
        if (_loop is not null)
            return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Configure(LightingSettingsDto settings)
    {
        _settings = settings;
        _settingsDirty = true;
    }

    public void PushAudio(AudioSpectrumDto spectrum)
    {
        _audio = spectrum;
        Volatile.Write(ref _audioTime, _clock.Elapsed.TotalSeconds);
    }

    public void Identify(IdentifyRequest request) =>
        _actions.Enqueue(_ =>
        {
            _identify = (request, _clock.Elapsed.TotalSeconds + Math.Clamp(request.DurationMs, 500, 15000) / 1000.0);
            return Task.CompletedTask;
        });

    /// <summary>Cambia el número de LEDs de una zona y espera a que se aplique.</summary>
    public Task ResizeZoneAsync(ResizeZoneRequest request)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _actions.Enqueue(async ct =>
        {
            try
            {
                var device = _devices.FirstOrDefault(d => d.Key == request.DeviceKey)
                             ?? throw new ArgumentException("El dispositivo no está conectado.");
                var zone = device.Zones.FirstOrDefault(z => z.Index == request.ZoneIndex)
                           ?? throw new ArgumentException("La zona no existe.");
                if (!zone.Resizable)
                    throw new ArgumentException("Esta zona tiene un número fijo de LEDs.");

                var count = Math.Clamp(request.LedCount, zone.MinLeds, zone.MaxLeds);
                await _backend.ResizeZoneAsync(device, zone.Index, count, ct).ConfigureAwait(false);
                ZoneResized?.Invoke(new ZoneSizeDto(device.Key, zone.Index, count));
                _devicesDirty = true;
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    public LightingStateDto GetState() => new(
        _backend.IsConnected,
        _backend.ServerName,
        _backend.ProtocolVersion,
        _devices.Select(ToDto).ToList(),
        _settings,
        _nightActive,
        _lastError);

    public static LightingDeviceDto ToDto(LightDevice d) => new(
        d.Key, d.Name, d.Vendor, d.Kind, d.Location, d.LedCount,
        d.Zones.Select(z => new LightingZoneDto(z.Index, z.Name, z.Type, z.LedCount, z.MinLeds, z.MaxLeds, z.Resizable, z.StartLed)).ToList(),
        d.NativeModes,
        d.LedNames);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        double nextConnect = 0;
        double lastPreview = 0;
        double lastNightCheck = -10;

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = _clock.Elapsed.TotalSeconds;
            try
            {
                if (!_backend.IsConnected)
                {
                    if (now >= nextConnect)
                    {
                        nextConnect = now + ReconnectSeconds;
                        await TryConnectAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (!_backend.IsConnected)
                    {
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                while (_actions.TryDequeue(out var action))
                    await action(cancellationToken).ConfigureAwait(false);

                if (_devicesDirty)
                    await RefreshDevicesAsync(cancellationToken).ConfigureAwait(false);

                if (now - lastNightCheck >= 1)
                {
                    lastNightCheck = now;
                    var night = LightingValidator.IsNightActive(_settings.Night, DateTime.Now.TimeOfDay);
                    if (night != _nightActive)
                    {
                        _nightActive = night;
                        _settingsDirty = true;
                        _logger.LogInformation("Modo noche {State}", night ? "activado" : "desactivado");
                    }
                }

                var delay = await RenderAndSendAsync(now, cancellationToken).ConfigureAwait(false);

                if (PreviewEnabled && now - lastPreview >= PreviewInterval)
                {
                    lastPreview = now;
                    PublishPreview();
                }

                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning(ex, "Error en el motor de iluminación; se reintenta");
                _devicesDirty = true;
                try { await Task.Delay(2000, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _backend.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _lastError = null;
            _devicesDirty = true;
            _logger.LogInformation("Conectado con {Server} (protocolo {Protocol})", _backend.ServerName ?? "el servidor RGB", _backend.ProtocolVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = "No se puede conectar con OpenRGB: ¿está en marcha su servicio?";
            if (DateTime.UtcNow - _lastConnectError > TimeSpan.FromMinutes(1))
            {
                _lastConnectError = DateTime.UtcNow;
                _logger.LogWarning("No se puede conectar con el servidor RGB: {Message}", ex.Message);
            }
        }
    }

    private async Task RefreshDevicesAsync(CancellationToken cancellationToken)
    {
        _devicesDirty = false;
        var devices = await _backend.GetDevicesAsync(cancellationToken).ConfigureAwait(false);

        // Cabezales ARGB: se ajustan al número de LEDs guardado (o a un valor inicial si están a 0).
        var resized = false;
        var sizes = _settings.ZoneSizes;
        foreach (var device in devices)
        {
            foreach (var zone in device.Zones.Where(z => z.Resizable))
            {
                var wanted = sizes.FirstOrDefault(s => s.DeviceKey == device.Key && s.ZoneIndex == zone.Index)?.LedCount
                             ?? (zone.LedCount == 0 ? DefaultResizableZoneSize(zone) : zone.LedCount);
                wanted = Math.Clamp(wanted, zone.MinLeds, zone.MaxLeds);
                if (wanted != zone.LedCount)
                {
                    _logger.LogInformation("{Device} · {Zone}: {Old} → {New} LEDs", device.Name, zone.Name, zone.LedCount, wanted);
                    await _backend.ResizeZoneAsync(device, zone.Index, wanted, cancellationToken).ConfigureAwait(false);
                    resized = true;
                }
            }
        }

        if (resized)
            devices = await _backend.GetDevicesAsync(cancellationToken).ConfigureAwait(false);

        // Se conserva el estado de los que siguen; los nuevos empiezan sin modo aplicado.
        var keys = devices.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _states.Keys.Where(k => !keys.Contains(k)).ToList())
            _states.Remove(gone);
        foreach (var d in devices)
        {
            if (_states.TryGetValue(d.Key, out var st) && st.LedCount != d.LedCount)
                _states.Remove(d.Key);
        }

        _devices = devices;
        _settingsDirty = true;
        _logger.LogInformation("Dispositivos RGB: {List}", string.Join(", ", devices.Select(d => $"{d.Name} ({d.LedCount} LEDs)")));
    }

    private static int DefaultResizableZoneSize(LightZone zone) =>
        Math.Clamp(LightingEngineDefaults.ResizableZoneLeds, zone.MinLeds, zone.MaxLeds);

    private async Task<double> RenderAndSendAsync(double now, CancellationToken cancellationToken)
    {
        var settings = _settings;
        var forceResend = _settingsDirty;
        _settingsDirty = false;

        var nightFactor = 1.0;
        var nightOff = false;
        if (_nightActive)
        {
            nightOff = settings.Night.Action == NightAction.TurnOff;
            nightFactor = nightOff ? 0 : settings.Night.DimBrightness / 100.0;
        }

        var identify = _identify;
        if (identify is { } id && now > id.Until)
        {
            _identify = identify = null;
            forceResend = true;
        }

        var anyAnimated = identify is not null;
        var anySlow = false;
        var fps = Math.Clamp(settings.Fps, 5, 60);

        foreach (var device in _devices)
        {
            var plan = Plan(device, settings);
            if (plan.Count == 0)
                continue; // dispositivo sin efecto asignado: no se toca

            var state = State(device);
            if (now < state.RetryAfter)
                continue;

            try
            {
                anyAnimated |= await RenderDeviceAsync(device, state, plan, settings, identify, now, nightOff, nightFactor, fps, forceResend, cancellationToken).ConfigureAwait(false);
                anySlow |= plan.Any(l => l.Effect.Kind == EffectKind.Temperature);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un dispositivo que falla no debe bloquear a los demás: se reintenta en unos segundos.
                state.Direct = false;
                state.NativeSignature = null;
                state.RetryAfter = now + 5;
                if (now - state.LastErrorLog > 60)
                {
                    state.LastErrorLog = now;
                    _logger.LogWarning(ex, "Error enviando la iluminación a {Device}", device.Name);
                }
            }
        }

        if (anyAnimated)
            return 1.0 / fps;
        return anySlow ? 0.5 : 1.0;
    }

    /// <summary>Pinta y envía un dispositivo. Devuelve true si su efecto es animado.</summary>
    private async Task<bool> RenderDeviceAsync(
        LightDevice device, DeviceState state, List<PlannedLayer> plan, LightingSettingsDto settings,
        (IdentifyRequest Request, double Until)? identify, double now, bool nightOff, double nightFactor, int fps,
        bool forceResend, CancellationToken cancellationToken)
    {
        // Efecto propio del dispositivo (sin modo noche "apagar" ni identificación en curso).
        if (plan is [{ Whole: true, Effect.Kind: EffectKind.DeviceMode } nativeLayer]
            && !nightOff
            && identify?.Request.DeviceKey != device.Key)
        {
            var effect = nativeLayer.Effect;
            var mode = ResolveNativeMode(device, effect.NativeMode);
            if (mode is not null)
            {
                var signature = $"{mode}|{string.Join(",", effect.Colors)}|{effect.Speed * settings.SpeedMultiplier}|{effect.Brightness}|{settings.Brightness}|{nightFactor}";
                if (state.NativeSignature != signature)
                {
                    var colors = effect.Colors.Select(h => Rgb.FromHex(h, Rgb.White)).ToList();
                    await _backend.SetNativeModeAsync(device, mode, colors,
                        SpeedToUnit(effect.Speed * settings.SpeedMultiplier),
                        effect.Brightness / 100.0 * settings.Brightness / 100.0 * nightFactor,
                        cancellationToken).ConfigureAwait(false);
                    state.NativeSignature = signature;
                    state.Direct = false;
                    state.LastSent = null;
                }

                return false;
            }

            // Este dispositivo no tiene un modo parecido: se imita por software.
            plan = [nativeLayer with { Effect = effect with { Kind = SoftwareEquivalent(effect.NativeMode) } }];
        }

        if (!state.Direct)
        {
            await _backend.SetDirectModeAsync(device, cancellationToken).ConfigureAwait(false);
            state.Direct = true;
            state.NativeSignature = null;
            state.LastSent = null;
        }

        var animated = identify?.Request.DeviceKey == device.Key || plan.Any(l => EffectRenderer.IsAnimated(l.Effect.Kind));

        var deviceFps = Math.Min(fps, device.MaxFps);
        if (animated && now < state.NextFrame && !forceResend)
            return animated;
        state.NextFrame = now + 1.0 / deviceFps;

        // Los LEDs que ninguna capa cubre quedan apagados.
        var buffer = state.Buffer;
        Array.Clear(buffer);
        foreach (var layer in plan)
            RenderLayer(device, layer, buffer, settings, now, nightOff ? 0 : nightFactor);

        if (identify is { } ident && ident.Request.DeviceKey == device.Key)
            Overlay(device, ident.Request, buffer, now);

        var changed = state.LastSent is null || !buffer.AsSpan().SequenceEqual(state.LastSent);
        if ((changed || forceResend || now - state.LastSendTime > KeepAliveSeconds) && _backend.PendingUpdates(device) < 2)
        {
            var copy = buffer.ToArray();
            await _backend.SendColorsAsync(device, copy, cancellationToken).ConfigureAwait(false);
            state.LastSent = copy;
            state.LastSendTime = now;
        }

        return animated;
    }

    /// <summary>Busca el modo propio pedido: nombre exacto, o uno que lo contenga ("Rainbow" → "Rainbow wave").</summary>
    public static string? ResolveNativeMode(LightDevice device, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return device.NativeModes.FirstOrDefault();

        return device.NativeModes.FirstOrDefault(m => m.Equals(requested, StringComparison.OrdinalIgnoreCase))
               ?? device.NativeModes.FirstOrDefault(m => m.Contains(requested, StringComparison.OrdinalIgnoreCase))
               ?? device.NativeModes.FirstOrDefault(m => requested.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Efecto por software parecido a un modo propio que el dispositivo no tiene.</summary>
    public static EffectKind SoftwareEquivalent(string? nativeMode)
    {
        var m = nativeMode?.ToLowerInvariant() ?? "";
        if (m.Contains("rainbow") || m.Contains("spectrum")) return EffectKind.Rainbow;
        if (m.Contains("breath") || m.Contains("pulse")) return EffectKind.Breathing;
        if (m.Contains("wave") || m.Contains("visor") || m.Contains("marquee")) return EffectKind.Wave;
        if (m.Contains("shift") || m.Contains("cycle")) return EffectKind.ColorCycle;
        if (m.Contains("meteor") || m.Contains("comet") || m.Contains("rain")) return EffectKind.Comet;
        return EffectKind.Static;
    }

    private readonly record struct PlannedLayer(bool Whole, int? Zone, int? Led, EffectDto Effect);

    private static List<PlannedLayer> Plan(LightDevice device, LightingSettingsDto settings)
    {
        if (!settings.Enabled)
            return [new PlannedLayer(true, null, null, new EffectDto { Kind = EffectKind.Off })];

        if (settings.SyncAll)
            return [new PlannedLayer(true, null, null, settings.SyncEffect)];

        return settings.Layers
            .Where(l => l.Target.DeviceKey == device.Key)
            .Select(l => new PlannedLayer(l.Target.ZoneIndex is null && l.Target.LedIndex is null, l.Target.ZoneIndex, l.Target.LedIndex, l.Effect))
            .ToList();
    }

    private void RenderLayer(LightDevice device, PlannedLayer layer, Rgb[] buffer, LightingSettingsDto settings, double now, double nightFactor)
    {
        var ctx = Context(layer.Effect, settings, now);
        var brightness = layer.Effect.Brightness / 100.0 * settings.Brightness / 100.0 * nightFactor;

        void Paint(int start, int count)
        {
            if (count <= 0 || start < 0 || start + count > buffer.Length)
                return;

            var span = buffer.AsSpan(start, count);
            EffectRenderer.Render(layer.Effect.Kind == EffectKind.DeviceMode ? EffectKind.Static : layer.Effect.Kind, span, ctx);
            if (brightness < 0.999)
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = span[i].Scale(brightness);
            }
        }

        if (layer.Led is { } led)
        {
            Paint(led, 1);
        }
        else if (layer.Zone is { } zoneIndex)
        {
            if (device.Zones.FirstOrDefault(z => z.Index == zoneIndex) is { } zone)
                Paint(zone.StartLed, zone.LedCount);
        }
        else if (device.Zones.Count > 0)
        {
            foreach (var zone in device.Zones)
                Paint(zone.StartLed, zone.LedCount);
        }
        else
        {
            Paint(0, buffer.Length);
        }
    }

    private EffectContext Context(EffectDto effect, LightingSettingsDto settings, double now)
    {
        var colors = effect.Colors.Select(h => Rgb.FromHex(h, Rgb.White)).ToList();

        double? temperature01 = null;
        if (effect.Kind == EffectKind.Temperature)
        {
            var sensor = effect.SensorId ?? _defaultTemperatureSensor();
            if (sensor is not null && _readSensor(sensor) is { } raw)
            {
                // Suavizado (~2 s) para que el color no tiemble con cada lectura.
                var smoothed = raw;
                if (_smoothedTemperatures.TryGetValue(sensor, out var prev))
                {
                    var alpha = Math.Clamp((now - prev.Time) / 2.0, 0, 1);
                    smoothed = (float)(prev.Value + (raw - prev.Value) * alpha);
                }

                _smoothedTemperatures[sensor] = (smoothed, now);
                temperature01 = Math.Clamp((smoothed - effect.MinTemperature) / Math.Max(1, effect.MaxTemperature - effect.MinTemperature), 0, 1);
            }
        }

        float[]? bands = null;
        float level = 0;
        if (effect.Kind == EffectKind.Music && now - Volatile.Read(ref _audioTime) < 0.5 && _audio is { } audio)
        {
            bands = audio.Bands;
            level = audio.Level;
        }

        return new EffectContext(now, effect.Speed * settings.SpeedMultiplier, colors, effect.Reverse, temperature01, bands, level, effect.Sensitivity);
    }

    private static void Overlay(LightDevice device, IdentifyRequest request, Rgb[] buffer, double now)
    {
        var on = (int)(now * 4) % 2 == 0;
        var color = on ? Rgb.White : Rgb.Black;

        if (request.LedIndex is { } led && led >= 0 && led < buffer.Length)
        {
            buffer[led] = color;
            return;
        }

        var (start, count) = request.ZoneIndex is { } z && device.Zones.FirstOrDefault(x => x.Index == z) is { } zone
            ? (zone.StartLed, zone.LedCount)
            : (0, buffer.Length);

        buffer.AsSpan(Math.Max(0, start), Math.Max(0, Math.Min(count, buffer.Length - start))).Fill(color);
    }

    /// <summary>Velocidad relativa (0,1-5, 1 = normal) a la escala 0-1 de los modos del dispositivo.</summary>
    public static double SpeedToUnit(double speed) =>
        Math.Clamp(Math.Log2(Math.Max(0.01, speed)) / 4 + 0.5, 0, 1);

    private void PublishPreview()
    {
        var handler = Frame;
        if (handler is null)
            return;

        var devices = new List<DeviceColorsDto>();
        foreach (var device in _devices)
        {
            if (!_states.TryGetValue(device.Key, out var st))
                continue;

            var source = st.LastSent ?? st.Buffer;
            var sb = new StringBuilder(source.Length * 6);
            foreach (var c in source)
                sb.Append(c.R.ToString("X2")).Append(c.G.ToString("X2")).Append(c.B.ToString("X2"));
            devices.Add(new DeviceColorsDto(device.Key, sb.ToString()));
        }

        handler(new LightingFrameDto(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), devices));
    }

    private DeviceState State(LightDevice device)
    {
        if (!_states.TryGetValue(device.Key, out var state))
            _states[device.Key] = state = new DeviceState(device.LedCount);
        return state;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); }
                catch { /* cierre */ }
            }

            _cts.Dispose();
        }

        await _backend.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class DeviceState(int ledCount)
    {
        public int LedCount { get; } = ledCount;
        public Rgb[] Buffer { get; } = new Rgb[ledCount];
        public Rgb[]? LastSent { get; set; }
        public double LastSendTime { get; set; } = -100;
        public double NextFrame { get; set; }
        public bool Direct { get; set; }
        public string? NativeSignature { get; set; }
        public double RetryAfter { get; set; }
        public double LastErrorLog { get; set; } = -1000;
    }
}

/// <summary>Valores por defecto ajustables del motor.</summary>
public static class LightingEngineDefaults
{
    /// <summary>
    /// LEDs que se asignan a un cabezal ARGB que está a 0 y nunca se ha configurado. 48 cubre las dos
    /// cadenas de tu PC (líquida: bomba 8 + 3 × 8 = 32; caja: 6 × 8 = 48). Se ajusta desde la interfaz.
    /// </summary>
    public static int ResizableZoneLeds { get; set; } = LightingValidator.DefaultResizableZoneLeds;
}
