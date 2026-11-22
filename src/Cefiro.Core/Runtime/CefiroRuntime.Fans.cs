using Cefiro.Core.Fans;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging;

namespace Cefiro.Core.Runtime;

/// <summary>Ventiladores: configuración, motor, calibración y seguridad.</summary>
public sealed partial class CefiroRuntime
{
    private const string FanSettingsFile = "ventiladores.json";
    private const string CalibrationFile = "calibracion.json";

    private FanEngine? _fanEngine;
    private FanCalibrator? _calibrator;
    private volatile FanSettingsDto? _fanSettings;

    private bool EmergencyActive => _fanEngine?.EmergencyActive ?? false;
    private string? EmergencyReason => _fanEngine?.EmergencyReason;

    /// <summary>Configuración de ventiladores vigente (ya validada).</summary>
    public FanSettingsDto? FanSettings => _fanSettings;

    private void RegisterFanRoutes()
    {
        Route<object, FanSettingsDto>(Methods.GetFanSettings, (_, _) =>
            Task.FromResult(_fanSettings ?? throw new InvalidOperationException("Los ventiladores todavía se están iniciando.")));

        Route<FanSettingsDto, FanSettingsDto>(Methods.SetFanSettings, (request, _) =>
            RunOnHardwareThreadAsync(() => ApplyFanSettings(request ?? throw new ArgumentException("Configuración vacía."), save: true)));

        Route<ApplyPresetRequest, FanSettingsDto>(Methods.ApplyFanPreset, (request, _) =>
            RunOnHardwareThreadAsync(() => ApplyPreset(request ?? throw new ArgumentException("Petición vacía."))));

        Route<CalibrationRequest, CalibrationStatusDto>(Methods.StartCalibration, (request, _) =>
            RunOnHardwareThreadAsync(() => StartCalibration(request ?? throw new ArgumentException("Petición vacía."))));

        Route<object, CalibrationStatusDto>(Methods.CancelCalibration, (_, _) => RunOnHardwareThreadAsync(() =>
        {
            if (_calibrator is { Running: true } && _fanEngine is not null)
                _calibrator.Cancel("cancelada por el usuario", _fanEngine);
            return CalibrationStatus();
        }));

        Route<object, CalibrationStatusDto>(Methods.GetCalibration, (_, _) => RunOnHardwareThreadAsync(CalibrationStatus));
    }

    /// <summary>Se llama en el hilo de hardware cuando el catálogo ya está construido.</summary>
    private void OnHardwareOpened()
    {
        if (_fanEngine is null)
        {
            _fanEngine = new FanEngine(_logger);
            _fanEngine.Alert += (kind, title, message) => PublishAlert(kind, title, message);
            _calibrator = new FanCalibrator(_logger);
        }

        var stored = _store.Load<FanSettingsDto?>(FanSettingsFile, () => null);
        var applied = ApplyFanSettings(stored, save: stored is null);

        var controlled = applied.Channels.Count(c => c.Mode != FanMode.Bios);
        _logger.LogInformation("Ventiladores configurados: {Controlled} de {Total} salidas bajo control de Céfiro", controlled, applied.Channels.Count);
    }

    private FanSettingsDto ApplyFanSettings(FanSettingsDto? input, bool save)
    {
        var catalog = _catalog ?? throw new InvalidOperationException("El hardware todavía no está listo.");
        var engine = _fanEngine ?? throw new InvalidOperationException("El motor de ventiladores no está listo.");

        var validated = FanSettingsValidator.Validate(input, catalog.FanChannels, _sensors, catalog.Keys, _pumpsSeen);
        engine.Configure(validated);
        _fanSettings = validated;

        if (save)
        {
            try
            {
                _store.Save(FanSettingsFile, validated);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo guardar la configuración de ventiladores");
            }
        }

        return validated;
    }

