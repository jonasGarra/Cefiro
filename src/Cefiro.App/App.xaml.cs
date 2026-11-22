using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Cefiro.App.Infrastructure;
using Cefiro.App.Services;
using Cefiro.App.ViewModels;
using Cefiro.App.Views;
using Cefiro.Ipc.Contracts;

namespace Cefiro.App;

/// <summary>
/// Arranque de la interfaz.
///   Cefiro.exe                → normal (se conecta al servicio)
///   Cefiro.exe --minimized    → arranca en la bandeja (inicio con Windows)
///   Cefiro.exe --demo         → hardware simulado, sin servicio
///   Cefiro.exe --demo --capture &lt;carpeta&gt; → guarda una imagen de cada página y se cierra
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private CefiroSession? _session;
    private MainWindow? _main;
    private MainViewModel? _vm;
    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private UiSettingsStore? _settings;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var culture = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => UiLog.Error("Excepción no controlada", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { UiLog.Error("Excepción no observada", ev.Exception); ev.SetObserved(); };

        var args = e.Args;
        var demo = args.Contains("--demo");
        var minimized = args.Contains("--minimized");
        var capture = GetArg(args, "--capture");
        var pipe = GetArg(args, "--pipe");

        // Una sola instancia: si ya hay una abierta, se le pide que se muestre y esta se cierra.
        if (capture is null)
        {
            var name = demo ? @"Local\Cefiro.App.Demo" : @"Local\Cefiro.App";
            _singleInstance = new Mutex(true, name, out var isFirst);
            if (!isFirst)
            {
                try { EventWaitHandle.OpenExisting(name + ".Show").Set(); } catch { /* la otra se está cerrando */ }
                Shutdown();
                return;
            }

            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show");
            var listener = new Thread(() =>
            {
                while (_showSignal.WaitOne())
                {
                    if (_exiting) return;
                    Dispatcher.BeginInvoke(() => _main?.ShowFromTray());
                }
            }) { IsBackground = true, Name = "Céfiro · mostrar ventana" };
            listener.Start();
        }

        _settings = new UiSettingsStore();
        Format.UseFahrenheit = _settings.Current.UseFahrenheit;
        ThemeService.ApplyAccent(
            ThemeService.Parse(_settings.Current.AccentHex, System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xFF)),
            ThemeService.Parse(_settings.Current.Accent2Hex, System.Windows.Media.Color.FromRgb(0x7C, 0x4D, 0xFF)));

        SplashWindow? splash = null;
        if (!minimized)
        {
            splash = new SplashWindow();
            splash.SetStatus(demo ? "Preparando el modo demo…" : "Conectando con el servicio de Céfiro…");
            splash.Show();
        }

        // Las capturas siempre parten de la configuración de fábrica del modo demo.
        if (capture is not null && demo)
        {
            try { Directory.Delete(CefiroSession.DemoDataDirectory, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) { UiLog.Error("No se pudo limpiar la carpeta del modo demo", ex); }
        }

        try
        {
            _session = await CefiroSession.CreateAsync(demo, pipe, Dispatcher);
        }
        catch (Exception ex)
        {
            UiLog.Error("No se pudo iniciar la sesión", ex);
            splash?.Close();
            MessageBox.Show($"No se pudo iniciar Céfiro:\n\n{ex.Message}", "Céfiro", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _vm = new MainViewModel(_session, _settings);
        _main = new MainWindow(_vm) { CloseToTray = _settings.Current.CloseToTray && capture is null };
        MainWindow = _main;
        _settings.Changed += s =>
        {
            _main.CloseToTray = s.CloseToTray;
            Format.UseFahrenheit = s.UseFahrenheit;
        };
        _main.HiddenToTray += OnHiddenToTray;

        if (capture is null)
        {
            _tray = new TrayService(
                showWindow: () => _main.ShowFromTray(),
                exit: () => _ = ExitAsync(),
                applyPreset: async preset => await SafeAsync(() => _session.ApplyPresetAsync(preset)),
                setLightsEnabled: enabled =>
                {
                    _vm.Lighting.Enabled = enabled;
                    return Task.CompletedTask;
                },
                lightsEnabled: () => _vm.Lighting.Enabled);
            Notifier.Handler = (title, message, warning) => _tray?.Notify(title, message, warning);

            // Atajos de teclado globales de los perfiles.
            try
            {
                _hotkeys = new HotkeyService(_main);
                _hotkeys.ProfileHotkeyPressed += id => _ = ActivateProfileAsync(id, fromHotkey: true);
            }
            catch (Exception ex)
            {
                UiLog.Error("No se pudieron preparar los atajos de teclado", ex);
            }

            _session.ProfilesChanged += OnProfilesChanged;
            OnProfilesChanged();
        }

        _session.AlertReceived += OnAlert;

        // Se espera a tener datos (como mucho unos segundos) para no enseñar una ventana vacía.
        for (var i = 0; i < 60 && !_session.IsLoaded; i++)
        {
            if (i == 20)
                splash?.SetStatus("El servicio está abriendo el hardware…");
            await Task.Delay(100);
        }

        if (!minimized)
            _main.Show();
        splash?.FadeOutAndClose();

        if (capture is not null)
            await CaptureAsync(capture);
    }

    private static string? GetArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void OnHiddenToTray(object? sender, EventArgs e)
    {
        if (_settings is null || _settings.Current.TrayHintShown)
            return;

        _tray?.Notify("Céfiro sigue en marcha", "La ventana se ha ocultado en la bandeja. El servicio sigue controlando los ventiladores y las luces.");
        _settings.Update(s => s with { TrayHintShown = true });
    }

    /// <summary>Perfiles nuevos o cambiados: menú de la bandeja, atajos y texto del icono.</summary>
    private void OnProfilesChanged()
    {
        if (_session?.Profiles is not { } state)
            return;

        _tray?.SetProfiles(state.Profiles, state.ActiveProfileId, id => ActivateProfileAsync(id, fromHotkey: false));

        var active = state.Profiles.FirstOrDefault(p => p.Id == state.ActiveProfileId);
        _tray?.SetTooltip(active is null ? "Céfiro" : $"Céfiro · {active.Name}");

        if (_hotkeys is not null)
        {
            var failed = _hotkeys.Register(state.Profiles.Where(p => p.Hotkey is not null).Select(p => (p.Id, p.Hotkey!)));
            if (failed.Count > 0)
                _tray?.Notify("Atajo no disponible", $"Otro programa ya usa {string.Join(", ", failed)}. Elige otra combinación en Perfiles.", warning: true);
        }
    }

    private async Task ActivateProfileAsync(string id, bool fromHotkey)
    {
        try
        {
            var state = await _session!.ActivateProfileAsync(id);
            if (fromHotkey && state.Profiles.FirstOrDefault(p => p.Id == id) is { } profile && _settings?.Current.ToastsEnabled == true)
                _tray?.Notify($"Perfil «{profile.Name}»", "Ventiladores y luces cambiados.");
        }
        catch (Exception ex)
        {
            UiLog.Error("No se pudo activar el perfil", ex);
            _tray?.Notify("No se pudo activar el perfil", ex.Message, warning: true);
        }
    }

    private void OnAlert(AlertDto alert)
    {
        if (_settings?.Current.ToastsEnabled != true)
            return;

        var warning = alert.Kind is AlertKind.Emergency or AlertKind.FanStopped or AlertKind.SensorLost or AlertKind.TemperatureHigh or AlertKind.Conflict;
        _tray?.Notify(alert.Title, alert.Message, warning);
    }

    private static async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            UiLog.Error("Error en una acción de la bandeja", ex);
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        UiLog.Error("Error en la interfaz", e.Exception);
        e.Handled = true; // la interfaz no debe cerrarse por un error de presentación
    }

    /// <summary>Modo --capture: guarda una imagen de cada página para revisar el diseño y sale.</summary>
    private async Task CaptureAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await Task.Delay(6000); // que se llenen gráficas y medidores

        var pages = new[]
        {
            "panel", "panel-abajo", "ventiladores", "ventiladores-abajo", "ventiladores-editor", "ventiladores-equilibrado",
            "iluminacion", "iluminacion-abajo", "iluminacion-zonas", "sensores", "perfiles", "ajustes",
        };
        for (var i = 0; i < pages.Length; i++)
        {
            var page = pages[i];
            if (page == "perfiles")
            {
                // Prueba de uso real: dos perfiles, uno con programa y atajo, y activarlo.
                _vm!.Navigate("perfiles");
                await _vm.Profiles.NewProfileCommand.ExecuteAsync(null);
                await _vm.Profiles.NewProfileCommand.ExecuteAsync(null);
                if (_session!.Profiles?.Profiles.FirstOrDefault() is { } first)
                {
                    await _session.SaveProfileAsync(first with { Name = "Juegos", Programs = ["juego.exe", "steam.exe"], Hotkey = "Ctrl+Alt+1" }, captureCurrent: false);
                    await _session.ActivateProfileAsync(first.Id);
                }

                await Task.Delay(2200);
            }
            else if (page == "ventiladores-equilibrado")
            {
                // Prueba de uso real: aplicar un modo rápido y ver que las tarjetas pasan a curva.
                _vm!.Fans.CloseEditorCommand.Execute(null);
                await Task.Delay(300);
                ScrollCurrentPage(toEnd: false);
                await _vm.Fans.ApplyPresetCommand.ExecuteAsync("Balanced");
                await Task.Delay(2500);
            }
            else if (page == "iluminacion-zonas")
            {
                // Prueba de uso real: separar la sincronización y dar efectos distintos por zona y dispositivo.
                ScrollCurrentPage(toEnd: false);
                var l = _vm!.Lighting;
                l.SyncAll = false;
                if (l.Devices.FirstOrDefault()?.Zones.FirstOrDefault(z => z.Name == "JRAINBOW1") is { } zone)
                {
                    l.SelectZoneCommand.Execute(zone);
                    l.SelectEffectCommand.Execute(l.Effects.First(e => e.Kind == EffectKind.Rainbow));
                }

                if (l.Devices.Skip(1).FirstOrDefault() is { } ram)
                {
                    l.SelectDeviceCommand.Execute(ram);
                    l.SelectEffectCommand.Execute(l.Effects.First(e => e.Kind == EffectKind.Music));
                }

                await Task.Delay(2500);
            }
            else if (page == "ventiladores-editor")
            {
                // El editor de curva abierto sobre el primer ventilador visible.
                ScrollCurrentPage(toEnd: false);
                if (_vm!.Fans.VisibleChannels.FirstOrDefault() is { } first)
                    _vm.Fans.EditCommand.Execute(first);
                await Task.Delay(900);
            }
            else if (page.EndsWith("-abajo", StringComparison.Ordinal))
            {
                ScrollCurrentPage(toEnd: true);
                await Task.Delay(900);
            }
            else
            {
                _vm!.Fans.CloseEditorCommand.Execute(null);
                _vm.Navigate(page);
                await Task.Delay(2200);
            }

            try
            {
                _main!.CaptureTo(Path.Combine(directory, $"{i + 1}-{page}.png"));
            }
            catch (Exception ex)
            {
                UiLog.Error($"No se pudo capturar {page}", ex);
            }
        }

        await ExitAsync();
    }

    /// <summary>Desplaza la página actual arriba del todo o abajo del todo (solo para las capturas).</summary>
    private void ScrollCurrentPage(bool toEnd)
    {
        static T? Find<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;
                if (Find<T>(child) is { } nested)
                    return nested;
            }

            return null;
        }

        if (_vm?.CurrentPage is { } page && Find<System.Windows.Controls.ScrollViewer>(page) is { } scroller)
        {
            if (toEnd)
                scroller.ScrollToEnd();
            else
                scroller.ScrollToHome();
        }
    }

    public async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;

        if (_main is not null)
        {
            _main.ExitRequested = true;
            _main.Close();
        }

        _hotkeys?.Dispose();
        _tray?.Dispose();
        _vm?.Lighting.Dispose();
        if (_session is not null)
        {
            try { await _session.DisposeAsync(); }
            catch (Exception ex) { UiLog.Error("Error cerrando la sesión", ex); }
        }

        _showSignal?.Set();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
