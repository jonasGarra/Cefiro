using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Cefiro.App.Controls;

/// <summary>Barra de nivel fina con degradado y transición suave.</summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MeterBar), new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(MeterBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerProperty = DependencyProperty.Register(
        nameof(Marker), typeof(double), typeof(MeterBar), new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartColorProperty = DependencyProperty.Register(
        nameof(StartColor), typeof(Color), typeof(MeterBar), new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE5, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndColorProperty = DependencyProperty.Register(
        nameof(EndColor), typeof(Color), typeof(MeterBar), new FrameworkPropertyMetadata(Color.FromRgb(0x7C, 0x4D, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }

    /// <summary>Marca opcional (p. ej. el mínimo de seguridad). -1 = sin marca.</summary>
    public double Marker { get => (double)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    public Color StartColor { get => (Color)GetValue(StartColorProperty); set => SetValue(StartColorProperty, value); }
    public Color EndColor { get => (Color)GetValue(EndColorProperty); set => SetValue(EndColorProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (MeterBar)d;
        bar.BeginAnimation(DisplayValueProperty, new DoubleAnimation(Math.Clamp((double)e.NewValue, 0, 100), TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 6);

    protected override void OnRender(DrawingContext dc)
    {
        var h = Math.Min(ActualHeight, 6);
        var y = (ActualHeight - h) / 2;
        var r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, y, ActualWidth, h), r, r);

        var w = ActualWidth * Math.Clamp(DisplayValue / 100, 0, 1);
        if (w > 1)
        {
            var fill = new LinearGradientBrush(StartColor, EndColor, 0) { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(ActualWidth, 0) };
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, new Rect(0, y, w, h), r, r);
        }

        if (Marker >= 0)
        {
            var x = ActualWidth * Math.Clamp(Marker / 100, 0, 1);
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3D, 0x5A)), 2);
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, y - 3), new Point(x, y + h + 3));
        }
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
