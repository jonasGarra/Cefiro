using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Cefiro.App.Controls;

/// <summary>
/// Fondo con profundidad: degradado oscuro, tres manchas de color muy desenfocadas que se mueven
/// despacio (siguen el color de acento) y una rejilla de puntos casi invisible. Las animaciones van a
/// 12 fotogramas por segundo: suficiente para algo tan lento y casi sin coste.
/// </summary>
public sealed class AnimatedBackground : Grid
{
    public AnimatedBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "CefiroBackdropBrush");

        var canvas = new Canvas();
        Children.Add(canvas);

        AddBlob(canvas, "CefiroAccentBrush", 520, 0.20, -120, -160, 140, 90, 38);
        AddBlob(canvas, "CefiroAccent2Brush", 600, 0.18, 640, 280, -160, -120, 46);
        AddBlob(canvas, "CefiroHotBrush", 380, 0.08, 980, -120, -120, 160, 52);

        // Rejilla de puntos: textura "técnica" muy sutil.
        var dots = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 26, 26),
            ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
                null,
                new EllipseGeometry(new Point(1, 1), 0.9, 0.9)),
        };
        dots.Freeze();
        Children.Add(new Border { Background = dots });

        // Viñeta para que los bordes queden más oscuros y el centro respire.
        var vignette = new RadialGradientBrush(Color.FromArgb(0x00, 0, 0, 0), Color.FromArgb(0x80, 0x02, 0x03, 0x08))
        {
            RadiusX = 0.85,
            RadiusY = 0.85,
        };
        vignette.Freeze();
        Children.Add(new Border { Background = vignette });
    }

    private static void AddBlob(Canvas canvas, string brushKey, double size, double opacity, double x, double y, double dx, double dy, double seconds)
    {
        var blob = new Ellipse
        {
            Width = size,
            Height = size,
            Opacity = opacity,
            Effect = new BlurEffect { Radius = 140, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        };
        blob.SetResourceReference(Shape.FillProperty, brushKey);
        Canvas.SetLeft(blob, x);
        Canvas.SetTop(blob, y);

        var move = new TranslateTransform();
        blob.RenderTransform = move;
        canvas.Children.Add(blob);

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var ax = new DoubleAnimation(0, dx, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        var ay = new DoubleAnimation(0, dy, TimeSpan.FromSeconds(seconds * 0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        Timeline.SetDesiredFrameRate(ax, 12);
        Timeline.SetDesiredFrameRate(ay, 12);
        move.BeginAnimation(TranslateTransform.XProperty, ax);
        move.BeginAnimation(TranslateTransform.YProperty, ay);
    }
}
