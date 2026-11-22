using System.Windows;
using System.Windows.Media;

namespace Cefiro.App.Controls;

/// <summary>Minigráfica ligera (línea + área con degradado) para las tarjetas.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<float?>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE5, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<float?>? Values { get => (IReadOnlyList<float?>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        if (values is null || values.Count < 2 || ActualWidth < 4 || ActualHeight < 4)
            return;

        var present = values.Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
        if (present.Count < 2)
            return;

        var min = double.IsNaN(Minimum) ? present.Min() : Minimum;
        var max = double.IsNaN(Maximum) ? present.Max() : Maximum;
        if (max - min < 1)
        {
            var mid = (max + min) / 2;
            min = mid - 0.5;
            max = mid + 0.5;
        }

        var w = ActualWidth;
        var h = ActualHeight - 2;
        var step = w / (values.Count - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            var started = false;
            Point first = default, last = default;
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is not { } v)
                    continue;

                var p = new Point(i * step, 1 + h - (v - min) / (max - min) * h);
                if (!started)
                {
                    lc.BeginFigure(p, false, false);
                    ac.BeginFigure(new Point(p.X, ActualHeight), true, true);
                    ac.LineTo(p, false, false);
                    first = p;
                    started = true;
                }
                else
                {
                    lc.LineTo(p, true, true);
                    ac.LineTo(p, false, false);
                }

                last = p;
            }

            if (started)
                ac.LineTo(new Point(last.X, ActualHeight), false, false);
            _ = first;
        }

        line.Freeze();
        area.Freeze();

        var fill = new LinearGradientBrush(Color.FromArgb(0x55, Color.R, Color.G, Color.B), Color.FromArgb(0x00, Color.R, Color.G, Color.B), 90);
        fill.Freeze();
        var stroke = new SolidColorBrush(Color);
        stroke.Freeze();

        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(stroke, 1.8) { LineJoin = PenLineJoin.Round }, line);
    }
}
