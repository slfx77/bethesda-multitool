namespace BethesdaMultitool.Core.Formats.Nif.Conversion;

/// <summary>
///     Context for evaluating NIF version expressions. Per-game NIF stream identities (the expected
///     Version / UserVersion / BsVersion) are the single source of truth on
///     <c>GameProfile.ExpectedNif</c> (Core.Games) — construct a context from a profile when targeting
///     a specific game's NIF output. A NIF parse always self-detects the real version from the file
///     header (Oblivion in particular ships mixed versions on disk).
/// </summary>
public sealed record NifVersionContext
{
    /// <summary>NIF file version (e.g., 0x14020007 for 20.2.0.7)</summary>
    public uint Version { get; init; }

    /// <summary>User version (game-specific)</summary>
    public uint UserVersion { get; init; }

    /// <summary>Bethesda stream version (e.g., 34 for FO3/NV)</summary>
    public int BsVersion { get; init; }
}
