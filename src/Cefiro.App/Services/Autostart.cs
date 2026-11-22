using Cefiro.App.Infrastructure;
using Microsoft.Win32;

namespace Cefiro.App.Services;

/// <summary>
/// Inicio con Windows de la interfaz (HKCU\...\Run, sin permisos de administrador ni UAC). El control
/// del hardware no depende de esto: lo hace el servicio de Céfiro, que arranca solo con Windows.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Cefiro";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string { Length: > 0 };
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled, bool minimized)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\"{(minimized ? " --minimized" : "")}");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            UiLog.Error("No se pudo cambiar el inicio con Windows", ex);
        }
    }
}
