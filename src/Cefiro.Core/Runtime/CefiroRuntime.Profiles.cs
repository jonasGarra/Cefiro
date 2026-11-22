using System.Diagnostics;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging;

namespace Cefiro.Core.Runtime;

/// <summary>Lo que se guarda en perfiles.json.</summary>
internal sealed record ProfilesStore
{
    public List<ProfileDto> Profiles { get; init; } = [];
    public string? ActiveProfileId { get; init; }
    public bool AutoSwitchEnabled { get; init; } = true;
}

/// <summary>
/// Perfiles (ventiladores + luces) y cambio automático al abrir un programa. Al cerrarse el
/// programa se vuelve exactamente a lo que había antes. Si mientras tanto eliges otro perfil a
/// mano, se respeta tu elección hasta que el programa se cierre.
/// </summary>
public sealed partial class CefiroRuntime
{
    private const string ProfilesFile = "perfiles.json";
    private const int MaxProfiles = 64;

    private readonly object _profilesGate = new();
    private List<ProfileDto> _profiles = [];
    private string? _activeProfileId;
    private string? _activatedBy;
    private bool _autoSwitch = true;
    private bool _manualOverride;
    private AutoSnapshot? _beforeAuto;
    private DateTime _lastProgramScan = DateTime.MinValue;
    private int _programScanBusy;

    /// <summary>Configuración que había antes del primer cambio automático (para volver a ella).</summary>
    private sealed record AutoSnapshot(FanSettingsDto? Fans, LightingSettingsDto? Lighting, string? ProfileId);

    private string? ActiveProfileId
    {
        get
        {
            lock (_profilesGate)
                return _activeProfileId;
        }
    }

    private void RegisterProfileRoutes()
    {
        Route<object, ProfilesStateDto>(Methods.GetProfiles, (_, _) => Task.FromResult(ProfilesState()));

        Route<SaveProfileRequest, ProfilesStateDto>(Methods.SaveProfile, (request, _) =>
            Task.FromResult(SaveProfile(request ?? throw new ArgumentException("Petición vacía."))));

        Route<ProfileIdRequest, ProfilesStateDto>(Methods.DeleteProfile, (request, _) =>
            Task.FromResult(DeleteProfile(request?.Id ?? throw new ArgumentException("Falta el perfil."))));

        Route<ProfileIdRequest, ProfilesStateDto>(Methods.ActivateProfile, async (request, _) =>
        {
            var profile = FindProfile(request?.Id) ?? throw new ArgumentException("Ese perfil no existe.");
            lock (_profilesGate)
            {
                // Elegido a mano: manda sobre el cambio automático mientras siga abierto el programa.
                _manualOverride = _activatedBy is not null || _manualOverride;
                _beforeAuto = null;
            }

            await ActivateProfileAsync(profile, activatedBy: null).ConfigureAwait(false);
            return ProfilesState();
        });

        Route<object, ServiceSettingsDto>(Methods.GetSettings, (_, _) => Task.FromResult(ServiceSettings()));

        Route<ServiceSettingsDto, ServiceSettingsDto>(Methods.SetSettings, (request, _) =>
        {
            lock (_profilesGate)
                _autoSwitch = request?.AutoSwitchProfiles ?? true;
            SaveProfiles();
            PublishProfiles();
            return Task.FromResult(ServiceSettings());
        });
    }

    private ServiceSettingsDto ServiceSettings()
    {
        lock (_profilesGate)
            return new ServiceSettingsDto { AutoSwitchProfiles = _autoSwitch };
    }

    private void LoadProfiles()
    {
        var store = _store.Load(ProfilesFile, () => new ProfilesStore());
        lock (_profilesGate)
        {
            _profiles = (store.Profiles ?? []).Where(p => p?.Id is not null).Take(MaxProfiles).ToList();
            _activeProfileId = _profiles.Any(p => p.Id == store.ActiveProfileId) ? store.ActiveProfileId : null;
            _autoSwitch = store.AutoSwitchEnabled;
        }
    }

