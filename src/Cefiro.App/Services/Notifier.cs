namespace Cefiro.App.Services;

/// <summary>Notificaciones de Windows desde cualquier parte de la interfaz (las muestra la bandeja).</summary>
public static class Notifier
{
    /// <summary>Lo asigna App al crear el icono de la bandeja.</summary>
    public static Action<string, string, bool>? Handler { get; set; }

    public static void Show(string title, string message, bool warning = false) => Handler?.Invoke(title, message, warning);
}
