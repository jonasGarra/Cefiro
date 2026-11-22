using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Cefiro.App.Controls;

/// <summary>
/// Reloj común de las animaciones de ventiladores: 30 fotogramas por segundo y solo mientras hay
/// alguno visible. Al girar solo cambia una transformación (no se vuelve a dibujar nada).
/// </summary>
internal static class FanAnimator
{
    private static readonly HashSet<IAnimatedFan> Fans = [];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;
    private static double _last;

    public static void Register(IAnimatedFan fan)
    {
        Fans.Add(fan);
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, _) => Tick();
        }

        if (!_timer.IsEnabled)
        {
            _last = Clock.Elapsed.TotalSeconds;
            _timer.Start();
        }
    }

    public static void Unregister(IAnimatedFan fan)
    {
        Fans.Remove(fan);
        if (Fans.Count == 0)
            _timer?.Stop();
    }

    private static void Tick()
    {
        var now = Clock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.1, now - _last);
        _last = now;
        foreach (var fan in Fans)
            fan.Advance(dt);
    }
}

internal interface IAnimatedFan
{
    void Advance(double dt);
}

/// <summary>Ventilador del esquema: marco, anillo de 8 LEDs y aspas girando a su velocidad real.</summary>
public sealed class FanView : FrameworkElement, IAnimatedFan
{
    public static readonly DependencyProperty RpmProperty = DependencyProperty.Register(
        nameof(Rpm), typeof(double), typeof(FanView), new PropertyMetadata(0.0));

    public static readonly DependencyProperty LedColorsProperty = DependencyProperty.Register(
        nameof(LedColors), typeof(IReadOnlyList<Color>), typeof(FanView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BladeCountProperty = DependencyProperty.Register(
        nameof(BladeCount), typeof(int), typeof(FanView), new FrameworkPropertyMetadata(7, (d, _) => ((FanView)d).DrawBlades()));

    private static readonly Brush FrameFill = Freeze(new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen FramePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Brush BladeFill = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xE8, 0xEE, 0xFF)));
    private static readonly Brush HubFill = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFF)));
    private static readonly Color DarkLed = Color.FromArgb(0x40, 0x9A, 0xA6, 0xC0);

    private readonly DrawingVisual _blades = new();
    private readonly RotateTransform _rotation = new();
    private double _angle;

    public FanView()
    {
        _blades.Transform = _rotation;
        AddVisualChild(_blades);
        Loaded += (_, _) => FanAnimator.Register(this);
        Unloaded += (_, _) => FanAnimator.Unregister(this);
    }

    public double Rpm { get => (double)GetValue(RpmProperty); set => SetValue(RpmProperty, value); }
    public IReadOnlyList<Color>? LedColors { get => (IReadOnlyList<Color>?)GetValue(LedColorsProperty); set => SetValue(LedColorsProperty, value); }
    public int BladeCount { get => (int)GetValue(BladeCountProperty); set => SetValue(BladeCountProperty, value); }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _blades;

    /// <summary>
    /// Velocidad visual: la real sería imposible de ver (2000 RPM = 33 vueltas/s), así que se comprime
    /// con una raíz: más RPM sigue viéndose más rápido, pero siempre se distingue el giro.
    /// </summary>
    public void Advance(double dt)
    {
        if (Rpm <= 1 || !IsVisible)
            return;

        var turnsPerSecond = 0.35 * Math.Sqrt(Rpm / 60.0);
        _angle = (_angle + turnsPerSecond * 360 * dt) % 360;
        _rotation.Angle = _angle;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        DrawBlades();
    }

    private void DrawBlades()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8)
            return;

        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        _rotation.CenterX = c.X;
        _rotation.CenterY = c.Y;
        var r = size * 0.36;
        var hub = size * 0.11;
        var count = Math.Max(3, BladeCount);

        using var dc = _blades.RenderOpen();
        for (var i = 0; i < count; i++)
        {
            var a = 2 * Math.PI * i / count;
            var span = 2 * Math.PI / count * 0.62;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                var root1 = Polar(c, hub, a);
                var root2 = Polar(c, hub, a + span * 0.55);
                var tip1 = Polar(c, r, a + span * 0.45);
                var tip2 = Polar(c, r, a + span);
                ctx.BeginFigure(root1, true, true);
                ctx.QuadraticBezierTo(Polar(c, r * 0.62, a + span * 0.05), tip1, true, true);
                ctx.ArcTo(tip2, new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                ctx.QuadraticBezierTo(Polar(c, r * 0.55, a + span * 0.8), root2, true, true);
            }

            g.Freeze();
            dc.DrawGeometry(BladeFill, null, g);
        }

        dc.DrawEllipse(HubFill, null, c, hub, hub);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8)
            return;

        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var frame = new Rect(c.X - size / 2 + 1, c.Y - size / 2 + 1, size - 2, size - 2);
        dc.DrawRoundedRectangle(FrameFill, FramePen, frame, size * 0.14, size * 0.14);

        // Anillo de LEDs: 8 arcos alrededor de las aspas (los RS120 tienen 8 LEDs).
        var colors = LedColors;
        var ringRadius = size * 0.41;
        var leds = colors is { Count: > 0 } ? colors.Count : 8;
        for (var i = 0; i < leds; i++)
        {
            var color = colors is { Count: > 0 } ? colors[i] : DarkLed;
            if (color.R + color.G + color.B < 24)
                color = DarkLed;

            var a0 = 2 * Math.PI * i / leds - Math.PI / 2;
            var a1 = a0 + 2 * Math.PI / leds * 0.82;
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                ctx.BeginFigure(Polar(c, ringRadius, a0), false, false);
                ctx.ArcTo(Polar(c, ringRadius, a1), new Size(ringRadius, ringRadius), 0, false, SweepDirection.Clockwise, true, false);
            }

            arc.Freeze();
            var glow = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
            glow.Freeze();
            var core = new SolidColorBrush(color);
            core.Freeze();
            dc.DrawGeometry(null, new Pen(glow, size * 0.09) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
            dc.DrawGeometry(null, new Pen(core, size * 0.035) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
        }
    }

    internal static Point Polar(Point c, double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

/// <summary>Cabezal de la bomba: anillo de 8 LEDs y un rodete interior que gira despacio con las RPM.</summary>
public sealed class PumpView : FrameworkElement, IAnimatedFan
{
    public static readonly DependencyProperty RpmProperty = DependencyProperty.Register(
        nameof(Rpm), typeof(double), typeof(PumpView), new PropertyMetadata(0.0));

    public static readonly DependencyProperty LedColorsProperty = DependencyProperty.Register(
        nameof(LedColors), typeof(IReadOnlyList<Color>), typeof(PumpView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DrawingVisual _impeller = new();
    private readonly RotateTransform _rotation = new();
    private double _angle;

    public PumpView()
    {
        _impeller.Transform = _rotation;
        AddVisualChild(_impeller);
        Loaded += (_, _) => FanAnimator.Register(this);
        Unloaded += (_, _) => FanAnimator.Unregister(this);
    }

    public double Rpm { get => (double)GetValue(RpmProperty); set => SetValue(RpmProperty, value); }
    public IReadOnlyList<Color>? LedColors { get => (IReadOnlyList<Color>?)GetValue(LedColorsProperty); set => SetValue(LedColorsProperty, value); }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _impeller;

    public void Advance(double dt)
    {
        if (Rpm <= 1 || !IsVisible)
            return;

        _angle = (_angle + 0.12 * Math.Sqrt(Rpm / 60.0) * 360 * dt) % 360;
        _rotation.Angle = _angle;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var size = Math.Min(ActualWidth, ActualHeight);
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        _rotation.CenterX = c.X;
        _rotation.CenterY = c.Y;

        using var dc = _impeller.RenderOpen();
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xE8, 0xEE, 0xFF)), Math.Max(1, size * 0.02));
        pen.Freeze();
        for (var i = 0; i < 6; i++)
        {
            var a = Math.PI * 2 * i / 6;
            dc.DrawLine(pen, FanView.Polar(c, size * 0.08, a), FanView.Polar(c, size * 0.24, a + 0.5));
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8)
            return;

        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var body = new RadialGradientBrush(Color.FromRgb(0x1C, 0x24, 0x3A), Color.FromRgb(0x0A, 0x0E, 0x1A));
        body.Freeze();
        dc.DrawEllipse(body, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1), c, size / 2 - 1, size / 2 - 1);

        var colors = LedColors;
        var ring = size * 0.38;
        var leds = colors is { Count: > 0 } ? colors.Count : 8;
        for (var i = 0; i < leds; i++)
        {
            var color = colors is { Count: > 0 } ? colors[i] : Color.FromArgb(0x40, 0x9A, 0xA6, 0xC0);
            var a0 = 2 * Math.PI * i / leds - Math.PI / 2;
            var a1 = a0 + 2 * Math.PI / leds * 0.85;
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                ctx.BeginFigure(FanView.Polar(c, ring, a0), false, false);
                ctx.ArcTo(FanView.Polar(c, ring, a1), new Size(ring, ring), 0, false, SweepDirection.Clockwise, true, false);
            }

            arc.Freeze();
            var glow = new SolidColorBrush(Color.FromArgb(0x60, color.R, color.G, color.B));
            var core = new SolidColorBrush(color);
            glow.Freeze();
            core.Freeze();
            dc.DrawGeometry(null, new Pen(glow, size * 0.12) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
            dc.DrawGeometry(null, new Pen(core, size * 0.045) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
        }
    }
}