    private void SaveProfiles()
    {
        ProfilesStore snapshot;
        lock (_profilesGate)
            snapshot = new ProfilesStore { Profiles = [.. _profiles], ActiveProfileId = _activeProfileId, AutoSwitchEnabled = _autoSwitch };

        try
        {
            _store.Save(ProfilesFile, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudieron guardar los perfiles");
        }
    }

    internal ProfilesStateDto ProfilesState()
    {
        lock (_profilesGate)
            return new ProfilesStateDto([.. _profiles], _activeProfileId, _activatedBy, _autoSwitch);
    }

    private void PublishProfiles() => Publish(Events.Profiles, ProfilesState());

    private ProfileDto? FindProfile(string? id)
    {
        lock (_profilesGate)
            return _profiles.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>"Juego.EXE", "C:\juegos\juego.exe" o "juego" → "juego.exe".</summary>
    internal static string NormalizeProgram(string program)
    {
        var name = Path.GetFileName(program.Trim().Trim('"')).ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }

    internal ProfilesStateDto SaveProfile(SaveProfileRequest request)
    {
        var given = request.Profile ?? throw new ArgumentException("Falta el perfil.");
        var name = string.IsNullOrWhiteSpace(given.Name) ? "Perfil" : given.Name.Trim();
        if (name.Length > 40)
            name = name[..40];

        var fans = given.Fans;
        var lighting = given.Lighting;
        if (request.CaptureCurrent)
        {
            fans = _fanSettings;
            lighting = _lightingEngine?.Settings ?? lighting;
        }

        var programs = (given.Programs ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeProgram)
            .Where(p => p.Length > 4)
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();

        var hotkey = string.IsNullOrWhiteSpace(given.Hotkey) ? null : given.Hotkey.Trim();
        if (hotkey is { Length: > 40 })
            hotkey = hotkey[..40];

        var glyph = string.IsNullOrWhiteSpace(given.Glyph) || given.Glyph.Length > 40 ? "Layer24" : given.Glyph;

        lock (_profilesGate)
        {
            var id = string.IsNullOrWhiteSpace(given.Id) || given.Id.Length > 64 ? NewProfileId() : given.Id.Trim();
            var profile = new ProfileDto
            {
                Id = id,
                Name = name,
                Glyph = glyph,
                Fans = fans,
                Lighting = lighting,
                Hotkey = hotkey,
                Programs = programs,
            };

            var index = _profiles.FindIndex(p => p.Id == id);
            if (index >= 0)
                _profiles[index] = profile;
            else if (_profiles.Count >= MaxProfiles)
                throw new InvalidOperationException($"Como mucho {MaxProfiles} perfiles.");
            else
                _profiles.Add(profile);

            // Un atajo solo puede tenerlo un perfil.
            if (hotkey is not null)
            {
                for (var i = 0; i < _profiles.Count; i++)
                {
                    if (_profiles[i].Id != id && string.Equals(_profiles[i].Hotkey, hotkey, StringComparison.OrdinalIgnoreCase))
                        _profiles[i] = _profiles[i] with { Hotkey = null };
                }
            }

            _logger.LogInformation("Perfil guardado: {Name} ({Id})", name, id);
        }

        SaveProfiles();
        PublishProfiles();
        return ProfilesState();
    }

    private static string NewProfileId() => "p" + Guid.NewGuid().ToString("N")[..10];

    internal ProfilesStateDto DeleteProfile(string id)
    {
        lock (_profilesGate)
        {
            if (_profiles.RemoveAll(p => p.Id == id) == 0)
                throw new ArgumentException("Ese perfil no existe.");

            if (_activeProfileId == id)
            {
                _activeProfileId = null;
                _activatedBy = null;
            }
        }

        SaveProfiles();
        PublishProfiles();
        return ProfilesState();
    }

    /// <summary>Aplica los ventiladores y las luces de un perfil (validados como siempre).</summary>
    private async Task ActivateProfileAsync(ProfileDto profile, string? activatedBy)
    {
        await ApplySettingsAsync(profile.Fans, profile.Lighting).ConfigureAwait(false);

        lock (_profilesGate)
        {
            _activeProfileId = profile.Id;
            _activatedBy = activatedBy;
        }

        _logger.LogInformation("Perfil activado: {Name}{By}", profile.Name, activatedBy is null ? "" : $" (al abrir {activatedBy})");
        SaveProfiles();
        PublishProfiles();
    }

    private async Task ApplySettingsAsync(FanSettingsDto? fans, LightingSettingsDto? lighting)
    {
        if (fans is not null && _fanSettings is { } current)
        {
            try
            {
                // La seguridad es común a todos los perfiles: se conserva la actual.
                await RunOnHardwareThreadAsync(() => ApplyFanSettings(fans with { Safety = current.Safety }, save: true)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudieron aplicar los ventiladores del perfil");
            }
        }

        if (lighting is not null && _lightingEngine is { } engine)
        {
            try
            {
                // Tamaños de zona y favoritos son del PC, no del perfil.
                var now = engine.Settings;
                ApplyLightingSettings(lighting with { ZoneSizes = now.ZoneSizes, Favorites = now.Favorites }, save: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudieron aplicar las luces del perfil");
            }
        }
    }

    /// <summary>Se llama en cada ciclo: lanza la búsqueda de programas cada pocos segundos.</summary>
    private void MaybeScanPrograms()
    {
        if (DateTime.UtcNow - _lastProgramScan < _options.ProgramScanInterval)
            return;

        _lastProgramScan = DateTime.UtcNow;
        bool needed;
        lock (_profilesGate)
            needed = _activatedBy is not null || _manualOverride || (_autoSwitch && _profiles.Any(p => p.Programs.Count > 0));

        if (needed && Interlocked.CompareExchange(ref _programScanBusy, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try { await ScanProgramsAsync().ConfigureAwait(false); }
                finally { Interlocked.Exchange(ref _programScanBusy, 0); }
            });
        }
    }

    private static IReadOnlySet<string> RealRunningPrograms()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                names.Add(process.ProcessName.ToLowerInvariant() + ".exe");
            }
            catch
            {
                // El proceso ha terminado mientras se leía.
            }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }

    /// <summary>Una pasada del cambio automático de perfil.</summary>
    internal async Task ScanProgramsAsync()
    {
        try
        {
            List<ProfileDto> profiles;
            string? active;
            string? activatedBy;
            bool auto;
            lock (_profilesGate)
            {
                profiles = [.. _profiles];
                active = _activeProfileId;
                activatedBy = _activatedBy;
                auto = _autoSwitch;
            }

            ProfileDto? match = null;
            string? program = null;
            if (auto)
            {
                var running = (_options.RunningPrograms ?? RealRunningPrograms)();
                foreach (var p in profiles)
                {
                    program = p.Programs.FirstOrDefault(exe => running.Contains(NormalizeProgram(exe)));
                    if (program is not null)
                    {
                        match = p;
                        break;
                    }
                }
            }

            if (match is null)
            {
                lock (_profilesGate)
                    _manualOverride = false;

                if (activatedBy is not null)
                    await RestoreAfterAutoAsync().ConfigureAwait(false);
                return;
            }

            lock (_profilesGate)
            {
                if (_manualOverride || active == match.Id)
                    return;

                // Primer cambio automático: se guarda lo que había para volver después.
                _beforeAuto ??= new AutoSnapshot(_fanSettings, _lightingEngine?.Settings, active);
            }

            await ActivateProfileAsync(match, program).ConfigureAwait(false);
            PublishAlert(AlertKind.Info, $"Perfil «{match.Name}» activado", $"Se ha abierto {program}. Al cerrarlo volverá la configuración anterior.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error en el cambio automático de perfil");
        }
    }

    private async Task RestoreAfterAutoAsync()
    {
        AutoSnapshot? before;
        lock (_profilesGate)
        {
            before = _beforeAuto;
            _beforeAuto = null;
        }

        if (before is not null)
            await ApplySettingsAsync(before.Fans, before.Lighting).ConfigureAwait(false);

        lock (_profilesGate)
        {
            _activeProfileId = before?.ProfileId is { } id && _profiles.Any(p => p.Id == id) ? id : null;
            _activatedBy = null;
        }

        _logger.LogInformation("Programa cerrado: se vuelve a la configuración anterior");
        SaveProfiles();
        PublishProfiles();
        PublishAlert(AlertKind.Info, "Configuración restaurada", "Se ha cerrado el programa: vuelven los ventiladores y las luces que tenías antes.");
    }

    // ------------------------------------------------------------------ avisos de temperatura

    private bool _cpuWarned;
    private bool _gpuWarned;
    private DateTime _lastCpuWarning = DateTime.MinValue;
    private DateTime _lastGpuWarning = DateTime.MinValue;

    /// <summary>
    /// Aviso (no emergencia) cuando la CPU o la GPU pasan su temperatura de aviso. Se rearma al bajar
    /// 5 °C y no se repite más de una vez cada 10 minutos.
    /// </summary>
    private void CheckTemperatureWarnings()
    {
        if (_fanSettings?.Safety is not { } safety || _catalog is not { } catalog)
            return;

        CheckWarning(catalog.Keys.CpuTemperature, safety.CpuWarningCelsius, "La CPU", ref _cpuWarned, ref _lastCpuWarning);
        CheckWarning(catalog.Keys.GpuTemperature, safety.GpuWarningCelsius, "La gráfica", ref _gpuWarned, ref _lastGpuWarning);
    }

    private void CheckWarning(string? sensorId, float limit, string what, ref bool warned, ref DateTime last)
    {
        if (sensorId is null || ReadSensor(sensorId) is not { } t)
            return;

        if (!warned && t >= limit && DateTime.UtcNow - last > TimeSpan.FromMinutes(10))
        {
            warned = true;
            last = DateTime.UtcNow;
            _logger.LogWarning("{What} ha llegado a {Temperature:0} °C (aviso a {Limit:0} °C)", what, t, limit);
            PublishAlert(AlertKind.TemperatureHigh, $"{what} está a {t:0} °C",
                $"Ha pasado del aviso de {limit:0} °C. Si llega a la temperatura crítica, todos los ventiladores se pondrán al 100 %.");
        }
        else if (warned && t <= limit - 5)
        {
            warned = false;
        }
    }
}
