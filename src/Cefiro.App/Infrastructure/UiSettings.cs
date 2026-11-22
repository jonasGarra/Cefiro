using System.IO;
using System.Text.Json;
using Cefiro.Ipc;

namespace Cefiro.App.Infrastructure;

/// <summary>Cómo está montado tu PC, para dibujar el esquema de la caja.</summary>
public sealed record CaseLayout
{
    /// <summary>Dónde va el radiador de la líquida: "Top" (techo) o "Front" (frontal).</summary>
    public string RadiatorPosition { get; init; } = "Top";

    public int FrontFans { get; init; } = 3;
    public int RearFans { get; init; } = 1;
    public int BottomFans { get; init; }

    /// <summary>Zona de iluminación de los ventiladores de caja (en tu PC: JRAINBOW1).</summary>
    public string CaseLightZone { get; init; } = "JRAINBOW1";

    /// <summary>Zona de iluminación de la líquida (en tu PC: JRAINBOW2).</summary>
    public string AioLightZone { get; init; } = "JRAINBOW2";
}

/// <summary>Preferencias de la interfaz (en %APPDATA%\Cefiro\interfaz.json). Las del hardware las guarda el servicio.</summary>
public sealed record UiSettings
{
    public string AccentHex { get; init; } = "#00E5FF";
    public string Accent2Hex { get; init; } = "#7C4DFF";

    /// <summary>El color de acento sigue al color actual de las luces del PC.</summary>
    public bool AccentFollowsLights { get; init; }

    public bool UseFahrenheit { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool StartMinimized { get; init; }
    public bool TrayHintShown { get; init; }
    public bool ShowEmptyFanHeaders { get; init; }
    public bool ToastsEnabled { get; init; } = true;
    public CaseLayout Layout { get; init; } = new();
    public string? LastPage { get; init; }
    public double WindowWidth { get; init; } = 1360;
    public double WindowHeight { get; init; } = 860;
}

public sealed class UiSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public UiSettingsStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cefiro");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "interfaz.json");
        Current = Load();
    }

    public UiSettings Current { get; private set; }

    public event Action<UiSettings>? Changed;

    private UiSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(_path), CefiroJson.Options) ?? new UiSettings();
        }
        catch
        {
            // Fichero dañado: valores por defecto.
        }

        return new UiSettings();
    }

    public void Update(Func<UiSettings, UiSettings> change)
    {
        lock (_gate)
        {
            Current = change(Current);
            try
            {
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Current, CefiroJson.Indented));
                File.Move(temp, _path, overwrite: true);
            }
            catch
            {
                // Sin permisos de escritura: se mantiene en memoria.
            }
        }

        Changed?.Invoke(Current);
    }
}
