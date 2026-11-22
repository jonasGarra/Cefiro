using System.Windows;

namespace Cefiro.App.Infrastructure;

/// <summary>Propiedades adjuntas para los estilos (p. ej. marcar una tarjeta-botón como activa).</summary>
public static class Ui
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject d, bool value) => d.SetValue(IsActiveProperty, value);
}
