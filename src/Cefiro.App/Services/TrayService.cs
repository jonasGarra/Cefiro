using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Cefiro.Ipc.Contracts;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Cefiro.App.Services;

/// <summary>Icono de la bandeja del sistema con menú rápido y notificaciones de Windows.</summary>
public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _lightsItem;
    private readonly MenuItem _profilesMenu;

    public TrayService(
        Action showWindow,
        Action exit,
        Func<FanPreset, Task> applyPreset,
        Func<bool, Task> setLightsEnabled,
        Func<bool> lightsEnabled)
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Abrir Céfiro", Icon = new SymbolIcon(SymbolRegular.Window24), FontWeight = System.Windows.FontWeights.SemiBold };
        open.Click += (_, _) => showWindow();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());

        var fans = new MenuItem { Header = "Ventiladores", Icon = new SymbolIcon(SymbolRegular.WeatherSqualls24) };
        foreach (var (preset, name) in new[]
                 {
                     (FanPreset.Silent, "Silencioso"),
                     (FanPreset.Balanced, "Equilibrado"),
                     (FanPreset.Performance, "Rendimiento"),
                     (FanPreset.Maximum, "Máximo"),
                 })
        {
            var item = new MenuItem { Header = name };
            item.Click += async (_, _) => await applyPreset(preset);
            fans.Items.Add(item);
        }

        menu.Items.Add(fans);

        _lightsItem = new MenuItem { Header = "Apagar las luces", Icon = new SymbolIcon(SymbolRegular.LightbulbFilament24) };
        _lightsItem.Click += async (_, _) => await setLightsEnabled(!lightsEnabled());
        menu.Items.Add(_lightsItem);

        _profilesMenu = new MenuItem { Header = "Perfiles", Icon = new SymbolIcon(SymbolRegular.Layer24), Visibility = System.Windows.Visibility.Collapsed };
        menu.Items.Add(_profilesMenu);

        menu.Items.Add(new Separator());
        var quit = new MenuItem { Header = "Salir", Icon = new SymbolIcon(SymbolRegular.Power24) };
        quit.Click += (_, _) => exit();
        menu.Items.Add(quit);

        menu.Opened += (_, _) => _lightsItem.Header = lightsEnabled() ? "Apagar las luces" : "Encender las luces";

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/cefiro.ico")),
            ToolTipText = "Céfiro",
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(showWindow),
            MenuActivation = PopupActivationMode.RightClick,
        };
        _icon.ForceCreate(false);
    }

    /// <summary>Menú "Perfiles" (lo rellena la Fase 5).</summary>
    public MenuItem ProfilesMenu => _profilesMenu;

    public void SetTooltip(string text) => _icon.ToolTipText = text;

    public void Notify(string title, string message, bool warning = false)
    {
        try
        {
            _icon.ShowNotification(title, message, warning ? NotificationIcon.Warning : NotificationIcon.Info);
        }
        catch
        {
            // Notificaciones desactivadas en Windows: no pasa nada.
        }
    }

    public void Dispose() => _icon.Dispose();
}
