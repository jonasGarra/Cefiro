namespace Cefiro.Ipc.Contracts;

/// <summary>Un perfil: ventiladores y luces guardados juntos.</summary>
public sealed record ProfileDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Icono del perfil (nombre de símbolo de la interfaz).</summary>
    public string Glyph { get; init; } = "Layer24";

    /// <summary>
    /// Salidas y grupos de ventiladores. La seguridad (temperaturas críticas) es común a todos los
    /// perfiles: al activar uno se conserva la actual.
    /// </summary>
    public FanSettingsDto? Fans { get; init; }

    /// <summary>
    /// Efectos de iluminación. El número de LEDs de cada zona y los favoritos son del PC: al activar
    /// un perfil se conservan los actuales.
    /// </summary>
    public LightingSettingsDto? Lighting { get; init; }

    /// <summary>Atajo de teclado global, p. ej. "Ctrl+Alt+1" (lo registra la interfaz).</summary>
    public string? Hotkey { get; init; }

    /// <summary>Programas (p. ej. "juego.exe") que activan este perfil mientras están abiertos.</summary>
    public IReadOnlyList<string> Programs { get; init; } = [];
}

/// <param name="ActiveProfileId">Último perfil activado (null si ninguno).</param>
/// <param name="ActivatedBy">Programa que lo activó automáticamente (null si fue a mano).</param>
public sealed record ProfilesStateDto(
    IReadOnlyList<ProfileDto> Profiles,
    string? ActiveProfileId,
    string? ActivatedBy,
    bool AutoSwitchEnabled);

/// <param name="CaptureCurrent">Si es true, el perfil guarda la configuración actual de ventiladores y luces.</param>
public sealed record SaveProfileRequest(ProfileDto Profile, bool CaptureCurrent);

public sealed record ProfileIdRequest(string Id);

/// <summary>Ajustes propios del servicio.</summary>
public sealed record ServiceSettingsDto
{
    /// <summary>Cambiar de perfil al abrir los programas asociados.</summary>
    public bool AutoSwitchProfiles { get; init; } = true;
}
