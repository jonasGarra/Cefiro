using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Cefiro.App.Infrastructure;
using Cefiro.App.Services;
using Cefiro.Core.Lighting;
using Cefiro.Ipc.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cefiro.App.ViewModels;

/// <summary>Una zona de LEDs (un cabezal ARGB de la placa, un módulo de RAM…).</summary>
public sealed partial class LedZoneViewModel : ObservableObject
{
    public LedZoneViewModel(LightDeviceViewModel device, LightingZoneDto zone, string displayName)
    {
        Device = device;
        Index = zone.Index;
        Name = zone.Name;
        DisplayName = displayName;
        StartLed = zone.StartLed;
        Resizable = zone.Resizable;
        MinLeds = zone.MinLeds;
        MaxLeds = zone.MaxLeds;
        LedCount = zone.LedCount;
    }

    public LightDeviceViewModel Device { get; }
    public int Index { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public int StartLed { get; }
    public bool Resizable { get; }
    public int MinLeds { get; }
    public int MaxLeds { get; }

    [ObservableProperty] public partial int LedCount { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Color> Colors { get; set; } = [];
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial int SelectedLed { get; set; } = -1;

    public string LedCountText => LedCount == 1 ? "1 LED" : $"{LedCount} LEDs";

    partial void OnLedCountChanged(int value) => OnPropertyChanged(nameof(LedCountText));
}

/// <summary>Un dispositivo de OpenRGB con sus zonas y su vista previa en directo.</summary>
public sealed partial class LightDeviceViewModel : ObservableObject
{
    public LightDeviceViewModel(LightingDeviceDto device, string displayName)
    {
        Key = device.Key;
        Name = displayName;
        Vendor = device.Vendor;
        Kind = device.Kind;
        Location = device.Location;
        LedCount = device.LedCount;
        NativeModes = device.NativeModes;
    }

    public string Key { get; }
    public string Name { get; }
    public string Vendor { get; }
    public string Kind { get; }
    public string Location { get; }
    public int LedCount { get; }
    public IReadOnlyList<string> NativeModes { get; }
    public ObservableCollection<LedZoneViewModel> Zones { get; } = [];

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string EffectText { get; set; } = "";

    public string KindText => Kind switch
    {
        "Motherboard" => "Placa base",
        "Dram" => "Memoria RAM",
        "Gpu" => "Tarjeta gráfica",
        "Cooler" => "Refrigeración",
        "LedStrip" => "Tira LED",
        "Keyboard" => "Teclado",
        "Mouse" => "Ratón",
        "Headset" => "Auriculares",
        "Case" => "Caja",
        _ => "Dispositivo RGB",
    };

    public string Glyph => Kind switch
    {
        "Motherboard" or "Gpu" => "DeveloperBoard24",
        "Dram" => "Board24",
        "Keyboard" => "Keyboard24",
        _ => "LightbulbFilament24",
    };

    public string Subtitle => $"{KindText} · {LedCount} LEDs · {Vendor}";
}

/// <summary>Una tarjeta de la galería de efectos.</summary>
public sealed partial class EffectOptionViewModel(EffectKind kind, string name, string glyph, string description, Brush preview) : ObservableObject
{
    public EffectKind Kind { get; } = kind;
    public string Name { get; } = name;
    public string Glyph { get; } = glyph;
    public string Description { get; } = description;
    public Brush Preview { get; } = preview;

    [ObservableProperty] public partial bool IsActive { get; set; }
}

/// <summary>Uno de los colores del efecto.</summary>
public sealed partial class ColorSlotViewModel : ObservableObject
{
    public ColorSlotViewModel(Color color) => Color = color;

    [ObservableProperty] public partial Color Color { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public string Hex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";

    public SolidColorBrush Brush
    {
        get
        {
            var b = new SolidColorBrush(Color);
            b.Freeze();
            return b;
        }
    }

    partial void OnColorChanged(Color value)
    {
        OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(Brush));
    }
}

public sealed record FavoriteColor(string Hex, SolidColorBrush Brush);

public sealed record SensorOption(string Id, string Label);

public sealed partial class LightingViewModel : ObservableObject, IDisposable
{
    private readonly CefiroSession _session;
    private readonly UiSettingsStore _ui;
    private readonly AudioBridge _audio;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _resizeTimer;
    private readonly Dictionary<LedZoneViewModel, int> _pendingResize = [];
    private LightingSettingsDto _settings = new();
    private LightingTargetDto? _target;
    private string _deviceSignature = "";
    private bool _loading;
    private bool _syncingColor;
    private long _lastLevelTicks;

    public LightingViewModel(CefiroSession session, UiSettingsStore ui)
    {
        _session = session;
        _ui = ui;
        _audio = new AudioBridge(session);
        _audio.LevelChanged += OnAudioLevel;
        _audio.Error += message => Application.Current?.Dispatcher.BeginInvoke(() => AudioText = message);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveNowAsync();
        };

        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _resizeTimer.Tick += async (_, _) =>
        {
            _resizeTimer.Stop();
            await ApplyResizesAsync();
        };

        Effects =
        [
            Option(EffectKind.Static, "Estático", "Circle24", "Un color fijo, o un degradado si eliges varios.", Gradient("#00E5FF", "#00E5FF")),
            Option(EffectKind.Breathing, "Respiración", "HeartPulse24", "Se enciende y se apaga suavemente.", Gradient("#0000E5FF", "#00E5FF", "#0000E5FF")),
            Option(EffectKind.Rainbow, "Arcoíris", "Color24", "Todos los colores recorriendo los LEDs.", Gradient("#FF0040", "#FFB300", "#00E676", "#00E5FF", "#7C4DFF", "#FF2E88")),
            Option(EffectKind.Wave, "Ola", "ArrowTrending24", "Una ola de luz que se desplaza.", Gradient("#1000E5FF", "#00E5FF", "#7C4DFF", "#107C4DFF")),
            Option(EffectKind.ColorCycle, "Ciclo de color", "ArrowRepeatAll24", "Pasa de un color a otro poco a poco.", Gradient("#00E5FF", "#7C4DFF", "#FF2E88")),
            Option(EffectKind.Comet, "Cometa", "Sparkle24", "Una luz con estela que recorre la tira.", Gradient("#0000E5FF", "#3000E5FF", "#FFFFFF")),
            Option(EffectKind.Temperature, "Temperatura", "Temperature24", "Cian en frío y rosa en caliente, según la CPU o la GPU.", Gradient("#00E5FF", "#FFB300", "#FF2E88")),
            Option(EffectKind.Music, "Música", "MusicNote224", "Reacciona al sonido del PC.", Gradient("#7C4DFF", "#00E5FF", "#00E676", "#FFB300", "#FF2E88")),
            Option(EffectKind.DeviceMode, "Modo del dispositivo", "DeveloperBoard24", "Efectos del propio hardware. No gastan CPU.", Gradient("#40FFFFFF", "#A0FFFFFF", "#40FFFFFF")),
            Option(EffectKind.Off, "Apagado", "LightbulbFilament24", "Todo apagado.", Gradient("#20FFFFFF", "#20FFFFFF")),
        ];

        session.LightingChanged += OnLightingChanged;
        session.LightingFrameUpdated += OnFrame;
        session.Loaded += BuildSensorOptions;
        ui.Changed += _ => RefreshTemperatureTexts();

        if (session.IsLoaded)
        {
            BuildSensorOptions();
            OnLightingChanged();
        }
    }

    public CefiroSession Session => _session;
    public IReadOnlyList<EffectOptionViewModel> Effects { get; }
    public ObservableCollection<LightDeviceViewModel> Devices { get; } = [];
    public ObservableCollection<ColorSlotViewModel> ColorSlots { get; } = [];
    public ObservableCollection<FavoriteColor> Favorites { get; } = [];

    // ---- Estado general ----
    [ObservableProperty] public partial bool Connected { get; set; }
    [ObservableProperty] public partial bool HasState { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Conectando con OpenRGB…";
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial double Brightness { get; set; } = 100;
    [ObservableProperty] public partial double SpeedMultiplier { get; set; } = 1;
    [ObservableProperty] public partial bool SyncAll { get; set; } = true;

    // ---- Destino que se está editando ----
    [ObservableProperty] public partial string TargetText { get; set; } = "Todos los dispositivos";
    [ObservableProperty] public partial bool IsPartialTarget { get; set; }

    // ---- Efecto del destino ----
    [ObservableProperty] public partial EffectKind Kind { get; set; } = EffectKind.Static;
    [ObservableProperty] public partial double EffectSpeed { get; set; } = 1;
    [ObservableProperty] public partial double EffectBrightness { get; set; } = 100;
    [ObservableProperty] public partial bool Reverse { get; set; }
    [ObservableProperty] public partial double MinTemperature { get; set; } = 35;
    [ObservableProperty] public partial double MaxTemperature { get; set; } = 85;
    [ObservableProperty] public partial double Sensitivity { get; set; } = 1;

    private string _sensorId = "";
    private string _nativeMode = "";

    /// <summary>Sensor del efecto Temperatura ("" = CPU). Los null de los ComboBox se ignoran.</summary>
    public string SensorId
    {
        get => _sensorId;
        set
        {
            if (value is not null && SetProperty(ref _sensorId, value))
                EffectEdited();
        }
    }

    /// <summary>Modo propio del hardware. Los null de los ComboBox (al cambiar su lista) se ignoran.</summary>
    public string NativeMode
    {
        get => _nativeMode;
        set
        {
            if (value is not null && SetProperty(ref _nativeMode, value))
                EffectEdited();
        }
    }
    [ObservableProperty] public partial IReadOnlyList<string> NativeModes { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SensorOption> SensorOptions { get; set; } = [];

    // ---- Color ----
    [ObservableProperty] public partial ColorSlotViewModel? SelectedSlot { get; set; }
    [ObservableProperty] public partial Color WheelColor { get; set; } = Color.FromRgb(0x00, 0xE5, 0xFF);
    [ObservableProperty] public partial string Hex { get; set; } = "#00E5FF";

    // ---- Modo noche ----
    [ObservableProperty] public partial bool NightEnabled { get; set; }
    [ObservableProperty] public partial string NightStart { get; set; } = "23:30";
    [ObservableProperty] public partial string NightEnd { get; set; } = "07:30";
    [ObservableProperty] public partial bool NightTurnOff { get; set; }
    [ObservableProperty] public partial double NightDim { get; set; } = 15;
    [ObservableProperty] public partial string NightStatusText { get; set; } = "";
    [ObservableProperty] public partial bool NightTimesValid { get; set; } = true;

    // ---- Audio ----
    [ObservableProperty] public partial double AudioLevel { get; set; }
    [ObservableProperty] public partial bool AudioActive { get; set; }
    [ObservableProperty] public partial string AudioText { get; set; } = "";

    public bool ShowColors => Kind is not (EffectKind.Off or EffectKind.Rainbow);
    public bool ShowSpeed => Kind is EffectKind.Breathing or EffectKind.Rainbow or EffectKind.Wave or EffectKind.ColorCycle or EffectKind.Comet or EffectKind.DeviceMode;
    public bool ShowReverse => Kind is EffectKind.Rainbow or EffectKind.Wave or EffectKind.Comet;
    public bool ShowBrightness => Kind != EffectKind.Off;
    public bool ShowTemperature => Kind == EffectKind.Temperature;
    public bool ShowMusic => Kind == EffectKind.Music;
    public bool ShowNative => Kind == EffectKind.DeviceMode;
    public bool CanAddColor => ColorSlots.Count < 4;
    public bool CanRemoveColor => ColorSlots.Count > 1;

    public string EffectHint => Kind switch
    {
        EffectKind.Static => "Con un color se pinta todo igual; con varios, un degradado a lo largo de cada tira.",
        EffectKind.Breathing => "Respira con el primer color; si añades más, va pasando de uno a otro.",
        EffectKind.Wave => "Un color: ola de brillo. Varios: ola de color.",
        EffectKind.ColorCycle => "Con un solo color recorre todo el arcoíris; con varios, va de uno a otro.",
        EffectKind.Comet => "El primer color es el cometa y el segundo, el fondo.",
        EffectKind.Temperature => "Con dos o más colores usas tu propio degradado de frío a caliente.",
        EffectKind.Music => "Con dos o más colores usas tu propia paleta; si no, la de Céfiro.",
        EffectKind.DeviceMode => "Lo ejecuta el propio hardware: no gasta CPU aunque Céfiro esté cerrado.",
        EffectKind.Rainbow => "Todos los colores a la vez, desplazándose.",
        _ => "Los LEDs se apagan.",
    };

    public string BrightnessText => $"{Brightness:0} %";
    public string SpeedMultiplierText => SpeedMultiplier.ToString("0.0#", Format.Es) + "×";
    public string EffectSpeedText => EffectSpeed.ToString("0.0", Format.Es) + "×";
    public string EffectBrightnessText => $"{EffectBrightness:0} %";
    public string MinTemperatureText => Format.Temperature((float)MinTemperature, 0);
    public string MaxTemperatureText => Format.Temperature((float)MaxTemperature, 0);
    public string SensitivityText => Sensitivity.ToString("0.0", Format.Es) + "×";
    public string NightDimText => $"{NightDim:0} %";

    private static EffectOptionViewModel Option(EffectKind kind, string name, string glyph, string description, Brush preview) =>
        new(kind, name, glyph, description, preview);

    private static Brush Gradient(params string[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (var i = 0; i < colors.Length; i++)
        {
            var c = (Color)ColorConverter.ConvertFromString(colors[i]);
            brush.GradientStops.Add(new GradientStop(c, colors.Length == 1 ? 0 : (double)i / (colors.Length - 1)));
        }

        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ carga

    private void BuildSensorOptions()
    {
        if (_session.Catalog is not { } catalog)
            return;

        var list = new List<SensorOption> { new("", "Procesador (por defecto)") };
        if (catalog.Keys.GpuTemperature is { } gpu)
            list.Add(new SensorOption(gpu, "Tarjeta gráfica"));
        list.AddRange(catalog.Sensors
            .Where(s => s.Kind == SensorKind.Temperature && s.Id != catalog.Keys.GpuTemperature)
            .Select(s => new SensorOption(s.Id, $"{s.Name} · {s.Device}")));
        SensorOptions = list;
    }

    private void OnLightingChanged()
    {
        // Cambios del usuario pendientes de guardar: el guardado traerá la versión buena.
        if (_saveTimer.IsEnabled || _session.Lighting is not { } state)
            return;

        Load(state);
    }

    private void Load(LightingStateDto state)
    {
        _loading = true;
        try
        {
            HasState = true;
            Connected = state.Connected;
            ErrorText = state.Connected ? null : state.Error;
            StatusText = state.Connected
                ? $"{state.Devices.Count} dispositivo{(state.Devices.Count == 1 ? "" : "s")} · {state.ServerName ?? "OpenRGB"} · protocolo {state.ProtocolVersion}"
                : "OpenRGB no está disponible";

            _settings = state.Settings;
            Enabled = _settings.Enabled;
            Brightness = _settings.Brightness;
            SpeedMultiplier = _settings.SpeedMultiplier;
            SyncAll = _settings.SyncAll;

            Favorites.Clear();
            foreach (var hex in _settings.Favorites)
                Favorites.Add(new FavoriteColor(hex, BrushOf(hex)));

            NightEnabled = _settings.Night.Enabled;
            NightStart = _settings.Night.Start;
            NightEnd = _settings.Night.End;
            NightTurnOff = _settings.Night.Action == NightAction.TurnOff;
            NightDim = _settings.Night.DimBrightness;
            NightTimesValid = true;
            NightStatusText = !_settings.Night.Enabled ? "Desactivado"
                : state.NightActive ? "Activo ahora mismo"
                : $"Se activará a las {_settings.Night.Start}";

            RebuildDevices(state.Devices);

            if (_settings.SyncAll)
                _target = null;
            else if (_target is null || Devices.All(d => d.Key != _target.DeviceKey))
                _target = Devices.FirstOrDefault() is { } first ? new LightingTargetDto(first.Key) : null;

            var effect = EffectForTarget();
            if (ColorSlots.Count == 0 || !SameEffect(effect, CurrentEffect()))
                LoadEffect(effect);
        }
        finally
        {
            _loading = false;
        }

        UpdateTarget();
        UpdateDeviceEffectTexts();
        RefreshTiles();
        UpdateAudio();
    }

    private void RebuildDevices(IReadOnlyList<LightingDeviceDto> devices)
    {
        var signature = string.Join(";", devices.Select(d => d.Key + ":" + string.Join(",", d.Zones.Select(z => $"{z.Index}/{z.LedCount}"))));
        if (signature == _deviceSignature)
            return;
        _deviceSignature = signature;

        var layout = _ui.Current.Layout;
        Devices.Clear();
        foreach (var group in devices.GroupBy(d => d.Name))
        {
            var number = 1;
            foreach (var d in group)
            {
                var name = group.Count() > 1 ? $"{d.Name} ({number++})" : d.Name;
                var vm = new LightDeviceViewModel(d, name);
                foreach (var z in d.Zones)
                    vm.Zones.Add(new LedZoneViewModel(vm, z, ZoneLabel(z.Name, layout)));
                Devices.Add(vm);
            }
        }

        OnFrame();
    }

    private static string ZoneLabel(string zone, CaseLayout layout)
    {
        if (string.Equals(zone, layout.CaseLightZone, StringComparison.OrdinalIgnoreCase))
            return $"{zone} · Ventiladores de la caja";
        if (string.Equals(zone, layout.AioLightZone, StringComparison.OrdinalIgnoreCase))
            return $"{zone} · Líquida (bomba y radiador)";
        if (zone.StartsWith("JRGB", StringComparison.OrdinalIgnoreCase))
            return $"{zone} · Tira de 12 V";
        if (zone.Contains("DRAM", StringComparison.OrdinalIgnoreCase))
            return "Módulo de memoria";
        return zone;
    }

    private void OnFrame()
    {
        if (_session.LightingFrame is not { } frame)
            return;

        foreach (var colors in frame.Devices)
        {
            var device = Devices.FirstOrDefault(d => d.Key == colors.DeviceKey);
            if (device is null)
                continue;

            var all = SchematicViewModel.Decode(colors.Colors);
            foreach (var zone in device.Zones)
            {
                if (zone.StartLed + zone.LedCount <= all.Count)
                    zone.Colors = all.Skip(zone.StartLed).Take(zone.LedCount).ToArray();
            }
        }
    }

    // ------------------------------------------------------------------ destino

    private static bool SameTarget(LightingTargetDto a, LightingTargetDto b) =>
        a.DeviceKey == b.DeviceKey && a.ZoneIndex == b.ZoneIndex && a.LedIndex == b.LedIndex;

    private static bool IsWholeDevice(LightingTargetDto t) => t.ZoneIndex is null && t.LedIndex is null;

    /// <summary>Efecto que se ve en el destino: su capa, o la del dispositivo, o el sincronizado.</summary>
    private EffectDto EffectForTarget()
    {
        if (_settings.SyncAll || _target is null)
            return _settings.SyncEffect;

        var exact = _settings.Layers.LastOrDefault(l => SameTarget(l.Target, _target));
        if (exact is not null)
            return exact.Effect;

        var device = _settings.Layers.LastOrDefault(l => l.Target.DeviceKey == _target.DeviceKey && IsWholeDevice(l.Target));
        return device?.Effect ?? _settings.SyncEffect;
    }

    private void UpdateTarget()
    {
        var device = _target is null ? null : Devices.FirstOrDefault(d => d.Key == _target.DeviceKey);
        var zone = device is null || _target?.ZoneIndex is null ? null : device.Zones.FirstOrDefault(z => z.Index == _target.ZoneIndex);

        TargetText = device is null
            ? "Todos los dispositivos"
            : zone is null
                ? device.Name
                : _target!.LedIndex is { } led
                    ? $"{device.Name} · {zone.Name} · LED {led - zone.StartLed + 1}"
                    : $"{device.Name} · {zone.Name}";
        IsPartialTarget = _target is not null && !IsWholeDevice(_target);

        foreach (var d in Devices)
        {
            d.IsSelected = !SyncAll && d == device && zone is null;
            foreach (var z in d.Zones)
            {
                z.IsSelected = !SyncAll && z == zone;
                z.SelectedLed = z == zone && _target?.LedIndex is { } l ? l - z.StartLed : -1;
            }
        }

        // Modos propios: los del dispositivo elegido o, sincronizado, los de todos.
        NativeModes = device is not null
            ? device.NativeModes
            : Devices.SelectMany(d => d.NativeModes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void UpdateDeviceEffectTexts()
    {
        foreach (var d in Devices)
        {
            EffectDto effect;
            if (_settings.SyncAll)
                effect = _settings.SyncEffect;
            else
                effect = _settings.Layers.LastOrDefault(l => l.Target.DeviceKey == d.Key && IsWholeDevice(l.Target))?.Effect
                         ?? new EffectDto { Kind = EffectKind.Off };

            var name = Effects.FirstOrDefault(e => e.Kind == effect.Kind)?.Name ?? effect.Kind.ToString();
            if (effect.Kind == EffectKind.DeviceMode && effect.NativeMode is { } native)
                name = $"{name}: {native}";
            var parts = _settings.SyncAll ? 0 : _settings.Layers.Count(l => l.Target.DeviceKey == d.Key && !IsWholeDevice(l.Target));
            d.EffectText = !_settings.Enabled ? "Apagado" : parts > 0 ? $"{name} + {parts} ajuste{(parts == 1 ? "" : "s")} por zona" : name;
        }
    }

    private void SelectTarget(LightingTargetDto target)
    {
        if (SyncAll)
            return;

        _target = target;
        LoadEffect(EffectForTarget());
        UpdateTarget();
        RefreshTiles();
    }

    [RelayCommand]
    private void SelectDevice(LightDeviceViewModel device) => SelectTarget(new LightingTargetDto(device.Key));

    [RelayCommand]
    private void SelectZone(LedZoneViewModel zone) => SelectTarget(new LightingTargetDto(zone.Device.Key, zone.Index));

    /// <summary>Clic sobre un LED concreto de la vista previa.</summary>
    public void SelectLed(LedZoneViewModel zone, int index)
    {
        if (index < 0 || index >= zone.LedCount)
            return;
        SelectTarget(new LightingTargetDto(zone.Device.Key, zone.Index, zone.StartLed + index));
    }

    /// <summary>Quita el ajuste propio de la zona o el LED: vuelve a verse el del dispositivo.</summary>
    [RelayCommand]
    private void RemoveTargetLayer()
    {
        if (_target is null || IsWholeDevice(_target))
            return;

        var target = _target;
        _settings = _settings with { Layers = _settings.Layers.Where(l => !SameTarget(l.Target, target)).ToList() };
        _target = new LightingTargetDto(target.DeviceKey);
        LoadEffect(EffectForTarget());
        UpdateTarget();
        UpdateDeviceEffectTexts();
        RefreshTiles();
        ScheduleSave();
    }

    // ------------------------------------------------------------------ efecto

    private EffectDto CurrentEffect() => new()
    {
        Kind = Kind,
        Colors = ColorSlots.Count > 0 ? ColorSlots.Select(c => c.Hex).ToList() : ["#00E5FF"],
        Speed = (float)EffectSpeed,
        Brightness = (float)EffectBrightness,
        Reverse = Reverse,
        SensorId = string.IsNullOrEmpty(SensorId) ? null : SensorId,
        MinTemperature = (float)MinTemperature,
        MaxTemperature = (float)MaxTemperature,
        Sensitivity = (float)Sensitivity,
        NativeMode = string.IsNullOrEmpty(NativeMode) ? null : NativeMode,
    };

    private static bool SameEffect(EffectDto a, EffectDto b) =>
        a.Kind == b.Kind
        && a.Colors.SequenceEqual(b.Colors, StringComparer.OrdinalIgnoreCase)
        && Math.Abs(a.Speed - b.Speed) < 0.001
        && Math.Abs(a.Brightness - b.Brightness) < 0.01
        && a.Reverse == b.Reverse
        && a.SensorId == b.SensorId
        && Math.Abs(a.MinTemperature - b.MinTemperature) < 0.01
        && Math.Abs(a.MaxTemperature - b.MaxTemperature) < 0.01
        && Math.Abs(a.Sensitivity - b.Sensitivity) < 0.001
        && a.NativeMode == b.NativeMode;

    private void LoadEffect(EffectDto e)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            Kind = e.Kind;
            EffectSpeed = e.Speed;
            EffectBrightness = e.Brightness;
            Reverse = e.Reverse;
            SensorId = e.SensorId ?? "";
            MinTemperature = e.MinTemperature;
            MaxTemperature = e.MaxTemperature;
            Sensitivity = e.Sensitivity;
            NativeMode = e.NativeMode ?? "";

            var selected = SelectedSlot is null ? 0 : Math.Max(0, ColorSlots.IndexOf(SelectedSlot));
            ColorSlots.Clear();
            foreach (var hex in e.Colors.Count > 0 ? e.Colors : ["#00E5FF"])
                ColorSlots.Add(new ColorSlotViewModel(ParseColor(hex)));
            SelectedSlot = ColorSlots[Math.Min(selected, ColorSlots.Count - 1)];
            OnPropertyChanged(nameof(CanAddColor));
            OnPropertyChanged(nameof(CanRemoveColor));
        }
        finally
        {
            _loading = wasLoading;
        }

        SyncWheelFromSlot();
    }

    private void EffectEdited()
    {
        if (_loading)
            return;

        var effect = CurrentEffect();
        if (_settings.SyncAll || _target is null)
        {
            _settings = _settings with { SyncEffect = effect };
        }
        else
        {
            var target = _target;
            var layers = _settings.Layers
                .Where(l => !SameTarget(l.Target, target))
                .Append(new LightingLayerDto(target, effect))
                .OrderBy(l => l.Target.ZoneIndex is null ? 0 : l.Target.LedIndex is null ? 1 : 2)
                .ToList();
            _settings = _settings with { Layers = layers };
        }

        ScheduleSave();
        RefreshTiles();
        UpdateDeviceEffectTexts();
        UpdateAudio();
    }

    private void RefreshTiles()
    {
        foreach (var e in Effects)
            e.IsActive = e.Kind == Kind;
    }

    [RelayCommand]
    private void SelectEffect(EffectOptionViewModel option)
    {
        if (option.Kind == EffectKind.DeviceMode && string.IsNullOrEmpty(NativeMode) && NativeModes.Count > 0)
        {
            _loading = true;
            NativeMode = NativeModes[0];
            _loading = false;
        }

        Kind = option.Kind;
    }

    partial void OnKindChanged(EffectKind value)
    {
        OnPropertyChanged(nameof(ShowColors));
        OnPropertyChanged(nameof(ShowSpeed));
        OnPropertyChanged(nameof(ShowReverse));
        OnPropertyChanged(nameof(ShowBrightness));
        OnPropertyChanged(nameof(ShowTemperature));
        OnPropertyChanged(nameof(ShowMusic));
        OnPropertyChanged(nameof(ShowNative));
        OnPropertyChanged(nameof(EffectHint));
        RefreshTiles();
        EffectEdited();
    }

    partial void OnEffectSpeedChanged(double value)
    {
        OnPropertyChanged(nameof(EffectSpeedText));
        EffectEdited();
    }

    partial void OnEffectBrightnessChanged(double value)
    {
        OnPropertyChanged(nameof(EffectBrightnessText));
        EffectEdited();
    }

    partial void OnReverseChanged(bool value) => EffectEdited();

    partial void OnMinTemperatureChanged(double value)
    {
        if (!_loading && value > MaxTemperature - 5)
            MaxTemperature = Math.Min(120, value + 5);
        OnPropertyChanged(nameof(MinTemperatureText));
        EffectEdited();
    }

    partial void OnMaxTemperatureChanged(double value)
    {
        if (!_loading && value < MinTemperature + 5)
            MinTemperature = Math.Max(0, value - 5);
        OnPropertyChanged(nameof(MaxTemperatureText));
        EffectEdited();
    }

    partial void OnSensitivityChanged(double value)
    {
        OnPropertyChanged(nameof(SensitivityText));
        EffectEdited();
    }

    private void RefreshTemperatureTexts()
    {
        OnPropertyChanged(nameof(MinTemperatureText));
        OnPropertyChanged(nameof(MaxTemperatureText));
    }

    // ------------------------------------------------------------------ colores

    private static Color ParseColor(string hex) =>
        Rgb.TryParseHex(hex, out var c) ? Color.FromRgb(c.R, c.G, c.B) : Color.FromRgb(0x00, 0xE5, 0xFF);

    private static SolidColorBrush BrushOf(string hex)
    {
        var b = new SolidColorBrush(ParseColor(hex));
        b.Freeze();
        return b;
    }

    partial void OnSelectedSlotChanged(ColorSlotViewModel? value)
    {
        foreach (var s in ColorSlots)
            s.IsSelected = s == value;
        SyncWheelFromSlot();
    }

    private void SyncWheelFromSlot()
    {
        if (SelectedSlot is not { } slot)
            return;

        _syncingColor = true;
        WheelColor = slot.Color;
        Hex = slot.Hex;
        _syncingColor = false;
    }

    partial void OnWheelColorChanged(Color value)
    {
        if (_syncingColor || SelectedSlot is not { } slot)
            return;

        slot.Color = value;
        _syncingColor = true;
        Hex = slot.Hex;
        _syncingColor = false;
        EffectEdited();
    }

    partial void OnHexChanged(string value)
    {
        if (_syncingColor || SelectedSlot is not { } slot)
            return;

        if (!Rgb.TryParseHex(value, out var c))
            return;

        var color = Color.FromRgb(c.R, c.G, c.B);
        slot.Color = color;
        _syncingColor = true;
        WheelColor = color;
        Hex = slot.Hex;
        _syncingColor = false;
        EffectEdited();
    }

    [RelayCommand]
    private void SelectSlot(ColorSlotViewModel slot) => SelectedSlot = slot;

    [RelayCommand]
    private void AddColor()
    {
        if (ColorSlots.Count >= 4)
            return;

        var last = ColorSlots.LastOrDefault()?.Color ?? Color.FromRgb(0x00, 0xE5, 0xFF);
        var slot = new ColorSlotViewModel(ThemeService.Shift(last, 60));
        ColorSlots.Add(slot);
        SelectedSlot = slot;
        OnPropertyChanged(nameof(CanAddColor));
        OnPropertyChanged(nameof(CanRemoveColor));
        EffectEdited();
    }

    [RelayCommand]
    private void RemoveColor()
    {
        if (ColorSlots.Count <= 1 || SelectedSlot is not { } slot)
            return;

        var index = ColorSlots.IndexOf(slot);
        ColorSlots.Remove(slot);
        SelectedSlot = ColorSlots[Math.Clamp(index - 1, 0, ColorSlots.Count - 1)];
        OnPropertyChanged(nameof(CanAddColor));
        OnPropertyChanged(nameof(CanRemoveColor));
        EffectEdited();
    }

    [RelayCommand]
    private void ApplyFavorite(FavoriteColor favorite)
    {
        if (SelectedSlot is null)
            return;
        Hex = favorite.Hex;
    }

    [RelayCommand]
    private void AddFavorite()
    {
        if (SelectedSlot is not { } slot || Favorites.Any(f => string.Equals(f.Hex, slot.Hex, StringComparison.OrdinalIgnoreCase)))
            return;

        Favorites.Add(new FavoriteColor(slot.Hex, BrushOf(slot.Hex)));
        while (Favorites.Count > 24)
            Favorites.RemoveAt(0);
        _settings = _settings with { Favorites = Favorites.Select(f => f.Hex).ToList() };
        ScheduleSave();
    }

    public void RemoveFavorite(FavoriteColor favorite)
    {
        Favorites.Remove(favorite);
        _settings = _settings with { Favorites = Favorites.Select(f => f.Hex).ToList() };
        ScheduleSave();
    }

    // ------------------------------------------------------------------ ajustes generales

    partial void OnEnabledChanged(bool value) => GlobalEdited(s => s with { Enabled = value });

    partial void OnBrightnessChanged(double value)
    {
        OnPropertyChanged(nameof(BrightnessText));
        GlobalEdited(s => s with { Brightness = (float)value });
    }

    partial void OnSpeedMultiplierChanged(double value)
    {
        OnPropertyChanged(nameof(SpeedMultiplierText));
        GlobalEdited(s => s with { SpeedMultiplier = (float)value });
    }

    partial void OnSyncAllChanged(bool value)
    {
        if (_loading)
            return;

        if (!value)
        {
            // Al separar, cada dispositivo empieza con el efecto que tenía sincronizado.
            var layers = _settings.Layers.Count > 0
                ? _settings.Layers
                : Devices.Select(d => new LightingLayerDto(new LightingTargetDto(d.Key), _settings.SyncEffect)).ToList();
            _settings = _settings with { SyncAll = false, Layers = layers };
            _target = Devices.FirstOrDefault() is { } first ? new LightingTargetDto(first.Key) : null;
        }
        else
        {
            _settings = _settings with { SyncAll = true };
            _target = null;
        }

        LoadEffect(EffectForTarget());
        UpdateTarget();
        UpdateDeviceEffectTexts();
        RefreshTiles();
        ScheduleSave();
        UpdateAudio();
    }

    private void GlobalEdited(Func<LightingSettingsDto, LightingSettingsDto> change)
    {
        if (_loading)
            return;

        _settings = change(_settings);
        UpdateDeviceEffectTexts();
        ScheduleSave();
        UpdateAudio();
    }

    partial void OnNightEnabledChanged(bool value) => NightEdited();
    partial void OnNightStartChanged(string value) => NightEdited();
    partial void OnNightEndChanged(string value) => NightEdited();
    partial void OnNightTurnOffChanged(bool value) => NightEdited();

    partial void OnNightDimChanged(double value)
    {
        OnPropertyChanged(nameof(NightDimText));
        NightEdited();
    }

    private void NightEdited()
    {
        if (_loading)
            return;

        NightTimesValid = LightingValidator.TryParseTime(NightStart, out _) && LightingValidator.TryParseTime(NightEnd, out _);
        if (!NightTimesValid)
            return;

        _settings = _settings with
        {
            Night = new NightModeDto
            {
                Enabled = NightEnabled,
                Start = NightStart.Trim(),
                End = NightEnd.Trim(),
                Action = NightTurnOff ? NightAction.TurnOff : NightAction.Dim,
                DimBrightness = (float)NightDim,
            },
        };
        ScheduleSave();
    }

    /// <summary>Encender o apagar todo (también lo usa la bandeja).</summary>
    [RelayCommand]
    private void ToggleLights() => Enabled = !Enabled;

    // ------------------------------------------------------------------ zonas

    [RelayCommand]
    private void AddLed(LedZoneViewModel zone) => ChangeLeds(zone, +1);

    [RelayCommand]
    private void RemoveLed(LedZoneViewModel zone) => ChangeLeds(zone, -1);

    private void ChangeLeds(LedZoneViewModel zone, int delta)
    {
        if (!zone.Resizable)
            return;

        var max = zone.MaxLeds > 0 ? zone.MaxLeds : 300;
        var count = Math.Clamp(zone.LedCount + delta, Math.Max(1, zone.MinLeds), max);
        if (count == zone.LedCount)
            return;

        zone.LedCount = count;
        _pendingResize[zone] = count;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private async Task ApplyResizesAsync()
    {
        foreach (var (zone, count) in _pendingResize.ToList())
        {
            _pendingResize.Remove(zone);
            try
            {
                await _session.Api.CallAsync<LightingStateDto>(Methods.ResizeZone, new ResizeZoneRequest(zone.Device.Key, zone.Index, count));
            }
            catch (Exception ex)
            {
                UiLog.Error("No se pudo cambiar el número de LEDs", ex);
                ErrorText = $"No se pudo cambiar el número de LEDs: {ex.Message}";
            }
        }

        try
        {
            await _session.RefreshLightingAsync();
        }
        catch (Exception ex)
        {
            UiLog.Error("No se pudo recargar la iluminación", ex);
        }
    }

    [RelayCommand]
    private async Task Identify(object? item)
    {
        IdentifyRequest? request = item switch
        {
            LightDeviceViewModel d => new IdentifyRequest(d.Key, null, null),
            LedZoneViewModel z => new IdentifyRequest(z.Device.Key, z.Index, null),
            _ => null,
        };
        if (request is null)
            return;

        try
        {
            await _session.Api.CallAsync(Methods.IdentifyLeds, request);
        }
        catch (Exception ex)
        {
            ErrorText = $"No se pudo identificar: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ guardado

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveNowAsync()
    {
        if (!HasState)
            return;

        try
        {
            await _session.SaveLightingAsync(_settings);
        }
        catch (Exception ex)
        {
            UiLog.Error("No se pudo guardar la iluminación", ex);
            ErrorText = $"No se pudo guardar: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ audio

    private void UpdateAudio()
    {
        var music = Connected && _settings.Enabled
                    && (_settings.SyncAll
                        ? _settings.SyncEffect.Kind == EffectKind.Music
                        : _settings.Layers.Any(l => l.Effect.Kind == EffectKind.Music));

        if (music != AudioActive || (music && !_audio.IsRunning))
        {
            _audio.SetActive(music);
            AudioActive = music;
            AudioText = music
                ? $"Escuchando: {AudioBridge.OutputName ?? "la salida de audio predeterminada"}"
                : "";
            if (!music)
                AudioLevel = 0;
        }
    }

    private void OnAudioLevel(float level)
    {
        // Como mucho 30 actualizaciones por segundo en la interfaz.
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastLevelTicks) < 33 && level > 0)
            return;
        Interlocked.Exchange(ref _lastLevelTicks, now);
        Application.Current?.Dispatcher.BeginInvoke(() => AudioLevel = Math.Clamp(level * 100, 0, 100), DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _audio.Dispose();
    }
}
