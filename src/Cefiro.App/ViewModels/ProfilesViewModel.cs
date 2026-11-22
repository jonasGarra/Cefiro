using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cefiro.App.Infrastructure;
using Cefiro.App.Services;
using Cefiro.Core.Fans;
using Cefiro.Core.Lighting;
using Cefiro.Ipc;
using Cefiro.Ipc.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Cefiro.App.ViewModels;

/// <summary>Un programa asociado a un perfil (chip con botón de quitar).</summary>
public sealed record ProgramChip(ProfileItemViewModel Owner, string Name);

/// <summary>Fichero de perfil exportado (JSON que se puede compartir).</summary>
public sealed record ProfileFile(string Format, int Version, ProfileDto Profile)
{
    public const string FormatName = "cefiro-perfil";
}

public sealed partial class ProfileItemViewModel : ObservableObject
{
    private readonly ProfilesViewModel _owner;
    internal bool Loading;

    public ProfileItemViewModel(ProfilesViewModel owner, ProfileDto dto)
    {
        _owner = owner;
        Dto = dto;
    }

    public ProfileDto Dto { get; private set; }
    public string Id => Dto.Id;
    public ObservableCollection<ProgramChip> Programs { get; } = [];

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string FansSummary { get; set; } = "";
    [ObservableProperty] public partial string LightingSummary { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<SolidColorBrush> Swatches { get; set; } = [];
    [ObservableProperty] public partial string? Hotkey { get; set; }
    [ObservableProperty] public partial bool IsRecordingHotkey { get; set; }
    [ObservableProperty] public partial string NewProgram { get; set; } = "";

    public string HotkeyText => IsRecordingHotkey ? "Pulsa la combinación…" : Hotkey ?? "Sin atajo";
    public bool HasPrograms => Programs.Count > 0;

    partial void OnHotkeyChanged(string? value) => OnPropertyChanged(nameof(HotkeyText));
    partial void OnIsRecordingHotkeyChanged(bool value) => OnPropertyChanged(nameof(HotkeyText));

    partial void OnNameChanged(string value)
    {
        if (!Loading)
            _owner.Rename(this, value);
    }

    internal void Update(ProfileDto dto, ProfilesStateDto state)
    {
        Loading = true;
        try
        {
            Dto = dto;
            Name = dto.Name;
            Hotkey = dto.Hotkey;
            IsActive = state.ActiveProfileId == dto.Id;
            StatusText = !IsActive ? "" : state.ActivatedBy is { } by ? $"Activo · por {by}" : "Activo";
            FansSummary = SummarizeFans(dto.Fans);
            LightingSummary = SummarizeLighting(dto.Lighting);
            Swatches = SwatchesOf(dto.Lighting);

            if (!Programs.Select(p => p.Name).SequenceEqual(dto.Programs))
            {
                Programs.Clear();
                foreach (var p in dto.Programs)
                    Programs.Add(new ProgramChip(this, p));
                OnPropertyChanged(nameof(HasPrograms));
            }
        }
        finally
        {
            Loading = false;
        }
    }

    private static string SummarizeFans(FanSettingsDto? fans)
    {
        if (fans is null)
            return "No cambia los ventiladores";

        var controlled = fans.Channels.Where(c => c.Mode != FanMode.Bios).ToList();
        if (controlled.Count == 0)
            return "Ventiladores: todo en BIOS";

        foreach (var preset in Enum.GetValues<FanPreset>())
        {
            if (controlled.All(c => SameControl(FanDefaults.ApplyPreset(c, preset), c)))
                return $"Ventiladores: modo {Names.Preset(preset)}";
        }

        var curve = controlled.Count(c => c.Mode == FanMode.Curve);
        var fixedCount = controlled.Count(c => c.Mode == FanMode.Fixed);
        var parts = new List<string>();
        if (curve > 0) parts.Add($"{curve} con curva");
        if (fixedCount > 0) parts.Add($"{fixedCount} a velocidad fija");
        return "Ventiladores: " + string.Join(" · ", parts);
    }

    private static bool SameControl(FanChannelSettingsDto expected, FanChannelSettingsDto actual) =>
        expected.Mode == actual.Mode && expected.Mode switch
        {
            FanMode.Fixed => Math.Abs(expected.FixedPercent - actual.FixedPercent) < 0.5,
            FanMode.Curve => expected.Curve.Count == actual.Curve.Count
                             && expected.Curve.Zip(actual.Curve).All(p => Math.Abs(p.First.Temperature - p.Second.Temperature) < 0.5
                                                                         && Math.Abs(p.First.Percent - p.Second.Percent) < 0.5),
            _ => true,
        };

    private static string SummarizeLighting(LightingSettingsDto? lighting)
    {
        if (lighting is null)
            return "No cambia las luces";
        if (!lighting.Enabled)
            return "Luces apagadas";
        if (!lighting.SyncAll)
            return $"Luces: efectos por dispositivo ({lighting.Layers.Count})";

        var effect = lighting.SyncEffect;
        var name = Names.Effect(effect.Kind);
        if (effect.Kind == EffectKind.DeviceMode && effect.NativeMode is { } native)
            name += $" ({native})";
        return $"Luces: {name} · brillo {lighting.Brightness:0} %";
    }

    private static IReadOnlyList<SolidColorBrush> SwatchesOf(LightingSettingsDto? lighting)
    {
        if (lighting is not { Enabled: true, SyncAll: true } l || l.SyncEffect.Kind == EffectKind.Off)
            return [];

        IEnumerable<string> hexes = l.SyncEffect.Kind == EffectKind.Rainbow
            ? ["#FF0040", "#FFB300", "#00E676", "#00E5FF", "#7C4DFF"]
            : l.SyncEffect.Colors;

        return hexes.Take(5).Select(h =>
        {
            var c = Rgb.FromHex(h, Rgb.White);
            var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            b.Freeze();
            return b;
        }).ToList();
    }
}

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly CefiroSession _session;
    private ProfileItemViewModel? _recording;
    private bool _loading;