    private FanSettingsDto ApplyPreset(ApplyPresetRequest request)
    {
        var current = _fanSettings ?? throw new InvalidOperationException("Los ventiladores todavía se están iniciando.");
        var targets = request.ChannelIds is { Count: > 0 } ids ? ids.ToHashSet(StringComparer.Ordinal) : null;

        var channels = current.Channels
            .Select(c => targets is null || targets.Contains(c.ChannelId) ? FanDefaults.ApplyPreset(c, request.Preset) : c)
            .ToList();

        _logger.LogInformation("Modo de ventiladores {Preset} aplicado a {Count} salida(s)", request.Preset, targets?.Count ?? channels.Count);
        return ApplyFanSettings(current with { Channels = channels }, save: true);
    }

    private CalibrationStatusDto StartCalibration(CalibrationRequest request)
    {
        var settings = _fanSettings ?? throw new InvalidOperationException("Los ventiladores todavía se están iniciando.");
        var calibrator = _calibrator ?? throw new InvalidOperationException("La calibración no está disponible.");
        if (EmergencyActive)
            throw new InvalidOperationException("No se puede calibrar en modo de emergencia.");

        var channels = request.ChannelIds
            .Select(id => settings.Channels.FirstOrDefault(c => c.ChannelId == id))
            .Where(c => c is not null)
            .Select(c => (c!.ChannelId, _pumpsSeen.ContainsKey(c.ChannelId) ? FanRole.Pump : c.Role))
            .ToList();

        if (channels.Count == 0)
            throw new ArgumentException("No se ha indicado ninguna salida válida.");

        calibrator.Start(channels);
        var status = calibrator.Status;
        Publish(Events.Calibration, status);
        return status;
    }

    private CalibrationStatusDto CalibrationStatus()
    {
        if (_calibrator is { } c && (c.Running || c.Results.Count > 0))
            return c.Status;

        var saved = _store.Load<List<CalibrationResultDto>>(CalibrationFile, () => []);
        return new CalibrationStatusDto(false, null, saved.Count > 0 ? 1 : 0, saved, null);
    }

    private void SaveCalibration()
    {
        if (_calibrator is null || _calibrator.Results.Count == 0)
            return;

        try
        {
            var saved = _store.Load<List<CalibrationResultDto>>(CalibrationFile, () => []);
            foreach (var result in _calibrator.Results.Where(r => r.Status == "Completada"))
            {
                saved.RemoveAll(r => r.ChannelId == result.ChannelId);
                saved.Add(result);
            }

            _store.Save(CalibrationFile, saved);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la calibración");
        }
    }

    private void TickFans(long timestamp)
    {
        if (_fanEngine is null || _catalog is not { } catalog)
            return;

        var now = DateTime.UtcNow;
        var frame = _fanEngine.Tick(_backend.FanOutputs, ReadSensor, catalog.Keys, _pumpsSeen, now, timestamp);

        if (_calibrator is { Running: true } calibrator)
        {
            var cpu = catalog.Keys.CpuTemperature is { } cpuId ? ReadSensor(cpuId) : null;
            var gpu = catalog.Keys.GpuTemperature is { } gpuId ? ReadSensor(gpuId) : null;
            if (calibrator.Tick(_backend.FanOutputs, _fanEngine, cpu, gpu, now))
            {
                Publish(Events.Calibration, calibrator.Status);
                if (!calibrator.Running)
                    SaveCalibration();
            }
        }

        if (HasListeners())
            Publish(Events.Fans, frame);
    }

    private void OnShutdownFans()
    {
        if (_calibrator is { Running: true } && _fanEngine is not null)
            _calibrator.Cancel("el servicio se detiene", _fanEngine);

        _fanEngine?.ReleaseAll(_backend.FanOutputs);
    }

    /// <summary>Ha aparecido una bomba nueva: se revalida la configuración para aplicar su protección.</summary>
    private void OnCatalogRolesChanged()
    {
        if (_fanSettings is { } current && _fanEngine is not null)
            ApplyFanSettings(current, save: true);
    }
}
