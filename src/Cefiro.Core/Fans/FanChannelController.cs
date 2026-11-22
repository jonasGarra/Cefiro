using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Fans;

/// <summary>Resultado de un ciclo de control de una salida.</summary>
/// <param name="Target">Lo que pide la curva o el valor fijo (tras histéresis y mínimo).</param>
/// <param name="Command">Lo que se envía ahora (tras suavizado y empujón de arranque).</param>
public readonly record struct FanDecision(float Target, float Command, float? FilteredTemperature, string? Note);

/// <summary>
/// Estado de control de una salida entre ciclos: histéresis, suavizado de subida/bajada,
/// % mínimo y empujón de arranque. No sabe nada de seguridad (eso es el SafetyGuard).
/// </summary>
public sealed class FanChannelController
{
    public const double KickSeconds = 2.0;

    private float? _filteredTemperature;
    private float? _lastCommand;
    private double _kickRemaining;

    public float? LastCommand => _lastCommand;

    /// <summary>Olvida el estado (al volver a la BIOS o al cambiar de modo).</summary>
    public void Reset()
    {
        _filteredTemperature = null;
        _lastCommand = null;
        _kickRemaining = 0;
    }

    /// <param name="settings">Ajustes ya validados.</param>
    /// <param name="temperature">Temperatura de la fuente (solo en modo curva).</param>
    /// <param name="currentOutput">Salida actual del hardware, para no dar saltos al tomar el control.</param>
    /// <param name="dt">Segundos desde el ciclo anterior.</param>
    public FanDecision Compute(FanChannelSettingsDto settings, float? temperature, float? currentOutput, double dt)
    {
        dt = Math.Clamp(dt, 0.05, 10);
        string? note = null;

        float raw;
        if (settings.Mode == FanMode.Fixed)
        {
            raw = settings.FixedPercent;
        }
        else
        {
            var t = temperature ?? throw new InvalidOperationException("El modo curva necesita una temperatura.");

            // Histéresis: al subir la temperatura se reacciona enseguida; al bajar, solo cuando ha
            // bajado al menos "HysteresisCelsius" respecto a la última temperatura usada.
            if (_filteredTemperature is not { } filtered || t > filtered || filtered - t >= settings.HysteresisCelsius)
                _filteredTemperature = t;

            raw = FanCurve.Evaluate(settings.Curve, _filteredTemperature!.Value);
        }

        // % mínimo: por debajo, o se para (si está permitido) o se queda en el mínimo.
        var target = raw;
        if (target < settings.MinimumPercent)
        {
            if (settings.AllowStop && target < settings.MinimumPercent)
            {
                target = 0;
                note = "Parado (0 RPM)";
            }
            else
            {
                target = settings.MinimumPercent;
            }
        }

        var previous = _lastCommand ?? currentOutput ?? target;
        float command;

        if (target <= 0)
        {
            command = 0;
            _kickRemaining = 0;
        }
        else if (previous <= 0.5f && target > 0)
        {
            // Empujón de arranque: un ventilador parado necesita más señal para empezar a girar.
            _kickRemaining = KickSeconds;
            command = Math.Max(target, settings.StartPercent);
            note = "Arrancando";
        }
        else if (_kickRemaining > 0)
        {
            _kickRemaining -= dt;
            command = Math.Max(target, settings.StartPercent);
            note = "Arrancando";
        }
        else
        {
            // Suavizado: limita cuánto puede cambiar la salida por segundo.
            var maxUp = (float)(settings.RampUpPercentPerSecond * dt);
            var maxDown = (float)(settings.RampDownPercentPerSecond * dt);
            command = target > previous
                ? Math.Min(target, previous + maxUp)
                : Math.Max(target, previous - maxDown);

            // Si la rampa de bajada acabaría por debajo del mínimo pero el objetivo no es 0, no se baja de él.
            if (command < settings.MinimumPercent && target > 0)
                command = Math.Max(command, Math.Min(target, settings.MinimumPercent));
        }

        command = MathF.Round(Math.Clamp(command, 0, 100), 1);
        _lastCommand = command;
        return new FanDecision(target, command, _filteredTemperature, note);
    }
}
