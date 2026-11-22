using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cefiro.App.ViewModels;
using Wpf.Ui.Controls;

namespace Cefiro.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Width = Math.Max(MinWidth, vm.Settings.Current.WindowWidth);
        Height = Math.Max(MinHeight, vm.Settings.Current.WindowHeight);

        vm.PropertyChanged += OnViewModelChanged;
        SizeChanged += (_, _) => _resizeDebounce = DateTime.UtcNow;
    }

    private DateTime _resizeDebounce;

    /// <summary>Si es true, cerrar la ventana la esconde en la bandeja en lugar de salir.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Se ha pedido salir de verdad (desde la bandeja).</summary>
    public bool ExitRequested { get; set; }

    public event EventHandler? HiddenToTray;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
            AnimatePageIn();
    }

    /// <summary>Transición de página: aparece desde abajo con un fundido.</summary>
    private void AnimatePageIn()
    {
        var duration = TimeSpan.FromMilliseconds(280);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        if (PageHost.RenderTransform is TranslateTransform move)
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, duration) { EasingFunction = ease });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ExitRequested && CloseToTray)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (WindowState == WindowState.Normal && _resizeDebounce != default)
        {
            var (w, h) = (Width, Height);
            _vm.Settings.Update(s => s with { WindowWidth = w, WindowHeight = h });
        }

        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Renderiza la ventana a una imagen (modo --capture para revisar el diseño).</summary>
    public void CaptureTo(string path)
    {
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }
}
