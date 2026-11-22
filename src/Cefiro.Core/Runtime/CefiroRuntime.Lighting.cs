using Cefiro.Core.Lighting;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging;

namespace Cefiro.Core.Runtime;

/// <summary>Iluminación: configuración, motor de efectos y vista previa.</summary>
public sealed partial class CefiroRuntime
{
    private const string LightingSettingsFile = "iluminacion.json";

    private LightingEngine? _lightingEngine;
    private volatile float?[]? _sensorSnapshot;

    private bool LightingConnected => _lightingEngine?.Connected ?? false;

    /// <summary>Lectura de un sensor segura desde cualquier hilo (copia del último ciclo).</summary>
    internal float? ReadSensorSnapshot(string id)
    {
        var snapshot = _sensorSnapshot;
        return snapshot is not null && _sensorIndex.TryGetValue(id, out var index) && index < snapshot.Length ? snapshot[index] : null;
    }

    private void RegisterLightingRoutes()
    {
        Route<object, LightingStateDto>(Methods.GetLighting, (_, _) => Task.FromResult(
            _lightingEngine?.GetState() ?? throw new InvalidOperationException("La iluminación todavía se está iniciando.")));

        Route<LightingSettingsDto, LightingStateDto>(Methods.SetLighting, (request, _) =>
        {
            var engine = _lightingEngine ?? throw new InvalidOperationException("La iluminación todavía se está iniciando.");
            ApplyLightingSettings(request ?? throw new ArgumentException("Configuración vacía."), save: true);
            return Task.FromResult(engine.GetState());
        });

        Route<ResizeZoneRequest, LightingStateDto>(Methods.ResizeZone, async (request, _) =>
        {
            var engine = _lightingEngine ?? throw new InvalidOperationException("La iluminación todavía se está iniciando.");
            await engine.ResizeZoneAsync(request ?? throw new ArgumentException("Petición vacía.")).ConfigureAwait(false);
            return engine.GetState();
        });

        Route<IdentifyRequest, object?>(Methods.IdentifyLeds, (request, _) =>
        {
            _lightingEngine?.Identify(request ?? throw new ArgumentException("Petición vacía."));
            return Task.FromResult<object?>(null);
        });

        Route<AudioSpectrumDto, object?>(Methods.AudioSpectrum, (request, _) =>
        {
            if (request is { Bands.Length: > 0 and <= 64 } spectrum && float.IsFinite(spectrum.Level))
                _lightingEngine?.PushAudio(spectrum);
            return Task.FromResult<object?>(null);
        });
    }

    private void StartLighting()
    {
        if (_lightingEngine is not null || _lightingBackend is null)
            return;

        var engine = new LightingEngine(_lightingBackend, _logger, ReadSensorSnapshot, () => _catalog?.Keys.CpuTemperature);
        engine.Frame += frame =>
        {
            if (HasListeners())
                Publish(Events.Lighting, frame);
        };
        engine.ZoneResized += OnZoneResized;
        _lightingEngine = engine;

        var stored = _store.Load<LightingSettingsDto?>(LightingSettingsFile, () => null);
        ApplyLightingSettings(stored, save: stored is null);
        engine.Start();
    }

    private LightingSettingsDto ApplyLightingSettings(LightingSettingsDto? input, bool save)
    {
        var temperatures = _sensors
            .Where(s => s.Kind == SensorKind.Temperature)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        // Mientras el hardware arranca no se conocen los sensores: se conserva el que viniera.
        if (temperatures.Count == 0 && input is not null)
        {
            foreach (var id in new[] { input.SyncEffect?.SensorId }.Concat(input.Layers?.Select(l => l.Effect?.SensorId) ?? []))
            {
                if (id is not null)
                    temperatures.Add(id);
            }
        }

        var validated = LightingValidator.Validate(input, temperatures);
        _lightingEngine?.Configure(validated);

        if (save)
        {
            try
            {
                _store.Save(LightingSettingsFile, validated);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo guardar la configuración de iluminación");
            }
        }

        return validated;
    }

    private void OnZoneResized(ZoneSizeDto size)
    {
        if (_lightingEngine is not { } engine)
            return;

        var current = engine.Settings;
        var sizes = current.ZoneSizes
            .Where(z => !(z.DeviceKey == size.DeviceKey && z.ZoneIndex == size.ZoneIndex))
            .Append(size)
            .ToList();
        ApplyLightingSettings(current with { ZoneSizes = sizes }, save: true);
    }

    private async Task StopLightingAsync()
    {
        if (_lightingEngine is { } engine)
        {
            _lightingEngine = null;
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }
}
