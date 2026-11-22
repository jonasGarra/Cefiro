using Cefiro.Ipc.Contracts;

namespace Cefiro.App.Infrastructure;

/// <summary>Nombres en español de lo que se enseña en varias páginas.</summary>
public static class Names
{
    public static string Effect(EffectKind kind) => kind switch
    {
        EffectKind.Off => "Apagado",
        EffectKind.Static => "Estático",
        EffectKind.Breathing => "Respiración",
        EffectKind.Rainbow => "Arcoíris",
        EffectKind.Wave => "Ola",
        EffectKind.ColorCycle => "Ciclo de color",
        EffectKind.Comet => "Cometa",
        EffectKind.Temperature => "Temperatura",
        EffectKind.Music => "Música",
        EffectKind.DeviceMode => "Modo del dispositivo",
        _ => kind.ToString(),
    };

    public static string Preset(FanPreset preset) => preset switch
    {
        FanPreset.Silent => "Silencioso",
        FanPreset.Balanced => "Equilibrado",
        FanPreset.Performance => "Rendimiento",
        FanPreset.Maximum => "Máximo",
        _ => preset.ToString(),
    };
}
