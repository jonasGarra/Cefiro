using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Cefiro.App.Infrastructure;
using Cefiro.Core.Fans;
using Cefiro.Ipc;
using Cefiro.Ipc.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cefiro.App.ViewModels;

/// <summary>Una opción de "temperatura de referencia" para las curvas.</summary>
public sealed record SourceOption(string Key, string Label, string ShortLabel, TemperatureAggregation Aggregation, IReadOnlyList<string> SensorIds);

public sealed record RoleOption(FanRole Role, string Label);

public sealed record GroupOption(string Id, string Label);

public sealed record CalibrationRowViewModel(string Name, string StopText, string StartText, string MaxText, string Status, bool Ok);

/// <summary>Una salida de ventilador: su configuración (editable) y su estado en directo.</summary>
public sealed partial class FanChannelViewModel : ObservableObject
{
    private readonly FansViewModel _owner;
    private readonly List<float?> _history = [];
    private TemperatureSourceDto _rawSource = new(TemperatureAggregation.Single, []);
    private string _sourceKey = "";
    private string _groupId = "";

    /// <summary>Mientras es true, los cambios vienen del servicio y no se vuelven a guardar.</summary>
    internal bool Loading;

    public FanChannelViewModel(FansViewModel owner, FanChannelDto channel)
    {
        _owner = owner;
        Channel = channel;
        Loading = true;
        Role = channel.DetectedRole;
        Loading = false;
    }

    public FanChannelDto Channel { get; }
    public string Id => Channel.Id;
    public string Header => Channel.Header;
    public bool IsGpu => Channel.DetectedRole == FanRole.Gpu;
    public bool CanChangeRole => !IsGpu;
    public bool HasRpmSensor => Channel.RpmSensorId is not null;
    public string RoleEvidence => Channel.RoleEvidence;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial FanRole Role { get; set; }
    [ObservableProperty] public partial FanMode Mode { get; set; }
    [ObservableProperty] public partial double FixedPercent { get; set; } = 50;
    [ObservableProperty] public partial IReadOnlyList<CurvePointDto> Curve { get; set; } = [];
    [ObservableProperty] public partial double Hysteresis { get; set; } = 2;
    [ObservableProperty] public partial double RampUp { get; set; } = 10;
    [ObservableProperty] public partial double RampDown { get; set; } = 3;
    [ObservableProperty] public partial double MinimumPercent { get; set; } = 20;
    [ObservableProperty] public partial double StartPercent { get; set; } = 35;
    [ObservableProperty] public partial bool AllowStop { get; set; }

    /// <summary>Clave de la fuente de temperatura (ver <see cref="SourceOption"/>). Los null de los ComboBox se ignoran.</summary>
    public string SourceKey
    {
        get => _sourceKey;
        set
        {
            if (value is null || !SetProperty(ref _sourceKey, value))
                return;
            OnPropertyChanged(nameof(SourceLabel));
            Edited(control: true);
        }
    }

    /// <summary>Grupo ("" = ninguno). Los null de los ComboBox (al rehacer la lista) se ignoran.</summary>
    public string GroupId
    {
        get => _groupId;
        set
        {
            if (value is null || !SetProperty(ref _groupId, value))
                return;
            if (!Loading)
                _owner.OnGroupSelected(this, value);
        }
    }

    // ---- En directo ----
    [ObservableProperty] public partial double Rpm { get; set; }
    [ObservableProperty] public partial string RpmText { get; set; } = "—";
    [ObservableProperty] public partial double Output { get; set; }
    [ObservableProperty] public partial string OutputText { get; set; } = "—";
    [ObservableProperty] public partial double SourceTemperature { get; set; } = double.NaN;
    [ObservableProperty] public partial string SourceText { get; set; } = "—";
    [ObservableProperty] public partial string? Note { get; set; }
    [ObservableProperty] public partial string LiveModeText { get; set; } = "BIOS";
    [ObservableProperty] public partial bool EverSpun { get; set; }
    [ObservableProperty] public partial IReadOnlyList<float?> History { get; set; } = [];
    [ObservableProperty] public partial string? GroupName { get; set; }

    // ---- Derivadas ----
    public double Floor => SafetyGuard.FloorFor(Role, Channel.HardwareMinimumPercent);
    public bool CanStop => SafetyGuard.CanStop(Role);
    public double FixedMinimum => CanStop && AllowStop ? 0 : Floor;
    public bool IsBios => Mode == FanMode.Bios;
    public bool IsFixed => Mode == FanMode.Fixed;
    public bool IsCurve => Mode == FanMode.Curve;
    public string SourceLabel => _owner.ShortLabelFor(SourceKey);

    public string RoleText => Role switch
    {
        FanRole.Pump => "Bomba",
        FanRole.Radiator => "Radiador",
        FanRole.Gpu => "Gráfica",
        _ => "Caja",
    };

    public string RoleGlyph => Role switch
    {
        FanRole.Pump => "Drop24",
        FanRole.Gpu => "DeveloperBoard24",
        _ => "WeatherSqualls24",
    };

