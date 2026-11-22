using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cefiro.App.Infrastructure;

/// <summary>
/// Comportamientos para cajas de texto "en línea" (nombres editables): Intro confirma y Esc deshace.
/// </summary>
public static class TextBoxBehaviors
{
    public static readonly DependencyProperty CommitOnEnterProperty = DependencyProperty.RegisterAttached(
        "CommitOnEnter", typeof(bool), typeof(TextBoxBehaviors), new PropertyMetadata(false, OnCommitOnEnterChanged));

    public static bool GetCommitOnEnter(DependencyObject d) => (bool)d.GetValue(CommitOnEnterProperty);

    public static void SetCommitOnEnter(DependencyObject d, bool value) => d.SetValue(CommitOnEnterProperty, value);

    private static void OnCommitOnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        box.KeyDown -= OnKeyDown;
        if (e.NewValue is true)
            box.KeyDown += OnKeyDown;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var binding = box.GetBindingExpression(TextBox.TextProperty);

        if (e.Key == Key.Enter)
        {
            binding?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            binding?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }
}
