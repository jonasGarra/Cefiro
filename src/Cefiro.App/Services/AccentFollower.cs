using System.Windows.Media;
using Cefiro.App.Infrastructure;
using Cefiro.App.ViewModels;
using Cefiro.Core.Lighting;

namespace Cefiro.App.Services;

/// <summary>
/// Opción "el acento sigue a las luces del PC": cada poco más de un segundo toma el color de los LEDs
/// (la media si es un color claro, o el LED más vivo si la media sale grisácea, como en un arcoíris)
/// y lo convierte en un acento de neón. Solo cambia el tema si el color cambia de verdad.
/// </summary>
public sealed class AccentFollower
{
    private readonly CefiroSession _session;
    private readonly UiSettingsStore _settings;
    private DateTime _last;
    private Color? _current;

    public AccentFollower(CefiroSession session, UiSettingsStore settings)
    {
        _session = session;
        _settings = settings;
        session.LightingFrameUpdated += OnFrame;
        settings.Changed += s =>
        {
            if (!s.AccentFollowsLights && _current is not null)
            {
                _current = null;
                ApplyStored(s);
            }
        };
    }

    /// <summary>Vuelve al acento elegido en Ajustes.</summary>
    public static void ApplyStored(UiSettings s) => ThemeService.ApplyAccent(
        ThemeService.Parse(s.AccentHex, Color.FromRgb(0x00, 0xE5, 0xFF)),
        ThemeService.Parse(s.Accent2Hex, Color.FromRgb(0x7C, 0x4D, 0xFF)));

    private void OnFrame()
    {
        if (!_settings.Current.AccentFollowsLights || DateTime.UtcNow - _last < TimeSpan.FromSeconds(1.2) || _session.LightingFrame is not { } frame)
            return;
        _last = DateTime.UtcNow;

        double r = 0, g = 0, b = 0;
        var count = 0;
        Color? vivid = null;
        var vividScore = 0.3;
        foreach (var device in frame.Devices)
        {
            foreach (var c in SchematicViewModel.Decode(device.Colors))
            {
                if (c.R + c.G + c.B < 40)
                    continue; // LED apagado

                r += c.R;
                g += c.G;
                b += c.B;
                count++;

                var (_, s, v) = new Rgb(c.R, c.G, c.B).ToHsv();
                if (s * v > vividScore)
                {
                    vividScore = s * v;
                    vivid = c;
                }
            }
        }

        if (count == 0)
            return; // luces apagadas: se deja el acento como está

        var average = new Rgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
        var (hue, saturation, _) = average.ToHsv();
        Color source;
        if (saturation >= 0.35)
            source = Color.FromRgb(average.R, average.G, average.B);
        else if (vivid is { } v2)
            source = v2;
        else
            return; // blanco o gris: no sirve de acento

        (hue, saturation, _) = new Rgb(source.R, source.G, source.B).ToHsv();
        var neon = Rgb.FromHsv(hue, Math.Max(0.6, saturation), 1);
        var accent = Color.FromRgb(neon.R, neon.G, neon.B);

        if (_current is { } now && Math.Abs(now.R - accent.R) + Math.Abs(now.G - accent.G) + Math.Abs(now.B - accent.B) < 36)
            return;

        _current = accent;
        ThemeService.ApplyAccent(accent);
    }
}
