using System.Windows;
using System.Windows.Media.Animation;

namespace Cefiro.App.Views;

/// <summary>Pantalla de carga: logotipo girando, halo que late y barra con brillo.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => StartAnimations();
    }

    public void SetStatus(string text) => StatusText.Text = text;

    private void StartAnimations()
    {
        Spin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });

        Pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(1.1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

        ShimmerMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(-130, 330, TimeSpan.FromSeconds(1.4))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>Se desvanece y se cierra.</summary>
    public void FadeOutAndClose()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
