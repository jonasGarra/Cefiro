using Cefiro.Core.Runtime;
using Cefiro.Demo;
using Cefiro.Ipc;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cefiro.Core.Tests;

public class ProfileTests
{
    private static RuntimeOptions Options(Func<IReadOnlySet<string>> programs) => new()
    {
        DemoMode = true,
        DataDirectory = Path.Combine(Path.GetTempPath(), "cefiro-tests", Guid.NewGuid().ToString("N")),
        TickInterval = TimeSpan.FromMilliseconds(100),
        ProgramScanInterval = TimeSpan.FromHours(1), // la prueba lanza la búsqueda a mano
        RunningPrograms = programs,
    };

    [Fact]
    public async Task Profiles_capture_activate_and_switch_automatically_when_a_program_opens_and_closes()
    {
        var ct = TestContext.Current.CancellationToken;
        var running = new HashSet<string>(StringComparer.Ordinal);
        await using var runtime = new CefiroRuntime(new DemoHardwareBackend(), new DemoLightingBackend(), Options(() => running), NullLogger<CefiroRuntime>.Instance);
        await using var api = new CefiroApi(new InProcessConnection(runtime, runtime));
        await api.StartAsync(ct);
        await runtime.StartAsync(ct);

        // "Silencio": lo que hay al arrancar (todo en BIOS).
        var state = await api.CallAsync<ProfilesStateDto>(Methods.SaveProfile,
            new SaveProfileRequest(new ProfileDto { Id = "", Name = "Silencio" }, CaptureCurrent: true), ct);
        var quiet = Assert.Single(state.Profiles);
        Assert.All(quiet.Fans!.Channels, c => Assert.Equal(FanMode.Bios, c.Mode));

        // "Juegos": Máximo + arcoíris, asociado a juego.exe.
        await api.ApplyFanPresetAsync(new ApplyPresetRequest(FanPreset.Maximum, null), ct);
        var lighting = await api.CallAsync<LightingStateDto>(Methods.GetLighting, null, ct);
        await api.CallAsync<LightingStateDto>(Methods.SetLighting,
            lighting.Settings with { SyncEffect = new EffectDto { Kind = EffectKind.Rainbow } }, ct);
        state = await api.CallAsync<ProfilesStateDto>(Methods.SaveProfile,
            new SaveProfileRequest(new ProfileDto { Id = "", Name = "Juegos", Programs = [@"C:\Juegos\Juego.EXE"], Hotkey = "Ctrl+Alt+J" }, true), ct);
        var games = state.Profiles.Single(p => p.Name == "Juegos");
        Assert.Equal(["juego.exe"], games.Programs);

        // A mano: Silencio.
        state = await api.CallAsync<ProfilesStateDto>(Methods.ActivateProfile, new ProfileIdRequest(quiet.Id), ct);
        Assert.Equal(quiet.Id, state.ActiveProfileId);
        Assert.All((await api.GetFanSettingsAsync(ct)).Channels, c => Assert.Equal(FanMode.Bios, c.Mode));

        // Se abre el juego: Juegos.
        running.Add("juego.exe");
        await runtime.ScanProgramsAsync();
        state = runtime.ProfilesState();
        Assert.Equal(games.Id, state.ActiveProfileId);
        Assert.Equal("juego.exe", state.ActivatedBy);
        Assert.Contains((await api.GetFanSettingsAsync(ct)).Channels, c => c is { Mode: FanMode.Fixed, FixedPercent: 100 });
        lighting = await api.CallAsync<LightingStateDto>(Methods.GetLighting, null, ct);
        Assert.Equal(EffectKind.Rainbow, lighting.Settings.SyncEffect.Kind);

        // Se cierra: vuelve exactamente lo de antes.
        running.Clear();
        await runtime.ScanProgramsAsync();
        state = runtime.ProfilesState();
        Assert.Equal(quiet.Id, state.ActiveProfileId);
        Assert.Null(state.ActivatedBy);
        Assert.All((await api.GetFanSettingsAsync(ct)).Channels, c => Assert.Equal(FanMode.Bios, c.Mode));

        // Con el juego abierto se elige otro perfil a mano: se respeta hasta que se cierre.
        running.Add("juego.exe");
        await runtime.ScanProgramsAsync();
        Assert.Equal(games.Id, runtime.ProfilesState().ActiveProfileId);
        await api.CallAsync<ProfilesStateDto>(Methods.ActivateProfile, new ProfileIdRequest(quiet.Id), ct);
        await runtime.ScanProgramsAsync();
        Assert.Equal(quiet.Id, runtime.ProfilesState().ActiveProfileId);
        running.Clear();
        await runtime.ScanProgramsAsync();
        Assert.Equal(quiet.Id, runtime.ProfilesState().ActiveProfileId);

        // Con el cambio automático desactivado no se cambia.
        await api.CallAsync<ServiceSettingsDto>(Methods.SetSettings, new ServiceSettingsDto { AutoSwitchProfiles = false }, ct);
        running.Add("juego.exe");
        await runtime.ScanProgramsAsync();
        Assert.Equal(quiet.Id, runtime.ProfilesState().ActiveProfileId);

        // Borrar el perfil activo deja "sin perfil".
        state = await api.CallAsync<ProfilesStateDto>(Methods.DeleteProfile, new ProfileIdRequest(quiet.Id), ct);
        Assert.Null(state.ActiveProfileId);
        Assert.Single(state.Profiles);
    }