    public ProfilesViewModel(CefiroSession session)
    {
        _session = session;
        session.ProfilesChanged += Load;
        if (session.IsLoaded)
            Load();
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    [ObservableProperty] public partial bool AutoSwitch { get; set; } = true;
    [ObservableProperty] public partial bool HasProfiles { get; set; }
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial string? InfoText { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> RunningApps { get; set; } = [];

    /// <summary>Hay un perfil esperando a que se pulse su atajo.</summary>
    public bool IsRecordingHotkey => _recording is not null;

    private void Load()
    {
        if (_session.Profiles is not { } state)
            return;

        _loading = true;
        try
        {
            AutoSwitch = _session.ServiceSettings?.AutoSwitchProfiles ?? state.AutoSwitchEnabled;

            var ids = state.Profiles.Select(p => p.Id).ToList();
            foreach (var stale in Profiles.Where(p => !ids.Contains(p.Id)).ToList())
                Profiles.Remove(stale);

            for (var i = 0; i < state.Profiles.Count; i++)
            {
                var dto = state.Profiles[i];
                var item = Profiles.FirstOrDefault(p => p.Id == dto.Id);
                if (item is null)
                {
                    item = new ProfileItemViewModel(this, dto);
                    Profiles.Insert(Math.Min(i, Profiles.Count), item);
                }

                item.Update(dto, state);
            }

            HasProfiles = Profiles.Count > 0;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RunAsync(Func<Task> action, string? done = null)
    {
        try
        {
            await action();
            ErrorText = null;
            InfoText = done;
        }
        catch (Exception ex)
        {
            UiLog.Error("Error en perfiles", ex);
            ErrorText = ex.Message;
        }
    }

    partial void OnAutoSwitchChanged(bool value)
    {
        if (!_loading)
            _ = RunAsync(() => _session.SaveServiceSettingsAsync(new ServiceSettingsDto { AutoSwitchProfiles = value }));
    }

    [RelayCommand]
    private Task NewProfile()
    {
        var n = 1;
        while (Profiles.Any(p => p.Name == $"Perfil {n}"))
            n++;
        return RunAsync(() => _session.SaveProfileAsync(new ProfileDto { Id = "", Name = $"Perfil {n}" }, captureCurrent: true),
            $"Perfil {n} creado con tus ventiladores y luces de ahora.");
    }

    [RelayCommand]
    private Task Activate(ProfileItemViewModel item) =>
        RunAsync(() => _session.ActivateProfileAsync(item.Id), $"Perfil «{item.Name}» activado.");

    [RelayCommand]
    private Task UpdateFromCurrent(ProfileItemViewModel item) =>
        RunAsync(() => _session.SaveProfileAsync(item.Dto, captureCurrent: true), $"«{item.Name}» guarda ahora tu configuración actual.");

    [RelayCommand]
    private async Task Delete(ProfileItemViewModel item)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Borrar perfil",
            Content = $"¿Seguro que quieres borrar «{item.Name}»? Los ventiladores y las luces se quedan como están ahora.",
            PrimaryButtonText = "Borrar",
            CloseButtonText = "Cancelar",
        };
        if (Application.Current.MainWindow is { IsVisible: true } owner)
            box.Owner = owner;

        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        await RunAsync(() => _session.DeleteProfileAsync(item.Id), $"Perfil «{item.Name}» borrado.");
    }

    internal void Rename(ProfileItemViewModel item, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == item.Dto.Name)
            return;
        _ = RunAsync(() => _session.SaveProfileAsync(item.Dto with { Name = name.Trim() }, captureCurrent: false));
    }

    // ------------------------------------------------------------------ programas

    [RelayCommand]
    private Task AddProgram(ProfileItemViewModel item)
    {
        var name = item.NewProgram.Trim();
        if (name.Length == 0)
            return Task.CompletedTask;

        item.NewProgram = "";
        return SaveProgramsAsync(item, [.. item.Dto.Programs, name]);
    }

    [RelayCommand]
    private Task BrowseProgram(ProfileItemViewModel item)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Programa que activa «{item.Name}»",
            Filter = "Programas (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        return dialog.ShowDialog() == true
            ? SaveProgramsAsync(item, [.. item.Dto.Programs, Path.GetFileName(dialog.FileName)])
            : Task.CompletedTask;
    }

