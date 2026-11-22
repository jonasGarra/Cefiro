using System.Windows.Controls;
using System.Windows.Input;
using Cefiro.App.ViewModels;

namespace Cefiro.App.Views;

public partial class ProfilesPage : UserControl
{
    public ProfilesPage()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Mientras se graba un atajo, la siguiente combinación de teclas es el atajo.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ProfilesViewModel { IsRecordingHotkey: true } vm)
            return;

        // Con Alt pulsado WPF entrega Key.System y la tecla real en SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (vm.HandleHotkeyKey(key, Keyboard.Modifiers))
            e.Handled = true;
    }

    private void OnProgramsDropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is ProfilesViewModel vm)
            vm.RefreshRunningAppsCommand.Execute(null);
    }
}