    public string FloorText => Role switch
    {
        FanRole.Pump => $"Protegida: nunca por debajo del {Floor:0} % ni parada",
        FanRole.Radiator => $"Protegido: nunca por debajo del {Floor:0} %",
        FanRole.Gpu => Floor > 0 ? $"La tarjeta no baja del {Floor:0} %" : "Límites de la tarjeta gráfica",
        _ => AllowStop ? "Se para cuando la curva pide menos del mínimo" : "Ventilador de caja",
    };

    public string ModeHint => Mode switch
    {
        FanMode.Fixed => "Siempre a la misma velocidad.",
        FanMode.Curve => "La velocidad sigue a la temperatura.",
        _ => IsGpu ? "Lo controla el driver de la gráfica, con su modo 0 RPM." : "Lo controla la placa base, como sin Céfiro.",
    };

    public string FixedText => $"{FixedPercent:0} %";
    public string HysteresisText => (Format.UseFahrenheit ? Hysteresis * 9 / 5 : Hysteresis).ToString("0.0", Format.Es) + " " + Format.TemperatureUnit;
    public string RampUpText => $"{RampUp:0} %/s";
    public string RampDownText => $"{RampDown:0} %/s";
    public string MinimumText => $"{MinimumPercent:0} %";
    public string StartText => $"{StartPercent:0} %";

    partial void OnNameChanged(string value) => Edited(control: false);

