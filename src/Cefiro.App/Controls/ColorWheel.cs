using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Cefiro.Core.Lighting;

namespace Cefiro.App.Controls;

/// <summary>
/// Rueda de color HSV: el ángulo es el tono y la distancia al centro la saturación. El brillo se elige
/// aparte (propiedad Brightness, normalmente con un deslizador).
/// </summary>
public sealed class ColorWheel : FrameworkElement
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(Color), typeof(ColorWheel),
        new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE5, 0xFF), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(
        nameof(Brightness), typeof(double), typeof(ColorWheel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBrightnessChanged));

    private WriteableBitmap? _wheel;
    private double _hue = 187;
    private double _saturation = 1;
    private bool _updating;

    public ColorWheel()
    {
        Cursor = Cursors.Cross;
        Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black };
    }

    public Color SelectedColor { get => (Color)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var wheel = (ColorWheel)d;
        if (wheel._updating)
            return;

        var c = (Color)e.NewValue;
        var (h, s, v) = new Rgb(c.R, c.G, c.B).ToHsv();
        if (s > 0.01)
            wheel._hue = h;
        wheel._saturation = s;
        wheel._updating = true;
        wheel.Brightness = v;
        wheel._updating = false;
        wheel.InvalidateVisual();
    }

    private static void OnBrightnessChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var wheel = (ColorWheel)d;
        if (wheel._updating)
            return;
        wheel.Commit();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = Math.Min(double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width,
                         double.IsInfinity(availableSize.Height) ? 220 : availableSize.Height);
        return new Size(s, s);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _wheel = null;
    }

    private void BuildWheel(int size)
    {
        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        var r = size / 2.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - r + 0.5;
                var dy = y - r + 0.5;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                var i = (y * size + x) * 4;
                if (dist > r)
                    continue;

                var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var c = Rgb.FromHsv(hue, Math.Min(1, dist / r), 1);
                // Borde suavizado.
                var alpha = Math.Clamp(r - dist, 0, 1);
                pixels[i] = (byte)(c.B * alpha);
                pixels[i + 1] = (byte)(c.G * alpha);
                pixels[i + 2] = (byte)(c.R * alpha);
                pixels[i + 3] = (byte)(255 * alpha);
            }
        }

        bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bmp.Freeze();
        _wheel = bmp;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20)
            return;

        var px = (int)Math.Round(size * VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (_wheel is null || _wheel.PixelWidth != px)
            BuildWheel(px);

        var rect = new Rect((ActualWidth - size) / 2, (ActualHeight - size) / 2, size, size);
        dc.DrawImage(_wheel, rect);

        // Velo oscuro según el brillo elegido, para ver cómo quedará.
        if (Brightness < 0.999)
        {
            var veil = new SolidColorBrush(Color.FromArgb((byte)((1 - Brightness) * 220), 0, 0, 0));
            veil.Freeze();
            dc.DrawEllipse(veil, null, new Point(rect.X + size / 2, rect.Y + size / 2), size / 2, size / 2);
        }

        // Selector.
        var radius = size / 2;
        var angle = _hue * Math.PI / 180;
        var p = new Point(rect.X + radius + Math.Cos(angle) * _saturation * radius, rect.Y + radius + Math.Sin(angle) * _saturation * radius);
        var fill = new SolidColorBrush(SelectedColor);
        fill.Freeze();
        dc.DrawEllipse(fill, new Pen(Brushes.White, 3), p, 9, 9);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 1), p, 11, 11);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)
            Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    private void Pick(Point position)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;
        var dx = position.X - cx;
        var dy = position.Y - cy;
        _hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        _saturation = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / (size / 2), 0, 1);
        Commit();
    }

    private void Commit()
    {
        var c = Rgb.FromHsv(_hue, _saturation, Math.Clamp(Brightness, 0, 1));
        _updating = true;
        SelectedColor = Color.FromRgb(c.R, c.G, c.B);
        _updating = false;
        InvalidateVisual();
    }
}
