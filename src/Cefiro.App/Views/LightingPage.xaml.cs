using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cefiro.App.Controls;
using Cefiro.App.ViewModels;

namespace Cefiro.App.Views;

public partial class LightingPage : UserControl
{
    public LightingPage()
    {
        InitializeComponent();
    }

    /// <summary>Clic sobre un LED de la vista previa: ese LED pasa a ser el destino del efecto.</summary>
    private void OnLedClicked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is LedStrip { DataContext: LedZoneViewModel zone } strip && DataContext is LightingViewModel vm)
            vm.SelectLed(zone, strip.ClickedIndex);
    }

    private void OnFavoriteRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FavoriteColor favorite } && DataContext is LightingViewModel vm)
        {
            vm.RemoveFavorite(favorite);
            e.Handled = true;
        }
    }
}
