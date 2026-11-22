using System.Windows;
using System.Windows.Media;
using Cefiro.Ipc.Contracts;

namespace Cefiro.App.Controls;

/// <summary>Miniatura de una curva de ventilador (sin ejes) con un punto en la temperatura actual.</summary>
public sealed class CurvePreview : FrameworkElement
{
    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(IReadOnlyList<CurvePointDto>), typeof(CurvePreview), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TemperatureProperty = DependencyProperty.Register(
        nameof(Temperature), typeof(double), typeof(CurvePreview), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FloorProperty = DependencyProperty.Register(
        nameof(Floor), typeof(double), typeof(CurvePreview), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(CurvePreview), new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE5, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    private const double MinT = 20;
    private const double MaxT = 100;

    public IReadOnlyList<CurvePointDto>? Curve { get => (IReadOnlyList<CurvePointDto>?)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
    public double Temperature { get => (double)GetValue(TemperatureProperty); set => SetValue(TemperatureProperty, value); }
    public double Floor { get => (double)GetValue(FloorProperty); set => SetValue(FloorProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 56 : availableSize.Height);

    private Point Map(double t, double p) => new(
        (Math.Clamp(t, MinT, MaxT) - MinT) / (MaxT - MinT) * ActualWidth,
        ActualHeight - 3 - Math.Clamp(p, 0, 100) / 100 * (ActualHeight - 6));

    protected override void OnRender(DrawingContext dc)
    {
        var curve = Curve;
        if (curve is null || curve.Count < 2 || ActualWidth < 10 || ActualHeight < 10)
            return;

        // Franja del mínimo de seguridad.
        if (Floor > 0.5)
        {
            var y = Map(MinT, Floor).Y;
            var band = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0x3D, 0x5A));
            band.Freeze();
            dc.DrawRectangle(band, null, new Rect(0, y, ActualWidth, Math.Max(0, ActualHeight - y)));
        }

        var points = curve.Select(p => Map(p.Temperature, p.Percent)).ToList();
        var first = new Point(0, points[0].Y);
        var last = new Point(ActualWidth, points[^1].Y);

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, ActualHeight), true, true);
            ctx.LineTo(first, false, false);
            foreach (var p in points)
                ctx.LineTo(p, false, false);
            ctx.LineTo(last, false, false);
            ctx.LineTo(new Point(ActualWidth, ActualHeight), false, false);
        }

        area.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(0x48, Color.R, Color.G, Color.B), Color.FromArgb(0x02, Color.R, Color.G, Color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(first, false, false);
            foreach (var p in points)
                ctx.LineTo(p, true, true);
            ctx.LineTo(last, true, true);
        }

        line.Freeze();
        var hot = Color.FromRgb(0xFF, 0x2E, 0x88);
        var stroke = new LinearGradientBrush(Color, hot, new Point(0, 0), new Point(1, 0));
        stroke.Freeze();
        dc.DrawGeometry(null, new Pen(stroke, 2) { LineJoin = PenLineJoin.Round }, line);

        if (!double.IsNaN(Temperature))
        {
            var t = Math.Clamp(Temperature, MinT, MaxT);
            var value = Evaluate(curve, t);
            var p = Map(t, value);
            var dash = new SolidColorBrush(Color.FromArgb(0x70, Color.R, Color.G, Color.B));
            dash.Freeze();
            dc.DrawLine(new Pen(dash, 1) { DashStyle = new DashStyle([3, 3], 0) }, new Point(p.X, 0), new Point(p.X, ActualHeight));
            var glow = new SolidColorBrush(Color.FromArgb(0x60, Color.R, Color.G, Color.B));
            glow.Freeze();
            dc.DrawEllipse(glow, null, p, 7, 7);
            dc.DrawEllipse(Brushes.White, null, p, 3.2, 3.2);
        }
    }

    private static double Evaluate(IReadOnlyList<CurvePointDto> curve, double t)
    {
        if (t <= curve[0].Temperature) return curve[0].Percent;
        for (var i = 1; i < curve.Count; i++)
        {
            if (t <= curve[i].Temperature)
            {
                var a = curve[i - 1];
                var b = curve[i];
                return b.Temperature - a.Temperature <= 0 ? b.Percent : a.Percent + (b.Percent - a.Percent) * (t - a.Temperature) / (b.Temperature - a.Temperature);
            }
        }

        return curve[^1].Percent;
    }
}

/// <summary>Franja roja bajo un deslizador que marca la zona por debajo del mínimo de seguridad.</summary>
public sealed class FloorBand : FrameworkElement
{
    public static readonly DependencyProperty FloorProperty = DependencyProperty.Register(
        nameof(Floor), typeof(double), typeof(FloorBand), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(FloorBand), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Band = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x3D, 0x5A)));

    public double Floor { get => (double)GetValue(FloorProperty); set => SetValue(FloorProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        if (Floor <= 0 || Maximum <= 0 || ActualWidth < 20)
            return;

        // El recorrido útil del deslizador deja 9 px a cada lado (medio pulgar).
        const double inset = 9;
        var width = (ActualWidth - inset * 2) * Math.Clamp(Floor / Maximum, 0, 1);
        dc.DrawRoundedRectangle(Band, null, new Rect(inset, ActualHeight / 2 - 2, width, 4), 2, 2);
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
