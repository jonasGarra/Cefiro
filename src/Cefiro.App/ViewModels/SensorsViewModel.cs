using System.Collections.ObjectModel;
using Cefiro.App.Infrastructure;
using Cefiro.Ipc.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cefiro.App.ViewModels;

public sealed partial class SensorRowViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Unit { get; init; }
    public required SensorKind Kind { get; init; }
    public float? Min { get; private set; }
    public float? Max { get; private set; }

    [ObservableProperty] public partial string ValueText { get; set; } = "—";
    [ObservableProperty] public partial string MinText { get; set; } = "—";
    [ObservableProperty] public partial string MaxText { get; set; } = "—";

    public string KindGlyph => Kind switch
    {
        SensorKind.Temperature => "Temperature24",
        SensorKind.Load => "DataHistogram24",
        SensorKind.Clock => "TopSpeed24",
        SensorKind.Power => "Flash24",
        SensorKind.Voltage => "Flash24",
        SensorKind.Fan => "WeatherSqualls24",
        SensorKind.Control => "WeatherSqualls24",
        _ => "Pulse24",
    };

    public void Update(float? value)
    {
        if (value is { } v)
        {
            Min = Min is { } mn ? Math.Min(mn, v) : v;
            Max = Max is { } mx ? Math.Max(mx, v) : v;
        }

        ValueText = Format.Value(value, Unit);
        MinText = Format.Value(Min, Unit);
        MaxText = Format.Value(Max, Unit);
    }
}

public sealed class SensorGroupViewModel
{
    public required string Device { get; init; }
    public required string Glyph { get; init; }
    public ObservableCollection<SensorRowViewModel> Rows { get; } = [];
}

public sealed partial class SensorsViewModel : ObservableObject
{
    private readonly CefiroSession _session;
    private readonly List<SensorRowViewModel> _all = [];

    public SensorsViewModel(CefiroSession session)
    {
        _session = session;
        session.Loaded += Build;
        session.SensorsUpdated += Update;
        if (session.IsLoaded)
            Build();
    }

    /// <summary>Dos columnas tipo "mosaico": cada tarjeta va a la columna más corta, sin huecos.</summary>
    public ObservableCollection<SensorGroupViewModel> LeftGroups { get; } = [];

    public ObservableCollection<SensorGroupViewModel> RightGroups { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial int Count { get; set; }

    partial void OnFilterChanged(string value) => Build();

    private void Build()
    {
        LeftGroups.Clear();
        RightGroups.Clear();
        _all.Clear();
        if (_session.Catalog is not { } catalog)
            return;

        var leftHeight = 0;
        var rightHeight = 0;
        var filter = Filter.Trim();
        // Orden lógico (CPU, gráfica, placa, memoria…) que además reparte bien las dos columnas.
        var groups = catalog.Sensors
            .GroupBy(s => (s.Device, s.Category))
            .OrderBy(g => g.Key.Category switch
            {
                DeviceCategory.Cpu => 0,
                DeviceCategory.Gpu => 1,
                DeviceCategory.Motherboard => 2,
                DeviceCategory.Memory => 3,
                _ => 4,
            });

        foreach (var group in groups)
        {
            var vm = new SensorGroupViewModel
            {
                Device = group.Key.Device,
                Glyph = group.Key.Category switch
                {
                    DeviceCategory.Cpu => "Temperature24",
                    DeviceCategory.Gpu => "DeveloperBoard24",
                    DeviceCategory.Memory => "Board24",
                    DeviceCategory.Motherboard => "Flash24",
                    _ => "Pulse24",
                },
            };

            foreach (var s in group)
            {
                if (filter.Length > 0 && !s.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                      && !s.Device.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                    continue;

                var row = new SensorRowViewModel { Id = s.Id, Name = s.Name, Unit = s.Unit, Kind = s.Kind };
                vm.Rows.Add(row);
                _all.Add(row);
            }

            if (vm.Rows.Count == 0)
                continue;

            // Altura aproximada: cabecera (≈3 filas) más una por sensor.
            var height = vm.Rows.Count + 3;
            if (leftHeight <= rightHeight)
            {
                LeftGroups.Add(vm);
                leftHeight += height;
            }
            else
            {
                RightGroups.Add(vm);
                rightHeight += height;
            }
        }

        Count = _all.Count;
        Update();
    }

    private void Update()
    {
        foreach (var row in _all)
            row.Update(_session.Value(row.Id));
    }
}
