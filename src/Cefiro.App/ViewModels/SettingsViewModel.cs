using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using Cefiro.App.Infrastructure;
using Cefiro.App.Services;
using Cefiro.Core.Platform;
using Cefiro.Ipc.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cefiro.App.ViewModels;

public sealed record AccentOption(string Name, string Hex, string Hex2)
{
    public SolidColorBrush Brush { get; } = Frozen(Hex);

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

public sealed record ConflictItem(string Name, string Detail, bool Critical);

public sealed record CreditItem(string Name, string Description);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly CefiroSession _session;
    private readonly UiSettingsStore _store;
    private bool _loading;

    public SettingsViewModel(CefiroSession session, UiSettingsStore store)
    {
        _session = session;
        _store = store;
        AppVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "—";

        LoadFromStore();
        session.PropertyChanged += OnSessionChanged;
        session.Loaded += RefreshInfo;
        session.LightingChanged += RefreshZones;
        RefreshInfo();
        RefreshZones();
    }

    public IReadOnlyList<AccentOption> Accents { get; } =
    [
        new("Cian", "#00E5FF", "#7C4DFF"),
        new("Violeta", "#7C4DFF", "#FF2E88"),
        new("Rosa neón", "#FF2E88", "#7C4DFF"),
        new("Ámbar", "#FFB300", "#FF2E88"),
        new("Verde", "#00E676", "#00E5FF"),
        new("Azul", "#2979FF", "#00E5FF"),
        new("Rojo", "#FF3D5A", "#FFB300"),
    ];

    public IReadOnlyList<CreditItem> Credits { get; } =
    [
        new("LibreHardwareMonitor", "Sensores y control de ventiladores de la placa y la gráfica (MPL 2.0)."),
        new("PawnIO", "Driver firmado con el que LibreHardwareMonitor accede al chip de la placa."),
        new("OpenRGB", "Servidor con el que Céfiro habla con las luces (protocolo del SDK, puerto 6742)."),
        new("WPF-UI, LiveCharts2, CommunityToolkit.Mvvm, H.NotifyIcon, NAudio", "Interfaz, gráficas, notificaciones y audio (MIT)."),
        new("Serilog", "Registros del servicio (Apache 2.0)."),
    ];

    public ObservableCollection<ConflictItem> Conflicts { get; } = [];

    public string AppVersion { get; }

