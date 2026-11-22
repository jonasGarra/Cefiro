using System.Collections.ObjectModel;
using System.Windows.Media;
using Cefiro.App.Infrastructure;
using Cefiro.Ipc.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Cefiro.App.ViewModels;

public sealed partial class FanSummaryViewModel : ObservableObject
{
    private readonly List<float?> _history = [];

    public required string ChannelId { get; init; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string RpmText { get; set; } = "—";
    [ObservableProperty] public partial string OutputText { get; set; } = "—";
    [ObservableProperty] public partial double Output { get; set; }
    [ObservableProperty] public partial string ModeText { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<float?> History { get; set; } = [];
    [ObservableProperty] public partial string Glyph { get; set; } = "WeatherSqualls24";

    public void Push(float? rpm)
    {
        _history.Add(rpm);
        if (_history.Count > 60)
            _history.RemoveAt(0);
        History = _history.ToArray();
    }
}

public enum HistoryWindow
{
    Seconds60,
    Minutes10,
    Hour1,
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly CefiroSession _session;
    private readonly UiSettingsStore _settings;
    private readonly ObservableCollection<double?> _cpuHistory = [];
    private readonly ObservableCollection<double?> _gpuHistory = [];
    private readonly ObservableCollection<double?> _cpuLoadHistory = [];
    private readonly ObservableCollection<double?> _gpuLoadHistory = [];
    private int _framesSinceReload;

    public DashboardViewModel(CefiroSession session, UiSettingsStore settings)
    {
        _session = session;
        _settings = settings;
        Schematic.ApplyLayout(settings.Current.Layout);
        settings.Changed += s => Schematic.ApplyLayout(s.Layout);

        var cyan = new SKColor(0x00, 0xE5, 0xFF);
        var pink = new SKColor(0xFF, 0x2E, 0x88);
        HistorySeries =
        [
            Line("CPU", _cpuHistory, cyan, fill: true),
            Line("GPU", _gpuHistory, pink, fill: true),
            Line("Uso CPU", _cpuLoadHistory, new SKColor(0x00, 0xE5, 0xFF, 0x70), fill: false, dashed: true),
            Line("Uso GPU", _gpuLoadHistory, new SKColor(0xFF, 0x2E, 0x88, 0x70), fill: false, dashed: true),
        ];

        XAxes = [new Axis { IsVisible = false }];
        YAxes =
        [
            new Axis
            {
                MinLimit = 0,
                MaxLimit = 100,
                MinStep = 20,
                TextSize = 11,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(new SKColor(0x5D, 0x68, 0x80)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(0xFF, 0xFF, 0xFF, 0x12)) { StrokeThickness = 1 },
            },
        ];

        session.SensorsUpdated += OnSensors;
        session.FansUpdated += OnFans;
        session.LightingFrameUpdated += () => Schematic.UpdateLeds(_session, _settings.Current.Layout);
        session.Loaded += () => _ = LoadHistoryAsync();
    }

    public SchematicViewModel Schematic { get; } = new();
    public ObservableCollection<FanSummaryViewModel> FanSummaries { get; } = [];
    public ISeries[] HistorySeries { get; }
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

    [ObservableProperty] public partial HistoryWindow Window { get; set; } = HistoryWindow.Seconds60;

    [ObservableProperty] public partial double CpuTemp { get; set; }
    [ObservableProperty] public partial string CpuTempText { get; set; } = "—";
    [ObservableProperty] public partial double CpuLoad { get; set; } = -1;
    [ObservableProperty] public partial string CpuLoadText { get; set; } = "—";
    [ObservableProperty] public partial string CpuClockText { get; set; } = "—";
    [ObservableProperty] public partial string CpuPowerText { get; set; } = "—";
    [ObservableProperty] public partial Color CpuColor { get; set; } = Color.FromRgb(0x00, 0xE5, 0xFF);
    [ObservableProperty] public partial string CpuName { get; set; } = "CPU";

    [ObservableProperty] public partial double GpuTemp { get; set; }
    [ObservableProperty] public partial string GpuTempText { get; set; } = "—";
    [ObservableProperty] public partial double GpuLoad { get; set; } = -1;
    [ObservableProperty] public partial string GpuLoadText { get; set; } = "—";
    [ObservableProperty] public partial string GpuClockText { get; set; } = "—";
    [ObservableProperty] public partial string GpuPowerText { get; set; } = "—";
    [ObservableProperty] public partial string GpuHotSpotText { get; set; } = "—";
    [ObservableProperty] public partial string GpuVramText { get; set; } = "—";
    [ObservableProperty] public partial Color GpuColor { get; set; } = Color.FromRgb(0x00, 0xE5, 0xFF);
    [ObservableProperty] public partial string GpuName { get; set; } = "GPU";

    [ObservableProperty] public partial double RamLoad { get; set; }
    [ObservableProperty] public partial string RamLoadText { get; set; } = "—";
    [ObservableProperty] public partial string RamUsedText { get; set; } = "—";
    [ObservableProperty] public partial string RamTempText { get; set; } = "—";

    [ObservableProperty] public partial double BoardTemp { get; set; }
    [ObservableProperty] public partial string BoardTempText { get; set; } = "—";
    [ObservableProperty] public partial string VrmText { get; set; } = "—";
    [ObservableProperty] public partial string ChipsetText { get; set; } = "—";
    [ObservableProperty] public partial string SystemText { get; set; } = "—";
    [ObservableProperty] public partial Color BoardColor { get; set; } = Color.FromRgb(0x00, 0xE5, 0xFF);
    [ObservableProperty] public partial string BoardName { get; set; } = "Placa base";

    [ObservableProperty] public partial string TemperatureUnit { get; set; } = "°C";

    private static LineSeries<double?> Line(string name, ObservableCollection<double?> values, SKColor color, bool fill, bool dashed = false)
    {
        var stroke = new SolidColorPaint(color) { StrokeThickness = dashed ? 1.5f : 2.4f };
        if (dashed)
            stroke.PathEffect = new LiveChartsCore.SkiaSharpView.Painting.Effects.DashEffect([4, 4]);

        return new LineSeries<double?>
        {
            Name = name,
            Values = values,
            Stroke = stroke,
            Fill = fill
                ? new LinearGradientPaint([color.WithAlpha(0x55), color.WithAlpha(0x00)], new SKPoint(0.5f, 0), new SKPoint(0.5f, 1))
                : null,
            GeometrySize = 0,
            GeometryFill = null,
            GeometryStroke = null,
            LineSmoothness = 0.6,
        };
    }

    [RelayCommand]
    private async Task SetWindow(string window)
    {
        Window = window switch
        {
            "10m" => HistoryWindow.Minutes10,
            "1h" => HistoryWindow.Hour1,
            _ => HistoryWindow.Seconds60,
        };
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        var keys = _session.Catalog?.Keys;
        if (keys is null)
            return;

        var ids = new[] { keys.CpuTemperature, keys.GpuTemperature, keys.CpuLoad, keys.GpuLoad }.Where(i => i is not null).Select(i => i!).ToList();
        if (ids.Count == 0)
            return;

        try
        {
            var range = Window switch
            {
                HistoryWindow.Minutes10 => HistoryRange.Last10Minutes,
                HistoryWindow.Hour1 => HistoryRange.LastHour,
                _ => HistoryRange.Last60Seconds,
            };
            var history = await _session.Api.GetHistoryAsync(new HistoryRequest(ids, range));
            Fill(_cpuHistory, history, keys.CpuTemperature, temperature: true);
            Fill(_gpuHistory, history, keys.GpuTemperature, temperature: true);
            Fill(_cpuLoadHistory, history, keys.CpuLoad, temperature: false);
            Fill(_gpuLoadHistory, history, keys.GpuLoad, temperature: false);
            _framesSinceReload = 0;
        }
        catch
        {
            // Sin historial todavía: se rellenará en directo.
        }
    }

    private static void Fill(ObservableCollection<double?> target, HistoryResponse history, string? id, bool temperature)
    {
        target.Clear();
        var series = history.Series.FirstOrDefault(s => s.SensorId == id);
        if (series is null)
            return;
        foreach (var v in series.Values)
            target.Add(temperature ? Format.ToUnit(v) : v);
    }

    private void OnSensors()
    {
        var s = _session;
        var k = s.Catalog?.Keys;
        if (k is null)
            return;

        TemperatureUnit = Format.TemperatureUnit;
        CpuName = s.Catalog!.Backend.Cpu ?? "CPU";
        GpuName = s.Catalog.Backend.Gpu ?? "GPU";
        BoardName = s.Catalog.Backend.Motherboard ?? "Placa base";

        var cpuTemp = s.Value(k.CpuTemperature);
        CpuTemp = Format.ToUnit(cpuTemp) ?? 0;
        CpuTempText = Format.TemperatureValue(cpuTemp);
        CpuLoad = s.Value(k.CpuLoad) ?? -1;
        CpuLoadText = Format.Percent(s.Value(k.CpuLoad));
        CpuClockText = Format.Mhz(s.Value(k.CpuClock));
        CpuPowerText = Format.Watts(s.Value(k.CpuPower));
        CpuColor = Format.TemperatureColor(cpuTemp, 70, 88);

        var gpuTemp = s.Value(k.GpuTemperature);
        GpuTemp = Format.ToUnit(gpuTemp) ?? 0;
        GpuTempText = Format.TemperatureValue(gpuTemp);
        GpuLoad = s.Value(k.GpuLoad) ?? -1;
        GpuLoadText = Format.Percent(s.Value(k.GpuLoad));
        GpuClockText = Format.Mhz(s.Value(k.GpuClock));
        GpuPowerText = Format.Watts(s.Value(k.GpuPower));
        GpuHotSpotText = Format.Temperature(s.Value(k.GpuHotSpot));
        GpuVramText = s.Value(k.GpuMemoryUsed) is { } used
            ? $"{Format.Gigabytes(used)} / {Format.Gigabytes(s.Value(k.GpuMemoryTotal))}"
            : "—";
        GpuColor = Format.TemperatureColor(gpuTemp, 65, 83);

        var ramLoad = s.Value(k.RamLoad);
        RamLoad = ramLoad ?? 0;
        RamLoadText = ramLoad is { } rl ? rl.ToString("0", Format.Es) : "—";
        var used2 = s.Value(k.RamUsed);
        var free = s.Value(k.RamAvailable);
        RamUsedText = used2 is { } u && free is { } f ? $"{Format.Gigabytes(u)} de {Format.Gigabytes(u + f)}" : Format.Gigabytes(used2);
        RamTempText = Format.Temperature(s.Value(k.RamTemperature));

        var vrm = s.Value(k.VrmTemperature);
        BoardTemp = Format.ToUnit(vrm) ?? 0;
        BoardTempText = Format.TemperatureValue(vrm);
        VrmText = Format.Temperature(vrm);
        ChipsetText = Format.Temperature(s.Value(k.ChipsetTemperature));
        SystemText = Format.Temperature(s.Value(k.BoardTemperature));
        BoardColor = Format.TemperatureColor(vrm, 60, 85);

        // Historial: se añade el punto en directo (en la vista de 1 h se recarga cada 5 s).
        if (Window == HistoryWindow.Hour1)
        {
            if (++_framesSinceReload >= 5)
                _ = LoadHistoryAsync();
        }
        else
        {
            var cap = Window == HistoryWindow.Seconds60 ? 60 : 600;
            Append(_cpuHistory, Format.ToUnit(cpuTemp), cap);
            Append(_gpuHistory, Format.ToUnit(gpuTemp), cap);
            Append(_cpuLoadHistory, s.Value(k.CpuLoad), cap);
            Append(_gpuLoadHistory, s.Value(k.GpuLoad), cap);
        }
    }

    private static void Append(ObservableCollection<double?> target, double? value, int cap)
    {
        target.Add(value);
        while (target.Count > cap)
            target.RemoveAt(0);
    }

    private void OnFans()
    {
        var s = _session;
        if (s.Catalog is not { } catalog || s.FanFrame is not { } frame)
            return;

        Schematic.UpdateFans(s);

        // Tarjetas de resumen: solo lo que gira o lo importante (bomba, radiador, GPU).
        foreach (var ch in catalog.FanChannels)
        {
            var state = frame.Channels.FirstOrDefault(c => c.ChannelId == ch.Id);
            var settings = s.FanSettings?.Channels.FirstOrDefault(c => c.ChannelId == ch.Id);
            var role = settings?.Role ?? ch.DetectedRole;
            var relevant = role is FanRole.Pump or FanRole.Radiator || (state?.Rpm ?? 0) > 0 || (role == FanRole.Gpu && ch.Header == "GPU 1");
            var summary = FanSummaries.FirstOrDefault(f => f.ChannelId == ch.Id);

            if (!relevant)
            {
                if (summary is not null)
                    FanSummaries.Remove(summary);
                continue;
            }

            if (summary is null)
            {
                summary = new FanSummaryViewModel { ChannelId = ch.Id };
                FanSummaries.Add(summary);
            }

            summary.Title = settings?.Name ?? ch.DefaultName;
            var roleText = role switch
            {
                FanRole.Pump => "Bomba de la líquida",
                FanRole.Radiator => "Radiador de la líquida",
                FanRole.Gpu => "Tarjeta gráfica",
                _ => "Ventiladores de caja",
            };
            summary.Subtitle = role == FanRole.Gpu || summary.Title.Contains(ch.Header, StringComparison.OrdinalIgnoreCase)
                ? roleText
                : $"{roleText} · {ch.Header}";
            summary.Glyph = role switch
            {
                FanRole.Pump => "Drop24",
                FanRole.Gpu => "DeveloperBoard24",
                _ => "WeatherSqualls24",
            };
            summary.RpmText = state?.Rpm is { } rpm ? rpm.ToString("#,0", Format.Es) : "—";
            summary.Output = state?.OutputPercent ?? 0;
            summary.OutputText = Format.Percent(state?.OutputPercent);
            summary.ModeText = state?.EffectiveMode switch
            {
                FanMode.Curve => "Curva",
                FanMode.Fixed => "Fijo",
                _ => "BIOS",
            };
            summary.Push(state?.Rpm);
        }
    }
}
