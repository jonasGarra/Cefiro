using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cefiro.App.Infrastructure;
using Cefiro.Ipc.Contracts;

namespace Cefiro.App.Controls;

/// <summary>
/// Editor de curvas de ventilador. Arrastra los puntos (se ajustan a 1 °C y 1 %), doble clic para añadir
/// un punto y clic derecho sobre un punto para quitarlo. Muestra en directo la temperatura actual,
/// la salida actual y la franja del mínimo de seguridad.
/// </summary>
public sealed class FanCurveEditor : FrameworkElement
{
    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(IReadOnlyList<CurvePointDto>), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender, OnCurveChanged));

    public static readonly DependencyProperty CurrentTemperatureProperty = DependencyProperty.Register(
        nameof(CurrentTemperature), typeof(double), typeof(FanCurveEditor), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentOutputProperty = DependencyProperty.Register(
        nameof(CurrentOutput), typeof(double), typeof(FanCurveEditor), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SafetyFloorProperty = DependencyProperty.Register(
        nameof(SafetyFloor), typeof(double), typeof(FanCurveEditor), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
        nameof(AccentColor), typeof(Color), typeof(FanCurveEditor), new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE5, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    private const double MinT = 20;
    private const double MaxT = 100;
    private const double Left = 44;
    private const double Bottom = 30;
    private const double Top = 14;
    private const double Right = 16;

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Small, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5D, 0x68, 0x80)));
    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Pen AxisPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))), 1));

    private readonly List<Point> _points = [];
    private int _dragIndex = -1;
    private int _hoverIndex = -1;
    private bool _internalUpdate;

    public FanCurveEditor()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Se ha terminado una edición (soltar un punto, añadirlo o quitarlo).</summary>
    public event EventHandler? CurveEdited;

    public IReadOnlyList<CurvePointDto>? Curve { get => (IReadOnlyList<CurvePointDto>?)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
    public double CurrentTemperature { get => (double)GetValue(CurrentTemperatureProperty); set => SetValue(CurrentTemperatureProperty, value); }
    public double CurrentOutput { get => (double)GetValue(CurrentOutputProperty); set => SetValue(CurrentOutputProperty, value); }
    public double SafetyFloor { get => (double)GetValue(SafetyFloorProperty); set => SetValue(SafetyFloorProperty, value); }
    public Color AccentColor { get => (Color)GetValue(AccentColorProperty); set => SetValue(AccentColorProperty, value); }

    private static void OnCurveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (FanCurveEditor)d;
        if (editor._internalUpdate || editor._dragIndex >= 0)
            return;

        editor._points.Clear();
        if (e.NewValue is IReadOnlyList<CurvePointDto> curve)
            editor._points.AddRange(curve.Select(p => new Point(p.Temperature, p.Percent)));
    }

    private Rect Plot => new(Left, Top, Math.Max(10, ActualWidth - Left - Right), Math.Max(10, ActualHeight - Top - Bottom));

    private Point ToScreen(Point v)
    {
        var p = Plot;
        return new Point(p.Left + (v.X - MinT) / (MaxT - MinT) * p.Width, p.Bottom - v.Y / 100 * p.Height);
    }

    private Point ToValue(Point s)
    {
        var p = Plot;
        return new Point(MinT + (s.X - p.Left) / p.Width * (MaxT - MinT), (p.Bottom - s.Y) / p.Height * 100);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 60 || ActualHeight < 60)
            return;

        var plot = Plot;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // para recibir el ratón

        // Franja del mínimo de seguridad.
        if (SafetyFloor > 0.5)
        {
            var y = ToScreen(new Point(MinT, SafetyFloor)).Y;
            var band = new LinearGradientBrush(Color.FromArgb(0x30, 0xFF, 0x3D, 0x5A), Color.FromArgb(0x08, 0xFF, 0x3D, 0x5A), 90);
            band.Freeze();
            dc.DrawRectangle(band, null, new Rect(plot.Left, y, plot.Width, plot.Bottom - y));
            var label = Text($"Mínimo de seguridad: {SafetyFloor:0} %", 11, Color.FromRgb(0xFF, 0x7A, 0x90), dpi, bold: true);
            dc.DrawText(label, new Point(plot.Right - label.Width - 6, y + 4));
        }

        // Rejilla.
        for (var t = 20; t <= 100; t += 10)
        {
            var x = ToScreen(new Point(t, 0)).X;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = Text($"{Format.TemperatureValue(t)}°", 10.5, null, dpi);
            dc.DrawText(label, new Point(x - label.Width / 2, plot.Bottom + 6));
        }

        for (var pct = 0; pct <= 100; pct += 20)
        {
            var y = ToScreen(new Point(MinT, pct)).Y;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text($"{pct} %", 10.5, null, dpi);
            dc.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }

        dc.DrawLine(AxisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));

        if (_points.Count >= 2)
        {
            var screen = _points.Select(ToScreen).ToList();
            var first = new Point(plot.Left, screen[0].Y);
            var last = new Point(plot.Right, screen[^1].Y);

            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(first, false, false);
                foreach (var p in screen)
                    ctx.LineTo(p, true, true);
                ctx.LineTo(last, true, true);
            }

            line.Freeze();

            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(plot.Left, plot.Bottom), true, true);
                ctx.LineTo(first, false, false);
                foreach (var p in screen)
                    ctx.LineTo(p, false, false);
                ctx.LineTo(last, false, false);
                ctx.LineTo(new Point(plot.Right, plot.Bottom), false, false);
            }

            area.Freeze();

            var hot = Color.FromRgb(0xFF, 0x2E, 0x88);
            var fill = new LinearGradientBrush(Color.FromArgb(0x50, AccentColor.R, AccentColor.G, AccentColor.B), Color.FromArgb(0x04, AccentColor.R, AccentColor.G, AccentColor.B), 90);
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);

            var stroke = new LinearGradientBrush(AccentColor, hot, new Point(0, 0), new Point(1, 0));
            stroke.Freeze();
            var glow = new LinearGradientBrush(Color.FromArgb(0x40, AccentColor.R, AccentColor.G, AccentColor.B), Color.FromArgb(0x40, hot.R, hot.G, hot.B), new Point(0, 0), new Point(1, 0));
            glow.Freeze();
            dc.DrawGeometry(null, new Pen(glow, 9) { LineJoin = PenLineJoin.Round }, line);
            dc.DrawGeometry(null, new Pen(stroke, 2.6) { LineJoin = PenLineJoin.Round }, line);

            // Temperatura actual: línea vertical y valor de la curva en ese punto.
            if (!double.IsNaN(CurrentTemperature))
            {
                var t = Math.Clamp(CurrentTemperature, MinT, MaxT);
                var x = ToScreen(new Point(t, 0)).X;
                var accent = new SolidColorBrush(Color.FromArgb(0xB0, AccentColor.R, AccentColor.G, AccentColor.B));
                accent.Freeze();
                dc.DrawLine(new Pen(accent, 1.5) { DashStyle = new DashStyle([4, 4], 0) }, new Point(x, plot.Top), new Point(x, plot.Bottom));

                var curveValue = Evaluate(t);
                var onCurve = ToScreen(new Point(t, curveValue));
                dc.DrawEllipse(Brushes.White, new Pen(accent, 3), onCurve, 5, 5);

                var pill = Text($"{Format.Temperature((float)CurrentTemperature)}  →  {curveValue:0} %", 11.5, Colors.White, dpi, bold: true);
                var pillRect = new Rect(Math.Min(x + 8, plot.Right - pill.Width - 16), plot.Top + 4, pill.Width + 14, pill.Height + 6);
                var pillBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x16, 0x26));
                pillBrush.Freeze();
                dc.DrawRoundedRectangle(pillBrush, new Pen(accent, 1), pillRect, 8, 8);
                dc.DrawText(pill, new Point(pillRect.X + 7, pillRect.Y + 3));
            }

            // Salida actual: marca en el eje vertical.
            if (!double.IsNaN(CurrentOutput))
            {
                var y = ToScreen(new Point(MinT, Math.Clamp(CurrentOutput, 0, 100))).Y;
                var marker = new StreamGeometry();
                using (var ctx = marker.Open())
                {
                    ctx.BeginFigure(new Point(plot.Left - 1, y), true, true);
                    ctx.LineTo(new Point(plot.Left + 9, y - 6), true, true);
                    ctx.LineTo(new Point(plot.Left + 9, y + 6), true, true);
                }

                marker.Freeze();
                var markerBrush = new SolidColorBrush(AccentColor);
                markerBrush.Freeze();
                dc.DrawGeometry(markerBrush, null, marker);
            }

            // Puntos.
            for (var i = 0; i < screen.Count; i++)
            {
                var active = i == _dragIndex || i == _hoverIndex;
                var r = active ? 8.5 : 6.5;
                if (active)
                {
                    var halo = new SolidColorBrush(Color.FromArgb(0x50, AccentColor.R, AccentColor.G, AccentColor.B));
                    halo.Freeze();
                    dc.DrawEllipse(halo, null, screen[i], r + 7, r + 7);
                }

                var pointFill = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x1E));
                pointFill.Freeze();
                var ring = new SolidColorBrush(AccentColor);
                ring.Freeze();
                dc.DrawEllipse(pointFill, new Pen(ring, 3), screen[i], r, r);

                if (active)
                {
                    var tip = Text($"{Format.TemperatureValue((float)_points[i].X)}{Format.TemperatureUnit} · {_points[i].Y:0} %", 11.5, Colors.White, dpi, bold: true);
                    var tipRect = new Rect(screen[i].X - tip.Width / 2 - 7, screen[i].Y - tip.Height - 22, tip.Width + 14, tip.Height + 6);
                    if (tipRect.Top < 0)
                        tipRect.Y = screen[i].Y + 16;
                    var tipBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x16, 0x26));
                    tipBrush.Freeze();
                    dc.DrawRoundedRectangle(tipBrush, new Pen(ring, 1), tipRect, 8, 8);
                    dc.DrawText(tip, new Point(tipRect.X + 7, tipRect.Y + 3));
                }
            }
        }
    }

    private double Evaluate(double t)
    {
        if (_points.Count == 0) return 100;
        if (t <= _points[0].X) return _points[0].Y;
        for (var i = 1; i < _points.Count; i++)
        {
            if (t <= _points[i].X)
            {
                var a = _points[i - 1];
                var b = _points[i];
                return b.X - a.X <= 0 ? b.Y : a.Y + (b.Y - a.Y) * (t - a.X) / (b.X - a.X);
            }
        }

        return _points[^1].Y;
    }

    private int HitTest(Point screenPoint)
    {
        for (var i = 0; i < _points.Count; i++)
        {
            var p = ToScreen(_points[i]);
            if ((p - screenPoint).Length <= 12)
                return i;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);

        if (_dragIndex >= 0 && IsMouseCaptured)
        {
            var v = ToValue(pos);
            var minT = _dragIndex > 0 ? _points[_dragIndex - 1].X + 1 : MinT;
            var maxT = _dragIndex < _points.Count - 1 ? _points[_dragIndex + 1].X - 1 : MaxT;
            var minP = _dragIndex > 0 ? _points[_dragIndex - 1].Y : 0;
            var maxP = _dragIndex < _points.Count - 1 ? _points[_dragIndex + 1].Y : 100;
            _points[_dragIndex] = new Point(
                Math.Round(Math.Clamp(v.X, minT, maxT)),
                Math.Round(Math.Clamp(v.Y, minP, maxP)));
            InvalidateVisual();
            return;
        }

        var hover = HitTest(pos);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragIndex < 0 && _hoverIndex >= 0)
        {
            _hoverIndex = -1;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var pos = e.GetPosition(this);

        if (e.ClickCount == 2 && HitTest(pos) < 0 && _points.Count < 16 && Plot.Contains(pos))
        {
            var v = ToValue(pos);
            var t = Math.Round(Math.Clamp(v.X, MinT, MaxT));
            if (_points.All(p => Math.Abs(p.X - t) >= 1))
            {
                _points.Add(new Point(t, Math.Round(Math.Clamp(v.Y, 0, 100))));
                _points.Sort((a, b) => a.X.CompareTo(b.X));
                Publish();
            }

            e.Handled = true;
            return;
        }

        _dragIndex = HitTest(pos);
        if (_dragIndex >= 0)
        {
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex >= 0)
        {
            _dragIndex = -1;
            ReleaseMouseCapture();
            Publish();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var index = HitTest(e.GetPosition(this));
        if (index >= 0 && _points.Count > 2)
        {
            _points.RemoveAt(index);
            _hoverIndex = -1;
            Publish();
            e.Handled = true;
        }
    }

    private void Publish()
    {
        _internalUpdate = true;
        Curve = _points.Select(p => new CurvePointDto((float)p.X, (float)p.Y)).ToList();
        _internalUpdate = false;
        InvalidateVisual();
        CurveEdited?.Invoke(this, EventArgs.Empty);
    }

    private static FormattedText Text(string text, double size, Color? color, double dpi, bool bold = false)
    {
        var brush = color is { } c ? new SolidColorBrush(c) : LabelBrush;
        if (brush.CanFreeze && !brush.IsFrozen)
            brush.Freeze();
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : LabelFace, size, brush, dpi);
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