    partial void OnRoleChanged(FanRole value)
    {
        OnPropertyChanged(nameof(Floor));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(FixedMinimum));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(RoleGlyph));
        OnPropertyChanged(nameof(FloorText));
        OnPropertyChanged(nameof(ModeHint));

        // Al cambiar a un rol protegido se suben los valores a su mínimo.
        var wasLoading = Loading;
        Loading = true;
        if (!SafetyGuard.CanStop(value))
            AllowStop = false;
        if (MinimumPercent < Floor)
            MinimumPercent = Floor;
        if (FixedPercent < FixedMinimum)
            FixedPercent = FixedMinimum;
        Loading = wasLoading;
        Edited(control: false);
    }

    partial void OnModeChanged(FanMode value)
    {
        OnPropertyChanged(nameof(IsBios));
        OnPropertyChanged(nameof(IsFixed));
        OnPropertyChanged(nameof(IsCurve));
        OnPropertyChanged(nameof(ModeHint));
        Edited(control: true);
    }

    partial void OnFixedPercentChanged(double value)
    {
        if (value < FixedMinimum - 0.01)
        {
            FixedPercent = FixedMinimum;
            return;
        }

        OnPropertyChanged(nameof(FixedText));
        Edited(control: true);
    }

    partial void OnCurveChanged(IReadOnlyList<CurvePointDto> value) => Edited(control: true);

    partial void OnHysteresisChanged(double value)
    {
        OnPropertyChanged(nameof(HysteresisText));
        Edited(control: true);
    }

    partial void OnRampUpChanged(double value)
    {
        OnPropertyChanged(nameof(RampUpText));
        Edited(control: true);
    }

    partial void OnRampDownChanged(double value)
    {
        OnPropertyChanged(nameof(RampDownText));
        Edited(control: true);
    }

    partial void OnMinimumPercentChanged(double value)
    {
        if (value < Floor - 0.01)
        {
            MinimumPercent = Floor;
            return;
        }

        if (StartPercent < value)
            StartPercent = value;
        OnPropertyChanged(nameof(MinimumText));
        Edited(control: false);
    }

    partial void OnStartPercentChanged(double value)
    {
        if (value < MinimumPercent - 0.01)
        {
            StartPercent = MinimumPercent;
            return;
        }

        OnPropertyChanged(nameof(StartText));
        Edited(control: false);
    }

    partial void OnAllowStopChanged(bool value)
    {
        OnPropertyChanged(nameof(FixedMinimum));
        OnPropertyChanged(nameof(FloorText));
        if (!value && FixedPercent < FixedMinimum)
            FixedPercent = FixedMinimum;
        Edited(control: false);
    }

    private void Edited(bool control)
    {
        if (!Loading)
            _owner.OnChannelEdited(this, control);
    }

    /// <summary>Carga la configuración que ha aplicado el servicio (ya validada).</summary>
    internal void Load(FanChannelSettingsDto s, string sourceKey)
    {
        Loading = true;
        try
        {
            Role = s.Role;
            AllowStop = s.AllowStop;
            Name = s.Name;
            Mode = s.Mode;
            MinimumPercent = s.MinimumPercent;
            StartPercent = s.StartPercent;
            FixedPercent = s.FixedPercent;
            Curve = s.Curve;
            _rawSource = s.Source;
            SourceKey = sourceKey;
            Hysteresis = s.HysteresisCelsius;
            RampUp = s.RampUpPercentPerSecond;
            RampDown = s.RampDownPercentPerSecond;
            GroupId = s.GroupId ?? "";
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>Copia el modo y la curva de otra salida (grupos). Mínimos y nombre no se tocan.</summary>
    internal void CopyControlFrom(FanChannelViewModel other)
    {
        Loading = true;
        try
        {
            Curve = other.Curve;
            SourceKey = other.SourceKey;
            _rawSource = other._rawSource;
            Hysteresis = other.Hysteresis;
            RampUp = other.RampUp;
            RampDown = other.RampDown;
            FixedPercent = Math.Max(other.FixedPercent, FixedMinimum);
            Mode = other.Mode;
        }
        finally
        {
            Loading = false;
        }
    }

    internal void SetModeQuietly(FanMode mode)
    {
        Loading = true;
        Mode = mode;
        Loading = false;
    }

    internal void SetLimitsQuietly(double minimum, double start)
    {
        Loading = true;
        MinimumPercent = Math.Max(minimum, Floor);
        StartPercent = Math.Max(start, MinimumPercent);
        Loading = false;
    }

    internal void SetGroupQuietly(string groupId)
    {
        Loading = true;
        GroupId = groupId;
        Loading = false;
    }

    /// <summary>Vuelve a avisar del grupo (el ComboBox pierde la selección al rehacer su lista).</summary>
    internal void NotifyGroup() => OnPropertyChanged(nameof(GroupId));

    public FanChannelSettingsDto ToDto() => new()
    {
        ChannelId = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? FanDefaults.DefaultName(Channel) : Name.Trim(),
        Role = Role,
        Mode = Mode,
        FixedPercent = (float)FixedPercent,
        Curve = Curve,
        Source = _owner.SourceFor(SourceKey) ?? _rawSource,
        HysteresisCelsius = (float)Hysteresis,
        RampUpPercentPerSecond = (float)RampUp,
        RampDownPercentPerSecond = (float)RampDown,
        MinimumPercent = (float)MinimumPercent,
        StartPercent = (float)StartPercent,
        AllowStop = AllowStop,
        GroupId = string.IsNullOrEmpty(GroupId) || GroupId == FansViewModel.NewGroupKey ? null : GroupId,
    };

    /// <param name="sourceTemperature">Temperatura de referencia (la del motor o, en modo BIOS, calculada aquí).</param>
    internal void UpdateLive(FanStateDto? s, float? sourceTemperature)
    {
        var rpm = s?.Rpm;
        Rpm = rpm ?? 0;
        RpmText = rpm is { } r ? r.ToString("#,0", Format.Es) : "—";
        Output = s?.OutputPercent ?? 0;
        OutputText = Format.Percent(s?.OutputPercent);
        SourceTemperature = sourceTemperature is { } t ? t : double.NaN;
        SourceText = sourceTemperature is { } t2 ? $"{SourceLabel} · {Format.Temperature(t2)}" : SourceLabel;
        Note = string.IsNullOrWhiteSpace(s?.Note) ? null : s.Note;
        LiveModeText = s?.EffectiveMode switch
        {
            FanMode.Curve => "Curva",
            FanMode.Fixed => "Fijo",
            _ => "BIOS",
        };

        if (rpm > 0)
            EverSpun = true;

        _history.Add(rpm);
        if (_history.Count > 60)
            _history.RemoveAt(0);
        History = _history.ToArray();
    }

    /// <summary>Refresca los textos que dependen de °C/°F.</summary>
    internal void RefreshUnits() => OnPropertyChanged(nameof(HysteresisText));
}

/// <summary>Grupo de salidas que comparten modo y curva.</summary>
public sealed partial class FanGroupViewModel(FansViewModel owner, string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Members { get; set; } = "";

    internal bool Loading;

    partial void OnNameChanged(string value)
    {
        if (!Loading)
            owner.RenameGroup(Id, value);
    }
}

public sealed partial class FansViewModel : ObservableObject
{
    public const string NewGroupKey = "__nuevo__";

    private readonly CefiroSession _session;
    private readonly UiSettingsStore _settings;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, FanChannelViewModel> _byId = new(StringComparer.Ordinal);
    private List<FanGroupDto> _groups = [];
    private IReadOnlyList<SourceOption> _sourceOptions = [];
    private int _catalogVersion = -1;
    private bool _loadingSafety;
    private CalibrationStatusDto? _calibration;

    public FansViewModel(CefiroSession session, UiSettingsStore settings)
    {
        _session = session;
        _settings = settings;
        ShowEmpty = settings.Current.ShowEmptyFanHeaders;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveNowAsync();
        };

        session.Loaded += OnSessionLoaded;
        session.FanSettingsChanged += OnSettingsChanged;
        session.FansUpdated += OnFrame;
        session.CalibrationUpdated += OnCalibration;
        settings.Changed += _ =>
        {
            foreach (var c in Channels)
                c.RefreshUnits();
            RefreshSafetyTexts();
        };

        if (session.IsLoaded)
        {
            OnSessionLoaded();
            OnSettingsChanged();
        }
    }

    public CefiroSession Session => _session;

    /// <summary>Todas las salidas que ha encontrado el servicio.</summary>
    public ObservableCollection<FanChannelViewModel> Channels { get; } = [];

    /// <summary>Las que se enseñan (las que tienen algo conectado, o todas si se pide).</summary>
    public ObservableCollection<FanChannelViewModel> VisibleChannels { get; } = [];

    public ObservableCollection<GroupOption> GroupOptions { get; } = [];
    public ObservableCollection<FanGroupViewModel> Groups { get; } = [];
    public ObservableCollection<CalibrationRowViewModel> CalibrationRows { get; } = [];

    public IReadOnlyList<RoleOption> RoleOptions { get; } =
    [
        new(FanRole.Case, "Ventiladores de caja"),
        new(FanRole.Radiator, "Radiador de la líquida"),
        new(FanRole.Pump, "Bomba de la líquida"),
    ];

    [ObservableProperty] public partial IReadOnlyList<SourceOption> SourceOptions { get; set; } = [];
    [ObservableProperty] public partial FanChannelViewModel? Editing { get; set; }
    [ObservableProperty] public partial bool ShowEmpty { get; set; }
    [ObservableProperty] public partial int HiddenCount { get; set; }
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial bool IsSaving { get; set; }
    [ObservableProperty] public partial bool HasChannels { get; set; }
    [ObservableProperty] public partial bool HasGroups { get; set; }

    // Modo rápido activo (para resaltar su tarjeta).
    [ObservableProperty] public partial bool IsSilentActive { get; set; }
    [ObservableProperty] public partial bool IsBalancedActive { get; set; }
    [ObservableProperty] public partial bool IsPerformanceActive { get; set; }
    [ObservableProperty] public partial bool IsMaximumActive { get; set; }
    [ObservableProperty] public partial bool IsBiosActive { get; set; }
    [ObservableProperty] public partial string ActivePresetText { get; set; } = "";

    // Seguridad (en °C; los textos se convierten a la unidad elegida).
    [ObservableProperty] public partial double CpuCritical { get; set; } = 90;
    [ObservableProperty] public partial double GpuCritical { get; set; } = 85;
    [ObservableProperty] public partial double CpuWarning { get; set; } = 85;
    [ObservableProperty] public partial double GpuWarning { get; set; } = 80;
    [ObservableProperty] public partial double RecoveryMargin { get; set; } = 8;
    [ObservableProperty] public partial double RecoveryHold { get; set; } = 15;
    [ObservableProperty] public partial double SensorTimeout { get; set; } = 3;
    [ObservableProperty] public partial bool EmergencyActive { get; set; }
    [ObservableProperty] public partial string EmergencyText { get; set; } = "Vigilando";

    // Calibración.
    [ObservableProperty] public partial bool CalibrationRunning { get; set; }
    [ObservableProperty] public partial double CalibrationProgress { get; set; }
    [ObservableProperty] public partial string? CalibrationMessage { get; set; }
    [ObservableProperty] public partial bool CanApplyCalibration { get; set; }
    [ObservableProperty] public partial bool HasCalibrationRows { get; set; }

    public string CpuCriticalText => Format.Temperature((float)CpuCritical, 0);
    public string GpuCriticalText => Format.Temperature((float)GpuCritical, 0);
    public string CpuWarningText => Format.Temperature((float)CpuWarning, 0);
    public string GpuWarningText => Format.Temperature((float)GpuWarning, 0);
    public string RecoveryMarginText => (Format.UseFahrenheit ? RecoveryMargin * 9 / 5 : RecoveryMargin).ToString("0", Format.Es) + " " + Format.TemperatureUnit;
    public string RecoveryHoldText => $"{RecoveryHold:0} s";
    public string SensorTimeoutText => $"{SensorTimeout:0} s";

    // ------------------------------------------------------------------ carga

    private void OnSessionLoaded()
    {
        if (_session.Catalog is not { } catalog)
            return;

        if (catalog.Version != _catalogVersion)
        {
            _catalogVersion = catalog.Version;
            BuildSourceOptions(catalog);
            BuildChannels(catalog);
        }

        _ = LoadCalibrationAsync();
    }

    private void BuildChannels(CatalogDto catalog)
    {
        var same = Channels.Count == catalog.FanChannels.Count
                   && Channels.Zip(catalog.FanChannels).All(p => p.First.Id == p.Second.Id && p.First.Channel.DetectedRole == p.Second.DetectedRole);
        if (same)
            return;

        Editing = null;
        Channels.Clear();
        _byId.Clear();
        foreach (var ch in catalog.FanChannels)
        {
            var vm = new FanChannelViewModel(this, ch);
            Channels.Add(vm);
            _byId[ch.Id] = vm;
        }

        HasChannels = Channels.Count > 0;
    }

    private void BuildSourceOptions(CatalogDto catalog)
    {
        var k = catalog.Keys;
        var list = new List<SourceOption>();

        void Single(string key, string label, string shortLabel, string? id)
        {
            if (id is not null)
                list.Add(new SourceOption(key, label, shortLabel, TemperatureAggregation.Single, [id]));
        }

        Single("cpu", "Procesador (CPU)", "CPU", k.CpuTemperature);
        Single("gpu", "Tarjeta gráfica (GPU)", "GPU", k.GpuTemperature);
        Single("vrm", "Placa base · VRM", "VRM", k.VrmTemperature);
        Single("caja", "Interior de la caja", "Caja", k.BoardTemperature);
        if (k.CpuTemperature is { } cpu && k.GpuTemperature is { } gpu)
        {
            list.Add(new SourceOption("max", "La más alta de CPU y gráfica", "Máx. CPU/GPU", TemperatureAggregation.Maximum, [cpu, gpu]));
            list.Add(new SourceOption("media", "Media de CPU y gráfica", "Media CPU/GPU", TemperatureAggregation.Average, [cpu, gpu]));
        }

        var used = list.Where(o => o.Aggregation == TemperatureAggregation.Single).SelectMany(o => o.SensorIds).ToHashSet(StringComparer.Ordinal);
        foreach (var s in catalog.Sensors.Where(s => s.Kind == SensorKind.Temperature && !used.Contains(s.Id)))
            list.Add(new SourceOption("s:" + s.Id, $"{s.Name} · {s.Device}", s.Name, TemperatureAggregation.Single, [s.Id]));

        _sourceOptions = list;
        SourceOptions = list;
    }

    internal string KeyFor(TemperatureSourceDto source)
    {
        foreach (var o in _sourceOptions)
        {
            if (o.Aggregation == source.Aggregation
                && o.SensorIds.Count == source.SensorIds.Count
                && o.SensorIds.All(source.SensorIds.Contains))
                return o.Key;
        }

        return "personalizada";
    }

    internal TemperatureSourceDto? SourceFor(string key) =>
        _sourceOptions.FirstOrDefault(o => o.Key == key) is { } o ? new TemperatureSourceDto(o.Aggregation, o.SensorIds) : null;

    /// <summary>Temperatura de una fuente con las lecturas actuales (para enseñarla aunque el motor no la use).</summary>
    private float? ComputeSource(string key)
    {
        if (_sourceOptions.FirstOrDefault(o => o.Key == key) is not { } option)
            return null;

        var values = option.SensorIds.Select(_session.Value).Where(v => v is not null).Select(v => v!.Value).ToList();
        if (values.Count == 0)
            return null;

        return option.Aggregation switch
        {
            TemperatureAggregation.Maximum => values.Max(),
            TemperatureAggregation.Average => values.Average(),
            _ => values[0],
        };
    }

    internal string ShortLabelFor(string key) =>
        _sourceOptions.FirstOrDefault(o => o.Key == key)?.ShortLabel ?? (key == "personalizada" ? "Personalizada" : "—");

    private void OnSettingsChanged()
    {
        // Si hay cambios del usuario pendientes de guardar, se esperan: el guardado traerá la versión buena.
        if (_saveTimer.IsEnabled)
            return;

        if (_session.FanSettings is not { } settings)
            return;

        if (_session.Catalog is { } catalog && catalog.Version != _catalogVersion)
            OnSessionLoaded();

        foreach (var s in settings.Channels)
        {
            if (_byId.TryGetValue(s.ChannelId, out var vm))
                vm.Load(s, KeyFor(s.Source));
        }

        _groups = settings.Groups.ToList();
        RebuildGroups();

        _loadingSafety = true;
        CpuCritical = settings.Safety.CpuCriticalCelsius;
        GpuCritical = settings.Safety.GpuCriticalCelsius;
        CpuWarning = settings.Safety.CpuWarningCelsius;
        GpuWarning = settings.Safety.GpuWarningCelsius;
        RecoveryMargin = settings.Safety.RecoveryMarginCelsius;
        RecoveryHold = settings.Safety.RecoveryHoldSeconds;
        SensorTimeout = settings.Safety.SensorTimeoutSeconds;
        _loadingSafety = false;
        RefreshSafetyTexts();

        UpdateVisibility();
        UpdateActivePreset();
    }

    // ------------------------------------------------------------------ en directo

    private void OnFrame()
    {
        if (_session.FanFrame is not { } frame)
            return;

        var spunBefore = Channels.Count(c => c.EverSpun);
        foreach (var state in frame.Channels)
        {
            if (_byId.TryGetValue(state.ChannelId, out var vm))
                vm.UpdateLive(state, state.SourceTemperature ?? ComputeSource(vm.SourceKey));
        }

        EmergencyActive = frame.EmergencyActive;
        EmergencyText = frame.EmergencyActive
            ? _session.Status?.EmergencyReason ?? "Todos los ventiladores al 100 %"
            : "Vigilando";

        if (Channels.Count(c => c.EverSpun) != spunBefore)
            UpdateVisibility();
    }

    private bool InUse(FanChannelViewModel c) =>
        c.Role != FanRole.Case || c.Mode != FanMode.Bios || c.EverSpun;

    private void UpdateVisibility()
    {
        var inUse = Channels.Where(InUse).ToList();
        _session.FanTargets = inUse.Select(c => c.Id).ToList();

        var visible = ShowEmpty ? Channels.ToList() : inUse;
        HiddenCount = Channels.Count - inUse.Count;

        if (!visible.SequenceEqual(VisibleChannels))
        {
            VisibleChannels.Clear();
            foreach (var c in visible)
                VisibleChannels.Add(c);
        }
    }

    partial void OnShowEmptyChanged(bool value)
    {
        if (_settings.Current.ShowEmptyFanHeaders != value)
            _settings.Update(s => s with { ShowEmptyFanHeaders = value });
        UpdateVisibility();
    }

    private void UpdateActivePreset()
    {
        var used = Channels.Where(InUse).ToList();
        string? active = null;
        if (used.Count > 0)
        {
            if (used.All(c => c.Mode == FanMode.Bios))
                active = "Bios";
            else
            {
                foreach (var preset in Enum.GetValues<FanPreset>())
                {
                    if (used.All(c => MatchesPreset(c, preset)))
                    {
                        active = preset.ToString();
                        break;
                    }
                }
            }
        }

        IsSilentActive = active == nameof(FanPreset.Silent);
        IsBalancedActive = active == nameof(FanPreset.Balanced);
        IsPerformanceActive = active == nameof(FanPreset.Performance);
        IsMaximumActive = active == nameof(FanPreset.Maximum);
        IsBiosActive = active == "Bios";
        ActivePresetText = active switch
        {
            nameof(FanPreset.Silent) => "Modo activo: Silencioso",
            nameof(FanPreset.Balanced) => "Modo activo: Equilibrado",
            nameof(FanPreset.Performance) => "Modo activo: Rendimiento",
            nameof(FanPreset.Maximum) => "Modo activo: Máximo",
            "Bios" => "Modo activo: todo lo controla la BIOS",
            _ => "Configuración personalizada",
        };
    }

    private static bool MatchesPreset(FanChannelViewModel c, FanPreset preset)
    {
        var expected = FanDefaults.ApplyPreset(c.ToDto(), preset);
        if (expected.Mode != c.Mode)
            return false;

        return expected.Mode switch
        {
            FanMode.Fixed => Math.Abs(expected.FixedPercent - c.FixedPercent) < 0.5,
            FanMode.Curve => expected.Curve.Count == c.Curve.Count
                             && expected.Curve.Zip(c.Curve).All(p => Math.Abs(p.First.Temperature - p.Second.Temperature) < 0.5
                                                                     && Math.Abs(p.First.Percent - p.Second.Percent) < 0.5),
            _ => true,
        };
    }

    // ------------------------------------------------------------------ edición y guardado

    internal void OnChannelEdited(FanChannelViewModel channel, bool control)
    {
        // En un grupo, el modo y la curva se comparten.
        if (control && channel.GroupId.Length > 0)
        {
            foreach (var other in Channels.Where(c => c != channel && c.GroupId == channel.GroupId))
                other.CopyControlFrom(channel);
        }

        ScheduleSave();
        UpdateActivePreset();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private SafetySettingsDto SafetyDto() => new()
    {
        CpuCriticalCelsius = (float)CpuCritical,
        GpuCriticalCelsius = (float)GpuCritical,
        CpuWarningCelsius = (float)CpuWarning,
        GpuWarningCelsius = (float)GpuWarning,
        RecoveryMarginCelsius = (float)RecoveryMargin,
        RecoveryHoldSeconds = (int)Math.Round(RecoveryHold),
        SensorTimeoutSeconds = (int)Math.Round(SensorTimeout),
    };

    private async Task SaveNowAsync()
    {
        if (_session.FanSettings is null || Channels.Count == 0)
            return;

        var dto = new FanSettingsDto(Channels.Select(c => c.ToDto()).ToList(), _groups.ToList(), SafetyDto());
        IsSaving = true;
        try
        {
            await _session.SaveFanSettingsAsync(dto);
            ErrorText = null;
        }
        catch (Exception ex)
        {
            UiLog.Error("No se pudo guardar la configuración de ventiladores", ex);
            ErrorText = $"No se pudo guardar: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    partial void OnCpuCriticalChanged(double value) => SafetyEdited();
    partial void OnGpuCriticalChanged(double value) => SafetyEdited();
    partial void OnCpuWarningChanged(double value) => SafetyEdited();
    partial void OnGpuWarningChanged(double value) => SafetyEdited();
    partial void OnRecoveryMarginChanged(double value) => SafetyEdited();
    partial void OnRecoveryHoldChanged(double value) => SafetyEdited();
    partial void OnSensorTimeoutChanged(double value) => SafetyEdited();

    private void SafetyEdited()
    {
        RefreshSafetyTexts();
        if (!_loadingSafety)
            ScheduleSave();
    }

    private void RefreshSafetyTexts()
    {
        OnPropertyChanged(nameof(CpuCriticalText));
        OnPropertyChanged(nameof(GpuCriticalText));
        OnPropertyChanged(nameof(CpuWarningText));
        OnPropertyChanged(nameof(GpuWarningText));
        OnPropertyChanged(nameof(RecoveryMarginText));
        OnPropertyChanged(nameof(RecoveryHoldText));
        OnPropertyChanged(nameof(SensorTimeoutText));
    }

    [RelayCommand]
    private void ResetSafety()
    {
        var d = FanDefaults.DefaultSafety();
        _loadingSafety = true;
        CpuCritical = d.CpuCriticalCelsius;
        GpuCritical = d.GpuCriticalCelsius;
        CpuWarning = d.CpuWarningCelsius;
        GpuWarning = d.GpuWarningCelsius;
        RecoveryMargin = d.RecoveryMarginCelsius;
        RecoveryHold = d.RecoveryHoldSeconds;
        SensorTimeout = d.SensorTimeoutSeconds;
        _loadingSafety = false;
        RefreshSafetyTexts();
        ScheduleSave();
    }

    // ------------------------------------------------------------------ modos rápidos

    [RelayCommand]
    private async Task ApplyPreset(string key)
    {
        // Un modo rápido sustituye a lo que hubiera pendiente de guardar.
        _saveTimer.Stop();
        try
        {
            if (key == "Bios")
            {
                foreach (var c in Channels.Where(InUse))
                    c.SetModeQuietly(FanMode.Bios);
                await SaveNowAsync();
            }
            else
            {
                await _session.ApplyPresetAsync(Enum.Parse<FanPreset>(key), Channels.Where(InUse).Select(c => c.Id).ToList());
            }

            ErrorText = null;
        }
        catch (Exception ex)
        {
            UiLog.Error("No se pudo aplicar el modo", ex);
            ErrorText = $"No se pudo aplicar el modo: {ex.Message}";
        }

        UpdateActivePreset();
    }

    // ------------------------------------------------------------------ editor de curva

    [RelayCommand]
    private void Edit(FanChannelViewModel channel) => Editing = channel;

    [RelayCommand]
    private void CloseEditor() => Editing = null;

    [RelayCommand]
    private void UseCurvePreset(string key)
    {
        if (Editing is not { } ch)
            return;

        ch.Curve = key == "Plana"
            ? [new CurvePointDto(30, 100), new CurvePointDto(90, 100)]
            : FanDefaults.Curve(ch.Role, Enum.Parse<FanPreset>(key));
        if (ch.Mode != FanMode.Curve)
            ch.Mode = FanMode.Curve;
    }

    // ------------------------------------------------------------------ grupos

    private void RebuildGroups()
    {
        var options = new List<GroupOption> { new("", "Sin grupo") };
        options.AddRange(_groups.Select(g => new GroupOption(g.Id, g.Name)));
        options.Add(new GroupOption(NewGroupKey, "Nuevo grupo…"));

        if (!options.SequenceEqual(GroupOptions))
        {
            GroupOptions.Clear();
            foreach (var o in options)
                GroupOptions.Add(o);

        }

        // El ComboBox pierde la selección al rehacer la lista: se le recuerda.
        foreach (var c in Channels)
            c.NotifyGroup();

        foreach (var c in Channels)
            c.GroupName = _groups.FirstOrDefault(g => g.Id == c.GroupId)?.Name;

        // Tarjetas de grupos.
        var ids = _groups.Select(g => g.Id).ToList();
        foreach (var stale in Groups.Where(g => !ids.Contains(g.Id)).ToList())
            Groups.Remove(stale);

        foreach (var g in _groups)
        {
            var vm = Groups.FirstOrDefault(x => x.Id == g.Id);
            if (vm is null)
            {
                vm = new FanGroupViewModel(this, g.Id);
                Groups.Add(vm);
            }

            vm.Loading = true;
            vm.Name = g.Name;
            vm.Members = string.Join(" · ", g.ChannelIds.Select(id => _byId.GetValueOrDefault(id)?.Name ?? id));
            vm.Loading = false;
        }

        HasGroups = Groups.Count > 0;
    }

    internal void OnGroupSelected(FanChannelViewModel channel, string groupId)
    {
        if (groupId == NewGroupKey)
        {
            var n = 1;
            while (_groups.Any(g => g.Name == $"Grupo {n}"))
                n++;
            groupId = "g" + Guid.NewGuid().ToString("N")[..8];
            _groups.Add(new FanGroupDto(groupId, $"Grupo {n}", []));
        }

        // Fuera de su grupo anterior.
        _groups = _groups.Select(g => g with { ChannelIds = g.ChannelIds.Where(id => id != channel.Id).ToList() }).ToList();

        if (groupId.Length > 0)
        {
            var index = _groups.FindIndex(g => g.Id == groupId);
            if (index >= 0)
            {
                // Al unirse a un grupo se adopta su modo y su curva.
                var donor = Channels.FirstOrDefault(c => c != channel && _groups[index].ChannelIds.Contains(c.Id));
                if (donor is not null)
                    channel.CopyControlFrom(donor);
                _groups[index] = _groups[index] with { ChannelIds = [.. _groups[index].ChannelIds, channel.Id] };
            }
        }

        _groups.RemoveAll(g => g.ChannelIds.Count == 0);

        // La selección de "Nuevo grupo…" se cambia por el grupo recién creado cuando el ComboBox termine.
        var finalId = groupId;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            channel.SetGroupQuietly(finalId);
            RebuildGroups();
        }, DispatcherPriority.Background);

        ScheduleSave();
    }

    internal void RenameGroup(string id, string name)
    {
        var index = _groups.FindIndex(g => g.Id == id);
        if (index < 0 || string.IsNullOrWhiteSpace(name))
            return;

        _groups[index] = _groups[index] with { Name = name.Trim() };
        RebuildGroups();
        ScheduleSave();
    }

    [RelayCommand]
    private void DeleteGroup(FanGroupViewModel group)
    {
        _groups.RemoveAll(g => g.Id == group.Id);
        foreach (var c in Channels.Where(c => c.GroupId == group.Id))
            c.SetGroupQuietly("");
        RebuildGroups();
        ScheduleSave();
    }

    // ------------------------------------------------------------------ calibración

    private async Task LoadCalibrationAsync()
    {
        try
        {
            OnCalibration(await _session.Api.GetCalibrationAsync());
        }
        catch (Exception ex) when (ex is IpcRemoteException or InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
        {
            // Sin datos de calibración todavía.
        }
    }

    private void OnCalibration(CalibrationStatusDto status)
    {
        _calibration = status;
        CalibrationRunning = status.Running;
        CalibrationProgress = status.Progress * 100;
        CalibrationMessage = status.Message ?? (status.Results.Count > 0 ? "Última calibración guardada." : null);

        CalibrationRows.Clear();
        foreach (var r in status.Results)
        {
            var name = _byId.GetValueOrDefault(r.ChannelId)?.Name ?? r.ChannelId;
            var ok = r.Status == "Completada";
            var spinning = r.Points.Where(p => p.Rpm >= 60).Select(p => p.Percent).DefaultIfEmpty(float.NaN).Min();
            CalibrationRows.Add(new CalibrationRowViewModel(
                name,
                float.IsNaN(spinning) ? "—" : $"{spinning:0} %",
                r.StartPercent is { } s ? $"{s:0} %" : "—",
                r.MaxRpm is { } m ? m.ToString("#,0", Format.Es) : "—",
                r.Status,
                ok));
        }

        HasCalibrationRows = CalibrationRows.Count > 0;
        CanApplyCalibration = !status.Running && status.Results.Any(r => r.Status == "Completada");
    }

    [RelayCommand]
    private Task CalibrateAll() =>
        StartCalibrationAsync(Channels.Where(c => InUse(c) && c.HasRpmSensor).Select(c => c.Id).ToList());

    [RelayCommand]
    private Task CalibrateChannel(FanChannelViewModel channel) => StartCalibrationAsync([channel.Id]);

    private async Task StartCalibrationAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            ErrorText = "No hay ventiladores con sensor de RPM para calibrar.";
            return;
        }

        try
        {
            OnCalibration(await _session.Api.StartCalibrationAsync(new CalibrationRequest(ids)));
            ErrorText = null;
        }
        catch (Exception ex)
        {
            ErrorText = $"No se pudo calibrar: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CancelCalibration()
    {
        try
        {
            OnCalibration(await _session.Api.CancelCalibrationAsync());
        }
        catch (Exception ex)
        {
            ErrorText = $"No se pudo cancelar: {ex.Message}";
        }
    }

    /// <summary>Pone como mínimo de cada ventilador calibrado el % más bajo al que aún giraba, con margen.</summary>
    [RelayCommand]
    private void ApplyCalibration()
    {
        if (_calibration is null)
            return;

        var applied = 0;
        foreach (var r in _calibration.Results.Where(r => r.Status == "Completada"))
        {
            if (!_byId.TryGetValue(r.ChannelId, out var ch))
                continue;

            var spinning = r.Points.Where(p => p.Rpm >= 60).Select(p => p.Percent).DefaultIfEmpty(float.NaN).Min();
            if (float.IsNaN(spinning))
                continue;

            var minimum = Math.Min(100, spinning + 5);
            var start = Math.Min(100, Math.Max(minimum, (r.StartPercent ?? spinning) + 5));
            ch.SetLimitsQuietly(minimum, start);
            applied++;
        }

        if (applied > 0)
        {
            ScheduleSave();
            CalibrationMessage = $"Mínimos calibrados aplicados a {applied} ventilador(es).";
        }
    }
}
