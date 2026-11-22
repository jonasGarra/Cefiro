namespace Cefiro.Ipc.Contracts;

public static class IpcConstants
{
    /// <summary>Nombre del named pipe: \\.\pipe\cefiro</summary>
    public const string PipeName = "cefiro";

    /// <summary>Versión del protocolo entre interfaz y servicio.</summary>
    public const int ProtocolVersion = 1;
}

/// <summary>Métodos que la interfaz puede invocar en el servicio.</summary>
public static class Methods
{
    public const string Hello = "hello";
    public const string GetCatalog = "catalog.get";
    public const string GetHistory = "history.get";
    public const string GetStatus = "status.get";

    public const string GetFanSettings = "fans.settings.get";
    public const string SetFanSettings = "fans.settings.set";
    public const string ApplyFanPreset = "fans.preset.apply";
    public const string StartCalibration = "fans.calibration.start";
    public const string CancelCalibration = "fans.calibration.cancel";
    public const string GetCalibration = "fans.calibration.get";

    public const string GetLighting = "lighting.get";
    public const string SetLighting = "lighting.set";
    public const string ResizeZone = "lighting.zone.resize";
    public const string IdentifyLeds = "lighting.identify";
    public const string AudioSpectrum = "lighting.audio";

    public const string GetProfiles = "profiles.get";
    public const string SaveProfile = "profiles.save";
    public const string DeleteProfile = "profiles.delete";
    public const string ActivateProfile = "profiles.activate";

    public const string GetSettings = "settings.get";
    public const string SetSettings = "settings.set";
}

/// <summary>Eventos que el servicio envía a la interfaz.</summary>
public static class Events
{
    public const string Sensors = "sensors";
    public const string Fans = "fans";
    public const string Status = "status";
    public const string Alert = "alert";
    public const string Lighting = "lighting";
    public const string Profiles = "profiles";
    public const string Calibration = "calibration";
}