    // ---- Apariencia y uso ----
    [ObservableProperty] public partial string AccentHex { get; set; } = "#00E5FF";
    [ObservableProperty] public partial bool AccentFollowsLights { get; set; }
    [ObservableProperty] public partial bool UseFahrenheit { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool StartMinimized { get; set; }
    [ObservableProperty] public partial bool ToastsEnabled { get; set; }

    // ---- Mi PC ----
    [ObservableProperty] public partial bool RadiatorOnTop { get; set; } = true;
    [ObservableProperty] public partial int FrontFans { get; set; }
    [ObservableProperty] public partial int RearFans { get; set; }
    [ObservableProperty] public partial int BottomFans { get; set; }
    [ObservableProperty] public partial string CaseLightZone { get; set; } = "";
    [ObservableProperty] public partial string AioLightZone { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<string> LightZones { get; set; } = [];

    // ---- Servicio ----
    [ObservableProperty] public partial bool HasConflicts { get; set; }
    [ObservableProperty] public partial string ConnectionText { get; set; } = "—";
    [ObservableProperty] public partial string ServiceVersionText { get; set; } = "—";
    [ObservableProperty] public partial string HardwareText { get; set; } = "—";
    [ObservableProperty] public partial string PawnIoText { get; set; } = "—";
    [ObservableProperty] public partial string OpenRgbText { get; set; } = "—";
    [ObservableProperty] public partial string ServiceUsageText { get; set; } = "—";
    [ObservableProperty] public partial string? LastErrorText { get; set; }
    [ObservableProperty] public partial string? WarningsText { get; set; }

    private void LoadFromStore()
    {
        _loading = true;
        try
        {
            var s = _store.Current;
            AccentHex = s.AccentHex;
            AccentFollowsLights = s.AccentFollowsLights;
            UseFahrenheit = s.UseFahrenheit;
            CloseToTray = s.CloseToTray;
            StartMinimized = s.StartMinimized;
            ToastsEnabled = s.ToastsEnabled;
            StartWithWindows = Autostart.IsEnabled;
            RadiatorOnTop = s.Layout.RadiatorPosition != "Front";
            FrontFans = s.Layout.FrontFans;
            RearFans = s.Layout.RearFans;
            BottomFans = s.Layout.BottomFans;
            CaseLightZone = s.Layout.CaseLightZone;
            AioLightZone = s.Layout.AioLightZone;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Func<UiSettings, UiSettings> change)
    {
        if (!_loading)
            _store.Update(change);
    }

    // ------------------------------------------------------------------ apariencia

    [RelayCommand]
    private void SetAccent(AccentOption option)
    {
        AccentFollowsLights = false;
        AccentHex = option.Hex;
        _store.Update(s => s with { AccentHex = option.Hex, Accent2Hex = option.Hex2, AccentFollowsLights = false });
        AccentFollower.ApplyStored(_store.Current);
    }

    partial void OnAccentHexChanged(string value)
    {
        if (_loading || !Core.Lighting.Rgb.TryParseHex(value, out var c))
            return;

        var hex = c.ToHex();
        if (hex == _store.Current.AccentHex)
            return;

        var second = ThemeService.Shift(Color.FromRgb(c.R, c.G, c.B), 70);
        _store.Update(s => s with { AccentHex = hex, Accent2Hex = $"#{second.R:X2}{second.G:X2}{second.B:X2}" });
        if (!AccentFollowsLights)
            AccentFollower.ApplyStored(_store.Current);
    }

    partial void OnAccentFollowsLightsChanged(bool value) => Save(s => s with { AccentFollowsLights = value });

    partial void OnUseFahrenheitChanged(bool value)
    {
        Format.UseFahrenheit = value;
        Save(s => s with { UseFahrenheit = value });
    }

    partial void OnCloseToTrayChanged(bool value) => Save(s => s with { CloseToTray = value });

    partial void OnToastsEnabledChanged(bool value) => Save(s => s with { ToastsEnabled = value });

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
            return;
        Autostart.Set(value, StartMinimized);
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        Save(s => s with { StartMinimized = value });
        if (!_loading && StartWithWindows)
            Autostart.Set(true, value);
    }

    [RelayCommand]
    private void TestNotification() =>
        Notifier.Show("Así se ven los avisos de Céfiro", "Te avisaremos si algo se calienta demasiado o si un ventilador se para.");

    // ------------------------------------------------------------------ mi PC

    private void SaveLayout()
    {
        if (_loading)
            return;

        var layout = new CaseLayout
        {
            RadiatorPosition = RadiatorOnTop ? "Top" : "Front",
            FrontFans = FrontFans,
            RearFans = RearFans,
            BottomFans = BottomFans,
            CaseLightZone = CaseLightZone,
            AioLightZone = AioLightZone,
        };
        _store.Update(s => s with { Layout = layout });
    }

    partial void OnRadiatorOnTopChanged(bool value) => SaveLayout();
    partial void OnFrontFansChanged(int value) => SaveLayout();
    partial void OnRearFansChanged(int value) => SaveLayout();
    partial void OnBottomFansChanged(int value) => SaveLayout();

    partial void OnCaseLightZoneChanged(string value)
    {
        if (value is null)
        {
            CaseLightZone = _store.Current.Layout.CaseLightZone;
            return;
        }

        SaveLayout();
    }

    partial void OnAioLightZoneChanged(string value)
    {
        if (value is null)
        {
            AioLightZone = _store.Current.Layout.AioLightZone;
            return;
        }

        SaveLayout();
    }

    [RelayCommand]
    private void ChangeFans(string what)
    {
        switch (what)
        {
            case "front+": FrontFans = Math.Min(4, FrontFans + 1); break;
            case "front-": FrontFans = Math.Max(0, FrontFans - 1); break;
            case "rear+": RearFans = Math.Min(2, RearFans + 1); break;
            case "rear-": RearFans = Math.Max(0, RearFans - 1); break;
            case "bottom+": BottomFans = Math.Min(3, BottomFans + 1); break;
            case "bottom-": BottomFans = Math.Max(0, BottomFans - 1); break;
        }
    }

    private void RefreshZones()
    {
        // Zonas de la placa que pueden llevar ventiladores (las redimensionables, JRAINBOW…).
        var zones = _session.Lighting?.Devices
            .Where(d => d.Kind == "Motherboard")
            .SelectMany(d => d.Zones.Where(z => z.Resizable).Select(z => z.Name))
            .Distinct()
            .ToList() ?? [];

        foreach (var current in new[] { _store.Current.Layout.CaseLightZone, _store.Current.Layout.AioLightZone })
        {
            if (!string.IsNullOrEmpty(current) && !zones.Contains(current))
                zones.Add(current);
        }

        LightZones = zones;
    }

    // ------------------------------------------------------------------ servicio

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CefiroSession.Status) or nameof(CefiroSession.IsConnected) or nameof(CefiroSession.ServiceVersion))
            RefreshInfo();
    }

    private void RefreshInfo()
    {
        var s = _session;
        ConnectionText = s.IsDemo ? "Modo demo (sin servicio)" : s.IsConnected ? "Conectado al servicio de Céfiro" : "Sin conexión con el servicio";
        ServiceVersionText = s.ServiceVersion is { } v ? $"Céfiro {v}" : "—";

        if (s.Catalog?.Backend is { } b)
        {
            HardwareText = $"{b.Motherboard ?? "Placa desconocida"} · chip {b.SuperIoChip ?? "—"}";
            PawnIoText = b.PawnIoInstalled
                ? b.IsAdministrator ? "PawnIO instalado · el servicio tiene permisos de administrador" : "PawnIO instalado · el servicio NO tiene permisos de administrador"
                : "PawnIO no está instalado: sin él no se pueden leer ni controlar los ventiladores de la placa";
            WarningsText = b.Warnings.Count > 0 ? string.Join("\n", b.Warnings) : null;
        }

        if (s.Status is { } st)
        {
            OpenRgbText = st.OpenRgbConnected ? $"Conectado · {s.Lighting?.ServerName ?? "OpenRGB"}" : "No conectado (las luces no responderán)";
            ServiceUsageText = $"{st.ServiceCpuPercent:0.0} % de CPU · {st.ServiceMemoryBytes / 1_048_576.0:0} MB · lleva {FormatUptime(st.UptimeSeconds)}";
            LastErrorText = st.LastError;
        }

        RefreshConflicts();
    }

    private static string FormatUptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min";
    }

    [RelayCommand]
    private void RefreshConflicts()
    {
        var list = (_session.Status?.Conflicts ?? []).ToList();
        if (ConflictDetector.DetectWindowsDynamicLighting() is { } dynamicLighting)
            list.Add(dynamicLighting);

        var items = list.Select(c => new ConflictItem(c.Name, c.Detail, c.Severity == ConflictSeverity.Critical)).ToList();
        if (!items.SequenceEqual(Conflicts))
        {
            Conflicts.Clear();
            foreach (var i in items)
                Conflicts.Add(i);
        }

        HasConflicts = Conflicts.Count > 0;
    }

    [RelayCommand]
    private void OpenUiLogs() => OpenFolder(UiLog.DirectoryPath);

    [RelayCommand]
    private void OpenServiceLogs() =>
        OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Cefiro", "logs"));

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UiLog.Error($"No se pudo abrir {path}", ex);
        }
    }
}
