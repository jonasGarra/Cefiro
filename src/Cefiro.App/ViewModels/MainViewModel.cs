using System.ComponentModel;
using System.Windows;
using Cefiro.App.Infrastructure;
using Cefiro.App.Views;
using Cefiro.Ipc.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cefiro.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, FrameworkElement> _pages = new(StringComparer.Ordinal);

    public MainViewModel(CefiroSession session, UiSettingsStore settings)
    {
        Session = session;
        Settings = settings;
        Dashboard = new DashboardViewModel(session, settings);
        Fans = new FansViewModel(session, settings);
        Lighting = new LightingViewModel(session, settings);
        Sensors = new SensorsViewModel(session);

        session.PropertyChanged += OnSessionChanged;
        IsDemo = session.IsDemo;
        UpdateConnection();
        Navigate(settings.Current.LastPage ?? "panel");
    }

    public CefiroSession Session { get; }
    public UiSettingsStore Settings { get; }
    public DashboardViewModel Dashboard { get; }
    public FansViewModel Fans { get; }
    public LightingViewModel Lighting { get; }
    public SensorsViewModel Sensors { get; }

    [ObservableProperty] public partial FrameworkElement? CurrentPage { get; set; }
    [ObservableProperty] public partial string CurrentKey { get; set; } = "panel";
    [ObservableProperty] public partial bool IsDemo { get; set; }
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial bool ShowConnectingBanner { get; set; } = true;
    [ObservableProperty] public partial string ConnectionText { get; set; } = "Conectando…";
    [ObservableProperty] public partial bool EmergencyActive { get; set; }
    [ObservableProperty] public partial string? EmergencyText { get; set; }
    [ObservableProperty] public partial bool HasConflicts { get; set; }
    [ObservableProperty] public partial string? ConflictText { get; set; }
    [ObservableProperty] public partial string? ConflictDetail { get; set; }

    [RelayCommand]
    public void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "ventiladores" => new FansPage { DataContext = Fans },
                "iluminacion" => new LightingPage { DataContext = Lighting },
                "sensores" => new SensorsPage { DataContext = Sensors },
                _ => new DashboardPage { DataContext = Dashboard },
            };
            if (key is not ("ventiladores" or "iluminacion" or "sensores"))
                key = "panel";
            _pages[key] = page;
        }

        CurrentKey = key;
        CurrentPage = page;
        if (Settings.Current.LastPage != key)
            Settings.Update(s => s with { LastPage = key });
    }

    [RelayCommand]
    private void DismissConflicts() => HasConflicts = false;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CefiroSession.IsConnected):
            case nameof(CefiroSession.IsLoaded):
                UpdateConnection();
                break;
            case nameof(CefiroSession.Status):
                UpdateStatus(Session.Status);
                break;
        }
    }

    private void UpdateConnection()
    {
        IsConnected = Session.IsConnected && Session.IsLoaded;
        ShowConnectingBanner = !IsConnected;
        ConnectionText = IsDemo ? "Modo demo"
            : Session.IsLoaded ? "Conectado"
            : Session.IsConnected ? "Cargando el hardware…"
            : "Buscando el servicio…";
    }

    private void UpdateStatus(StatusDto? status)
    {
        if (status is null)
            return;

        EmergencyActive = status.EmergencyActive;
        EmergencyText = status.EmergencyReason;

        // La iluminación dinámica de Windows solo se puede comprobar desde la sesión del usuario.
        var conflicts = status.Conflicts.ToList();
        if (Core.Platform.ConflictDetector.DetectWindowsDynamicLighting() is { } dynamicLighting)
            conflicts.Add(dynamicLighting);

        var important = conflicts.Where(c => c.Severity != ConflictSeverity.Info).ToList();
        var text = important.Count switch
        {
            0 => null,
            1 => $"{important[0].Name} puede interferir con Céfiro",
            _ => $"{important.Count} programas pueden interferir con Céfiro",
        };

        if (text != ConflictText)
        {
            ConflictText = text;
            ConflictDetail = string.Join("\n\n", important.Select(c => $"{c.Name}: {c.Detail}"));
            HasConflicts = text is not null;
        }
    }
}
