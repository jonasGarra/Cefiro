using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Cefiro.App.Controls;

/// <summary>
/// Fila de LEDs con brillo (vista previa en directo de una zona). Si <see cref="IsInteractive"/> es true,
/// al pulsar un LED se lanza <see cref="LedClickedEvent"/> con su posición en <see cref="ClickedIndex"/>.
/// </summary>
public sealed class LedStrip : FrameworkElement
{
    public static readonly DependencyProperty ColorsProperty = DependencyProperty.Register(
        nameof(Colors), typeof(IReadOnlyList<Color>), typeof(LedStrip), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(LedStrip), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.Register(
        nameof(IsInteractive), typeof(bool), typeof(LedStrip), new FrameworkPropertyMetadata(false, OnInteractiveChanged));

    public static readonly DependencyProperty LedSizeProperty = DependencyProperty.Register(
        nameof(LedSize), typeof(double), typeof(LedStrip), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly RoutedEvent LedClickedEvent = EventManager.RegisterRoutedEvent(
        "LedClicked", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(LedStrip));

    private const double Gap = 3;
    private int _hover = -1;

    public IReadOnlyList<Color>? Colors { get => (IReadOnlyList<Color>?)GetValue(ColorsProperty); set => SetValue(ColorsProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public bool IsInteractive { get => (bool)GetValue(IsInteractiveProperty); set => SetValue(IsInteractiveProperty, value); }

    /// <summary>Tamaño máximo de cada LED.</summary>
    public double LedSize { get => (double)GetValue(LedSizeProperty); set => SetValue(LedSizeProperty, value); }

    /// <summary>LED pulsado en el último <see cref="LedClickedEvent"/>.</summary>
    public int ClickedIndex { get; private set; } = -1;

    public event RoutedEventHandler LedClicked
    {
        add => AddHandler(LedClickedEvent, value);
        remove => RemoveHandler(LedClickedEvent, value);
    }

    private static void OnInteractiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((LedStrip)d).Cursor = e.NewValue is true ? Cursors.Hand : null;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width, LedSize + 8);

    private double Pitch(int count) => Math.Max(2, Math.Min(LedSize, (ActualWidth - Gap * (count - 1)) / count));

    protected override void OnRender(DrawingContext dc)
    {
        // Fondo transparente para recibir el ratón entre LED y LED.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var colors = Colors;
        if (colors is null || colors.Count == 0 || ActualWidth < 4)
            return;

        var count = colors.Count;
        var size = Pitch(count);
        var y = ActualHeight / 2;

        for (var i = 0; i < count; i++)
        {
            var c = colors[i];
            var x = i * (size + Gap) + size / 2;
            if (x > ActualWidth)
                break;

            var glow = new RadialGradientBrush(Color.FromArgb(0x70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
            glow.Freeze();
            dc.DrawEllipse(glow, null, new Point(x, y), size, size);

            var core = new SolidColorBrush(c.R + c.G + c.B < 30 ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : c);
            core.Freeze();
            dc.DrawEllipse(core, null, new Point(x, y), size / 2.4, size / 2.4);

            if (i == SelectedIndex || (IsInteractive && i == _hover))
            {
                var ring = new Pen(new SolidColorBrush(i == SelectedIndex ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1.5);
                ring.Freeze();
                dc.DrawEllipse(null, ring, new Point(x, y), size / 2 + 2, size / 2 + 2);
            }
        }
    }

    private int IndexAt(Point p)
    {
        var count = Colors?.Count ?? 0;
        if (count == 0)
            return -1;

        var size = Pitch(count);
        var index = (int)Math.Floor((p.X + Gap / 2) / (size + Gap));
        return index >= 0 && index < count ? index : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsInteractive)
            return;

        var index = IndexAt(e.GetPosition(this));
        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover >= 0)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    // Al pulsar (no al soltar): así la fila que lo contiene no se queda con el clic.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsInteractive)
            return;

        var index = IndexAt(e.GetPosition(this));
        if (index < 0)
            return;

        ClickedIndex = index;
        RaiseEvent(new RoutedEventArgs(LedClickedEvent, this));
        e.Handled = true;
    }
}
