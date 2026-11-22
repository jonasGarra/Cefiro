using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Cefiro.App.Controls;

/// <summary>
/// Indicador circular de 270° con degradado, brillo y animación suave del valor.
/// Dibujado a mano (OnRender) para que cueste muy poco aunque haya varios en pantalla.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryValueProperty = DependencyProperty.Register(
        nameof(SecondaryValue), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RingGauge), new FrameworkPropertyMetadata("—", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(RingGauge), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RingGauge), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartColorProperty = DependencyProperty.Register(
        nameof(StartColor), typeof(Color), typeof(RingGauge), new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE5, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndColorProperty = DependencyProperty.Register(
        nameof(EndColor), typeof(Color), typeof(RingGauge), new FrameworkPropertyMetadata(Color.FromRgb(0x7C, 0x4D, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface NumberFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);
    private static readonly Typeface CaptionFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFF)));
    private static readonly Brush CaptionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA6, 0xC0)));

    public RingGauge()
    {
        // Brillo de neón bajo el arco (el efecto lo aplica la GPU).
        Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 0, Opacity = 0.55, Color = StartColor };
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>Arco interior fino (p. ej. el % de carga bajo la temperatura). -1 = no se dibuja.</summary>
    public double SecondaryValue { get => (double)GetValue(SecondaryValueProperty); set => SetValue(SecondaryValueProperty, value); }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public Color StartColor { get => (Color)GetValue(StartColorProperty); set => SetValue(StartColorProperty, value); }
    public Color EndColor { get => (Color)GetValue(EndColorProperty); set => SetValue(EndColorProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RingGauge)d;
        var to = Math.Clamp((double)e.NewValue, 0, gauge.Maximum);
        gauge.BeginAnimation(DisplayValueProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(650))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == EndColorProperty && Effect is DropShadowEffect glow)
            glow.Color = EndColor;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = double.IsInfinity(availableSize.Width) ? 160 : Math.Min(availableSize.Width, double.IsInfinity(availableSize.Height) ? availableSize.Width : availableSize.Height);
        return new Size(s, s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20)
            return;

        var t = Thickness;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - t / 2 - 4;
        const double start = 135;  // grados (abajo a la izquierda)
        const double sweep = 270;

        // Pista.
        var trackPen = new Pen(TrackBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, trackPen, Arc(center, radius, start, sweep));

        // Valor con degradado.
        var fraction = Maximum <= 0 ? 0 : Math.Clamp(DisplayValue / Maximum, 0, 1);
        if (fraction > 0.002)
        {
            var gradient = new LinearGradientBrush(StartColor, EndColor, new Point(0, 1), new Point(1, 0));
            gradient.Freeze();
            var pen = new Pen(gradient, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Arc(center, radius, start, sweep * fraction));

            // Punto brillante en la punta.
            var tip = PointOn(center, radius, start + sweep * fraction);
            dc.DrawEllipse(Brushes.White, null, tip, t * 0.22, t * 0.22);
        }

        // Arco secundario interior (carga).
        if (SecondaryValue >= 0)
        {
            var inner = radius - t - 4;
            var secPen = new Pen(TrackBrush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, secPen, Arc(center, inner, start, sweep));
            var secFraction = Math.Clamp(SecondaryValue / 100, 0, 1);
            if (secFraction > 0.002)
            {
                var brush = new SolidColorBrush(Color.FromArgb(0xCC, EndColor.R, EndColor.G, EndColor.B));
                brush.Freeze();
                dc.DrawGeometry(null, new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    Arc(center, inner, start, sweep * secFraction));
            }
        }

        // Número central y unidad.
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var numberSize = size * 0.24;
        var number = new FormattedText(Text ?? "—", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, NumberFace, numberSize, TextBrush, dpi);
        var unit = new FormattedText(Unit ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CaptionFace, numberSize * 0.38, CaptionBrush, dpi);
        var totalWidth = number.Width + (unit.Width > 0 ? unit.Width + 3 : 0);
        var x = center.X - totalWidth / 2;
        var y = center.Y - number.Height / 2 - size * 0.02;
        dc.DrawText(number, new Point(x, y));
        if (unit.Width > 0)
            dc.DrawText(unit, new Point(x + number.Width + 3, y + number.Baseline - unit.Baseline));

        if (!string.IsNullOrEmpty(Caption))
        {
            // En el hueco inferior del arco, por debajo de sus extremos redondeados: así nunca se pisan.
            var caption = new FormattedText(Caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CaptionFace, Math.Max(10, size * 0.068), CaptionBrush, dpi)
            {
                MaxTextWidth = Math.Max(20, size - 8),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
            };
            var top = Math.Min(center.Y + radius * 0.7071 + t / 2 + 2, ActualHeight - caption.Height);
            dc.DrawText(caption, new Point(center.X - caption.MaxTextWidth / 2, top));
        }
    }

    private static Point PointOn(Point c, double r, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private static Geometry Arc(Point center, double radius, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Min(sweepDeg, 359.9);
        var startPoint = PointOn(center, radius, startDeg);
        var endPoint = PointOn(center, radius, startDeg + sweepDeg);
        var figure = new PathFigure { StartPoint = startPoint, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(endPoint, new Size(radius, radius), 0, sweepDeg > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