/// <summary>Módulo de RAM con su barra de 10 LEDs.</summary>
public sealed class RamStickView : FrameworkElement
{
    public static readonly DependencyProperty LedColorsProperty = DependencyProperty.Register(
        nameof(LedColors), typeof(IReadOnlyList<Color>), typeof(RamStickView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<Color>? LedColors { get => (IReadOnlyList<Color>?)GetValue(LedColorsProperty); set => SetValue(LedColorsProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 10)
            return;

        var spreader = new LinearGradientBrush(Color.FromRgb(0xE9, 0xEC, 0xF2), Color.FromRgb(0xB8, 0xBF, 0xCC), 0);
        spreader.Freeze();
        dc.DrawRoundedRectangle(spreader, null, new Rect(0, h * 0.18, w, h * 0.82), 2, 2);

        var colors = LedColors;
        var leds = colors is { Count: > 0 } ? colors.Count : 10;
        var barH = h * 0.18;
        var cell = barH / 1.0;
        var segment = h * 0.16 / 1.0;
        _ = cell;
        _ = segment;
        var ledHeight = (h * 0.85) / leds;
        for (var i = 0; i < leds; i++)
        {
            var color = colors is { Count: > 0 } ? colors[leds - 1 - i] : Color.FromArgb(0x40, 0x9A, 0xA6, 0xC0);
            var glow = new SolidColorBrush(Color.FromArgb(0x50, color.R, color.G, color.B));
            var core = new SolidColorBrush(color);
            glow.Freeze();
            core.Freeze();
            var y = h * 0.1 + i * ledHeight;
            dc.DrawRoundedRectangle(glow, null, new Rect(-2, y - 1, w + 4, ledHeight + 2), 3, 3);
            dc.DrawRoundedRectangle(core, null, new Rect(1, y + 1, w - 2, ledHeight - 2), 1.5, 1.5);
        }
    }
}
