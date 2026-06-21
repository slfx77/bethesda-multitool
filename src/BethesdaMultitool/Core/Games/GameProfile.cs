namespace BethesdaMultitool.Core.Games;

/// <summary>Record-stream family. TES3 (Morrowind) is a flat stream; everything since is TES4-style.</summary>
public enum EngineFamily
{
    /// <summary>Morrowind: flat record stream, 4-byte subrecord sizes, no GRUPs, no FormIDs.</summary>
    Tes3,

    /// <summary>Oblivion and later: GRUP-nested records, 2-byte subrecord sizes, FormIDs.</summary>
    Tes4
}

/// <summary>
///     The single source of truth for everything that varies by game. One immutable instance per
///     <see cref="BethesdaGame" /> lives in <see cref="GameProfiles" />; adding a new game is adding
///     one entry there. Resolve a profile with <see cref="GameProfiles.For" /> or detect one from a
///     file with <see cref="GameDetector" />.
/// </summary>
public sealed record GameProfile
{
    public required BethesdaGame Game { get; init; }

    public required EngineFamily Engine { get; init; }

    /// <summary>True for Morrowind, which routes to the dedicated TES3 parser family.</summary>
    public bool IsTes3 => Engine == EngineFamily.Tes3;

    // ---- Plugin record/group framing (consumed by EsmParser via PluginFormat) ----

    public required int RecordHeaderSize { get; init; }
    public required int GroupHeaderSize { get; init; }
    public required bool HasRecordVersionTrailer { get; init; }

    /// <summary>
    ///     Substrings matched (case-insensitively) against a plugin's master list + filename to
    ///     disambiguate the 24-byte TES4 family, whose HEDR version floats overlap (e.g. Skyrim 0.94 ≈
    ///     FO3 0.94). Newest/most-specific games are tried first in <see cref="GameProfiles.ResolveByNames" />.
    /// </summary>
    public IReadOnlyList<string> MasterFileHints { get; init; } = [];

    /// <summary>
    ///     True when ARMO/ARMA DNAM carries a Damage Threshold field after Damage Resistance (the
    ///     FNV 12-byte extension of FO3's 8-byte block). See <c>ItemRecordHandler.ParseArmorDefenseData</c>.
    /// </summary>
    public bool HasArmorDamageThreshold { get; init; }

    /// <summary>
    ///     Engine-default landscape diffuse texture (the <c>SDefaultLandDiffuseTexture</c> ini value),
    ///     bound for a quadrant whose LAND has no BTXT. Game-keyed: binding another game's path loads
    ///     nothing and the base renders white.
    /// </summary>
    public string DefaultLandscapeDiffuse { get; init; } = string.Empty;

    /// <inheritdoc cref="DefaultLandscapeDiffuse" />
    public string DefaultLandscapeNormal { get; init; } = string.Empty;
}
