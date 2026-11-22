namespace Cefiro.Core.Runtime;

// Piezas de fases posteriores: iluminación (Fase 3) y perfiles (Fase 5).
public sealed partial class CefiroRuntime
{
    private bool LightingConnected => false;
    private string? ActiveProfileId => null;
}