    [Fact]
    public async Task An_imported_profile_with_unsafe_values_is_corrected_before_touching_the_hardware()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = new CefiroRuntime(new DemoHardwareBackend(), new DemoLightingBackend(),
            Options(() => new HashSet<string>()), NullLogger<CefiroRuntime>.Instance);
        await using var api = new CefiroApi(new InProcessConnection(runtime, runtime));
        await api.StartAsync(ct);
        await runtime.StartAsync(ct);

        var catalog = await api.GetCatalogAsync(ct);
        var pump = catalog.FanChannels.Single(c => c.DetectedRole == FanRole.Pump);
        var before = await api.GetFanSettingsAsync(ct);

        // Perfil "importado" que intenta parar la bomba, toca una salida que no existe y sube el límite crítico.
        var bad = new ProfileDto
        {
            Id = "importado",
            Name = "Importado",
            Fans = new FanSettingsDto(
                [
                    new FanChannelSettingsDto { ChannelId = pump.Id, Name = "Bomba", Role = FanRole.Case, Mode = FanMode.Fixed, FixedPercent = 0, AllowStop = true },
                    new FanChannelSettingsDto { ChannelId = "/no/existe", Name = "Fantasma", Mode = FanMode.Fixed, FixedPercent = 0 },
                ],
                [],
                new SafetySettingsDto { CpuCriticalCelsius = 200 }),
        };
        await api.CallAsync<ProfilesStateDto>(Methods.SaveProfile, new SaveProfileRequest(bad, CaptureCurrent: false), ct);
        await api.CallAsync<ProfilesStateDto>(Methods.ActivateProfile, new ProfileIdRequest("importado"), ct);

        var fans = await api.GetFanSettingsAsync(ct);
        var p = fans.Channels.Single(c => c.ChannelId == pump.Id);
        Assert.Equal(FanRole.Pump, p.Role);
        Assert.True(p.FixedPercent >= 60, $"bomba al {p.FixedPercent} %");
        Assert.False(p.AllowStop);
        Assert.DoesNotContain(fans.Channels, c => c.ChannelId == "/no/existe");
        Assert.Equal(before.Safety, fans.Safety);
    }

    [Fact]
    public async Task Rescan_request_asks_the_rgb_server_to_detect_devices_again_and_resends_everything()
    {
        var ct = TestContext.Current.CancellationToken;
        var lights = new DemoLightingBackend();
        await using var runtime = new CefiroRuntime(new DemoHardwareBackend(), lights, Options(() => new HashSet<string>()), NullLogger<CefiroRuntime>.Instance);
        await using var api = new CefiroApi(new InProcessConnection(runtime, runtime));
        await api.StartAsync(ct);
        await runtime.StartAsync(ct);
        await Task.Delay(500, ct);

        var sentBefore = lights.SendCount;
        await api.CallAsync(Methods.RescanLighting, null, ct);
        for (var i = 0; i < 40 && lights.RescanCount == 0; i++)
            await Task.Delay(50, ct);
        await Task.Delay(600, ct);

        Assert.Equal(1, lights.RescanCount);
        Assert.True(lights.SendCount > sentBefore, "tras redetectar se tienen que reenviar los colores (aunque sean fijos)");
    }

    [Theory]
    [InlineData("Juego.EXE", "juego.exe")]
    [InlineData(@"C:\Program Files\Steam\steam.exe", "steam.exe")]
    [InlineData("\"obs64\"", "obs64.exe")]
    public void Program_names_are_normalized(string input, string expected) =>
        Assert.Equal(expected, CefiroRuntime.NormalizeProgram(input));
}