    [RelayCommand]
    private Task RemoveProgram(ProgramChip chip) =>
        SaveProgramsAsync(chip.Owner, chip.Owner.Dto.Programs.Where(p => p != chip.Name).ToList());

    private Task SaveProgramsAsync(ProfileItemViewModel item, IReadOnlyList<string> programs) =>
        RunAsync(() => _session.SaveProfileAsync(item.Dto with { Programs = programs }, captureCurrent: false));

    /// <summary>Programas abiertos con ventana (para elegir sin buscar el .exe).</summary>
    [RelayCommand]
    private void RefreshRunningApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.SessionId != 0 && p.Id != Environment.ProcessId)
                    names.Add(p.ProcessName + ".exe");
            }
            catch
            {
                // Proceso cerrado o protegido.
            }
            finally
            {
                p.Dispose();
            }
        }

        RunningApps = [.. names];
    }

    // ------------------------------------------------------------------ atajos

    [RelayCommand]
    private void RecordHotkey(ProfileItemViewModel item)
    {
        if (_recording is not null)
            _recording.IsRecordingHotkey = false;

        _recording = item;
        item.IsRecordingHotkey = true;
        InfoText = "Pulsa la combinación de teclas (por ejemplo Ctrl+Alt+1). Esc cancela y Supr quita el atajo.";
        OnPropertyChanged(nameof(IsRecordingHotkey));
    }

    /// <summary>Tecla pulsada mientras se graba un atajo. Devuelve true si la ha usado.</summary>
    public bool HandleHotkeyKey(Key key, ModifierKeys modifiers)
    {
        if (_recording is not { } item)
            return false;

        if (key == Key.Escape)
        {
            StopRecording();
            InfoText = null;
            return true;
        }

        if (key is Key.Delete or Key.Back)
        {
            StopRecording();
            _ = RunAsync(() => _session.SaveProfileAsync(item.Dto with { Hotkey = null }, captureCurrent: false), $"«{item.Name}» ya no tiene atajo.");
            return true;
        }

        var text = HotkeyService.Format(modifiers, key);
        if (text is null)
            return true; // solo modificadores o sin modificador: se sigue esperando

        StopRecording();
        _ = RunAsync(() => _session.SaveProfileAsync(item.Dto with { Hotkey = text }, captureCurrent: false), $"Atajo {text} asignado a «{item.Name}».");
        return true;
    }

    private void StopRecording()
    {
        if (_recording is not null)
            _recording.IsRecordingHotkey = false;
        _recording = null;
        OnPropertyChanged(nameof(IsRecordingHotkey));
    }

    // ------------------------------------------------------------------ importar / exportar

    [RelayCommand]
    private async Task Export(ProfileItemViewModel item)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar perfil",
            FileName = $"{string.Concat(item.Name.Split(Path.GetInvalidFileNameChars()))}.cefiro.json",
            Filter = "Perfil de Céfiro (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true)
            return;

        await RunAsync(async () =>
        {
            var file = new ProfileFile(ProfileFile.FormatName, 1, item.Dto);
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(file, CefiroJson.Indented));
        }, $"Perfil exportado a {Path.GetFileName(dialog.FileName)}.");
    }

    [RelayCommand]
    private async Task Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar perfil",
            Filter = "Perfil de Céfiro (*.json)|*.json|Todos los archivos (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        await RunAsync(async () =>
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var profile = ParseProfile(json) ?? throw new InvalidDataException("El archivo no es un perfil de Céfiro.");

            // Se importa como perfil nuevo; el servicio comprueba todos los valores antes de usarlos.
            var name = Profiles.Any(p => p.Name == profile.Name) ? $"{profile.Name} (importado)" : profile.Name;
            await _session.SaveProfileAsync(profile with { Id = "", Name = name }, captureCurrent: false);
        }, "Perfil importado. Revisa que los ventiladores coincidan con los de este PC antes de activarlo.");
    }

    internal static ProfileDto? ParseProfile(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<ProfileFile>(json, CefiroJson.Options);
            if (file is { Format: ProfileFile.FormatName, Profile: not null })
                return file.Profile;
        }
        catch (JsonException)
        {
            // Puede ser un perfil "suelto".
        }

        try
        {
            var profile = JsonSerializer.Deserialize<ProfileDto>(json, CefiroJson.Options);
            return profile is { Name.Length: > 0 } && (profile.Fans is not null || profile.Lighting is not null) ? profile : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
