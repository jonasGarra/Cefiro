using System.IO;
using System.Windows.Threading;
using Cefiro.Core.Runtime;
using Cefiro.Demo;
using Cefiro.Ipc;
using Cefiro.Ipc.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cefiro.App.Infrastructure;

/// <summary>
/// Sesión con el núcleo de Céfiro. Guarda una copia de todo lo que la interfaz necesita y traslada
/// los eventos al hilo de la interfaz. Al reconectar (p. ej. tras reiniciar el servicio) lo recarga todo.
/// </summary>
public sealed partial class CefiroSession : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly CefiroRuntime? _demoRuntime;
    private Dictionary<string, int> _index = new(StringComparer.Ordinal);

    private CefiroSession(CefiroApi api, Dispatcher dispatcher, CefiroRuntime? demoRuntime)
    {
        Api = api;
        _dispatcher = dispatcher;
        _demoRuntime = demoRuntime;
        IsDemo = demoRuntime is not null;

        api.Connection.ConnectionChanged += (_, connected) => Post(() => OnConnectionChanged(connected));
        api.SensorFrame += (_, frame) => Post(() => OnSensorFrame(frame));
        api.FanFrame += (_, frame) => Post(() => { FanFrame = frame; FansUpdated?.Invoke(); });
        api.StatusChanged += (_, status) => Post(() => Status = status);
        api.Alert += (_, alert) => Post(() => AlertReceived?.Invoke(alert));
        api.CalibrationChanged += (_, status) => Post(() => CalibrationUpdated?.Invoke(status));
        api.OtherEvent += (_, e) =>
        {
            if (e.Name == Events.Lighting && CefiroJson.FromElement<LightingFrameDto>(e.Payload) is { } frame)
                Post(() => { LightingFrame = frame; LightingFrameUpdated?.Invoke(); });
            else if (e.Name == Events.Profiles && CefiroJson.FromElement<ProfilesStateDto>(e.Payload) is { } profiles)
                Post(() => OnProfiles(profiles));
        };
    }

    /// <summary>Configuración del modo demo (separada de la real).</summary>
    public static string DemoDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cefiro", "demo-interfaz");

    public static async Task<CefiroSession> CreateAsync(bool demo, string? pipeName, Dispatcher dispatcher)
    {
        if (demo)
        {
            var options = new RuntimeOptions
            {
                DemoMode = true,
                DataDirectory = DemoDataDirectory,
            };
            var runtime = new CefiroRuntime(new DemoHardwareBackend(), new DemoLightingBackend(), options, NullLogger<CefiroRuntime>.Instance);
            var api = new CefiroApi(new InProcessConnection(runtime, runtime));
            var session = new CefiroSession(api, dispatcher, runtime);
            await api.StartAsync().ConfigureAwait(true);
            await runtime.StartAsync().ConfigureAwait(true);
            return session;
        }

        var connection = new PipeConnection(pipeName ?? IpcConstants.PipeName);
        var real = new CefiroSession(new CefiroApi(connection), dispatcher, null);
        await real.Api.StartAsync().ConfigureAwait(true);
        return real;
    }

    public CefiroApi Api { get; }
    public bool IsDemo { get; }

    [ObservableProperty] public partial bool IsConnected { get; private set; }
    [ObservableProperty] public partial bool IsLoaded { get; private set; }
    [ObservableProperty] public partial StatusDto? Status { get; private set; }
    [ObservableProperty] public partial string? ServiceVersion { get; private set; }

    public CatalogDto? Catalog { get; private set; }
    public float?[] Values { get; private set; } = [];
    public FanSettingsDto? FanSettings { get; private set; }
    public FanFrameDto? FanFrame { get; private set; }
    public LightingStateDto? Lighting { get; private set; }
    public LightingFrameDto? LightingFrame { get; private set; }
    public ProfilesStateDto? Profiles { get; private set; }
    public ServiceSettingsDto? ServiceSettings { get; private set; }

    /// <summary>Han cambiado los perfiles o el perfil activo.</summary>
    public event Action? ProfilesChanged;

    /// <summary>Se ha cargado (o recargado) el catálogo y la configuración.</summary>
    public event Action? Loaded;

    public event Action? SensorsUpdated;
    public event Action? FansUpdated;
    public event Action? FanSettingsChanged;
    public event Action? LightingChanged;
    public event Action? LightingFrameUpdated;
    public event Action<AlertDto>? AlertReceived;
    public event Action<CalibrationStatusDto>? CalibrationUpdated;

    public float? Value(string? sensorId) =>
        sensorId is not null && _index.TryGetValue(sensorId, out var i) && i < Values.Length ? Values[i] : null;

    public SensorDto? Sensor(string? id) => id is null ? null : Catalog?.Sensors.FirstOrDefault(s => s.Id == id);

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;
        if (connected)
            _ = ReloadAsync();
        else
            IsLoaded = false;
    }

    public async Task ReloadAsync()
    {
        try
        {
            var hello = await Api.HelloAsync("Céfiro").ConfigureAwait(true);
            ServiceVersion = hello.ServiceVersion;

            // El servicio puede estar todavía abriendo el hardware: se reintenta unos segundos.
            CatalogDto? catalog = null;
            for (var attempt = 0; attempt < 40 && catalog is null; attempt++)
            {
                try { catalog = await Api.GetCatalogAsync().ConfigureAwait(true); }
                catch (IpcRemoteException) { await Task.Delay(500).ConfigureAwait(true); }
            }

            if (catalog is null)
                return;

            Catalog = catalog;
            _index = catalog.Sensors.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
            Values = new float?[catalog.Sensors.Count];

            try { FanSettings = await Api.GetFanSettingsAsync().ConfigureAwait(true); } catch (IpcRemoteException) { }
            try { Lighting = await Api.CallAsync<LightingStateDto>(Methods.GetLighting, null).ConfigureAwait(true); } catch (IpcRemoteException) { }
            try { Status = await Api.GetStatusAsync().ConfigureAwait(true); } catch (IpcRemoteException) { }
            try { Profiles = await Api.CallAsync<ProfilesStateDto>(Methods.GetProfiles, null).ConfigureAwait(true); } catch (IpcRemoteException) { }
            try { ServiceSettings = await Api.CallAsync<ServiceSettingsDto>(Methods.GetSettings, null).ConfigureAwait(true); } catch (IpcRemoteException) { }

            IsLoaded = true;
            Loaded?.Invoke();
            FanSettingsChanged?.Invoke();
            LightingChanged?.Invoke();
            ProfilesChanged?.Invoke();
        }
        catch (Exception)
        {
            // Se reintentará en la próxima reconexión.
        }
    }

    private void OnSensorFrame(SensorFrameDto frame)
    {
        if (Catalog is null)
            return;

        if (frame.CatalogVersion != Catalog.Version)
        {
            // El servicio ha rehecho el catálogo (p. ej. ha detectado la bomba): se recarga.
            _ = ReloadAsync();
            return;
        }

        Values = frame.Values;
        SensorsUpdated?.Invoke();
    }

    public async Task<FanSettingsDto> SaveFanSettingsAsync(FanSettingsDto settings)
    {
        var applied = await Api.SetFanSettingsAsync(settings).ConfigureAwait(true);
        FanSettings = applied;
        FanSettingsChanged?.Invoke();
        return applied;
    }

    /// <summary>
    /// Salidas a las que se aplican los modos rápidos (las que tienen algo conectado). La página de
    /// ventiladores la mantiene al día; si es null se aplican a todas.
    /// </summary>
    public IReadOnlyList<string>? FanTargets { get; set; }

    public async Task<FanSettingsDto> ApplyPresetAsync(FanPreset preset, IReadOnlyList<string>? channelIds = null)
    {
        var applied = await Api.ApplyFanPresetAsync(new ApplyPresetRequest(preset, channelIds ?? FanTargets)).ConfigureAwait(true);
        FanSettings = applied;
        FanSettingsChanged?.Invoke();
        return applied;
    }

    public async Task<LightingStateDto> SaveLightingAsync(LightingSettingsDto settings)
    {
        var state = await Api.CallAsync<LightingStateDto>(Methods.SetLighting, settings).ConfigureAwait(true);
        Lighting = state;
        LightingChanged?.Invoke();
        return state;
    }

    public async Task RefreshLightingAsync()
    {
        Lighting = await Api.CallAsync<LightingStateDto>(Methods.GetLighting, null).ConfigureAwait(true);
        LightingChanged?.Invoke();
    }

    // ------------------------------------------------------------------ perfiles

    private void OnProfiles(ProfilesStateDto state)
    {
        var activeChanged = state.ActiveProfileId != Profiles?.ActiveProfileId || state.ActivatedBy != Profiles?.ActivatedBy;
        Profiles = state;
        ProfilesChanged?.Invoke();

        // Al activarse un perfil cambian los ventiladores y las luces: se recargan.
        if (activeChanged)
            _ = ReloadSettingsAsync();
    }

    private async Task ReloadSettingsAsync()
    {
        try
        {
            FanSettings = await Api.GetFanSettingsAsync().ConfigureAwait(true);
            FanSettingsChanged?.Invoke();
            await RefreshLightingAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IpcRemoteException or InvalidOperationException or IOException or OperationCanceledException)
        {
            // Se recargará en la próxima reconexión.
        }
    }

    private ProfilesStateDto Apply(ProfilesStateDto state)
    {
        OnProfiles(state);
        return state;
    }

    public async Task<ProfilesStateDto> SaveProfileAsync(ProfileDto profile, bool captureCurrent) =>
        Apply(await Api.CallAsync<ProfilesStateDto>(Methods.SaveProfile, new SaveProfileRequest(profile, captureCurrent)).ConfigureAwait(true));

    public async Task<ProfilesStateDto> DeleteProfileAsync(string id) =>
        Apply(await Api.CallAsync<ProfilesStateDto>(Methods.DeleteProfile, new ProfileIdRequest(id)).ConfigureAwait(true));

    public async Task<ProfilesStateDto> ActivateProfileAsync(string id) =>
        Apply(await Api.CallAsync<ProfilesStateDto>(Methods.ActivateProfile, new ProfileIdRequest(id)).ConfigureAwait(true));

    public async Task<ServiceSettingsDto> SaveServiceSettingsAsync(ServiceSettingsDto settings)
    {
        ServiceSettings = await Api.CallAsync<ServiceSettingsDto>(Methods.SetSettings, settings).ConfigureAwait(true);
        return ServiceSettings;
    }

    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync().ConfigureAwait(false);
        if (_demoRuntime is not null)
            await _demoRuntime.DisposeAsync().ConfigureAwait(false);
    }
}
