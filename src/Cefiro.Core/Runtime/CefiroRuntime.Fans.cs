namespace Cefiro.Core.Runtime;

// Fase 1: el núcleo solo lee sensores. Los ventiladores siguen en manos de la BIOS.
// La Fase 2 sustituye este fichero por el motor de ventiladores y la seguridad.
public sealed partial class CefiroRuntime
{
    private bool EmergencyActive => false;
    private string? EmergencyReason => null;

    private void RegisterFanRoutes()
    {
    }

    private void OnHardwareOpened()
    {
    }

    private void TickFans(long timestamp)
    {
    }

    private void OnShutdownFans()
    {
    }

    private void OnCatalogRolesChanged()
    {
    }
}
