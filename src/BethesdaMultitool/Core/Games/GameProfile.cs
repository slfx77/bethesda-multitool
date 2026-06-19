namespace BethesdaMultitool.Core.Games;

/// <summary>Record-stream family. TES3 (Morrowind) is a flat stream; everything since is TES4-style.</summary>
public enum EngineFamily
{
    /// <summary>Morrowind: flat record stream, 4-byte subrecord sizes, no GRUPs, no FormIDs.</summary>
    Tes3,

    /// <summary>Oblivion and later: GRUP-nested records, 2-byte subrecord sizes, FormIDs.</summary>
    Tes4
}

/// <summary>Archive container a game ships its loose assets in. Representative — parsers self-detect per file.</summary>
public enum ArchiveFormat
{
    None,

    /// <summary>Morrowind <c>.bsa</c> (version field <c>0x100</c>).</summary>
    MorrowindBsa,

    /// <summary>Oblivion <c>BSA\0</c> version 103.</summary>
    Bsa103,

    /// <summary>FO3 / FNV / Skyrim LE <c>BSA\0</c> version 104.</summary>
    Bsa104,

    /// <summary>Skyrim SE <c>BSA\0</c> version 105 (64-bit folder offsets).</summary>
    Bsa105,

    /// <summary>Fallout 4 / 76 / Starfield <c>BTDX</c> archive (BA2).</summary>
    Ba2
}

/// <summary>
///     Expected NIF stream identity for a game. These are DEFAULTS, not authority — Oblivion in
///     particular ships mixed NIF versions on disk, so a NIF parse always self-detects from the file
///     header. The profile value is the canonical expectation for export targeting / validation /
///     when no file is in hand.
/// </summary>
public sealed record NifExpectation(uint Version, uint UserVersion, int BsVersion);

/// <summary>
///     The single source of truth for everything that varies by game. One immutable instance per
///     <see cref="BethesdaGame" /> lives in <see cref="GameProfiles" />; adding a new game is adding
///     one entry there. Resolve a profile with <see cref="GameProfiles.For" /> or detect one from a
///     file with <see cref="GameDetector" />.
/// </summary>
public sealed record GameProfile
{
    public required BethesdaGame Game { get; init; }

    /// <summary>Human-readable name for logs and UI.</summary>
    public required string DisplayName { get; init; }

    public required EngineFamily Engine { get; init; }

    /// <summary>True for Morrowind, which routes to the dedicated TES3 parser family.</summary>
    public bool IsTes3 => Engine == EngineFamily.Tes3;

    // ---- Plugin record/group framing (consumed by EsmParser via PluginFormat) ----

    public required int RecordHeaderSize { get; init; }
    public required int GroupHeaderSize { get; init; }
    public required bool HasRecordVersionTrailer { get; init; }

    // ---- Detection hints ----

    /// <summary>
    ///     Substrings matched (case-insensitively) against a plugin's master list + filename to
    ///     disambiguate the 24-byte TES4 family, whose HEDR version floats overlap (e.g. Skyrim 0.94 ≈
    ///     FO3 0.94). Newest/most-specific games are tried first in <see cref="GameProfiles.ResolveByNames" />.
    /// </summary>
    public IReadOnlyList<string> MasterFileHints { get; init; } = [];

    /// <summary>True for games that store FULL/DESC/dialogue text in external .STRINGS tables (TES4 0x80 flag).</summary>
    public bool UsesLocalizedStrings { get; init; }

    // ---- Parsing capability flags (replace ad-hoc per-game branches) ----

    /// <summary>
    ///     True when ARMO/ARMA DNAM carries a Damage Threshold field after Damage Resistance (the
    ///     FNV 12-byte extension of FO3's 8-byte block). See <c>ItemRecordHandler.ParseArmorDefenseData</c>.
    /// </summary>
    public bool HasArmorDamageThreshold { get; init; }

    // ---- Asset expectations (DEFAULT/representative — parsers still self-detect per file) ----

    public NifExpectation? ExpectedNif { get; init; }
    public ArchiveFormat ArchiveFormat { get; init; }

    // ---- Rendering defaults ----

    /// <summary>
    ///     Engine-default landscape diffuse texture (the <c>SDefaultLandDiffuseTexture</c> ini value),
    ///     bound for a quadrant whose LAND has no BTXT. Game-keyed: binding another game's path loads
    ///     nothing and the base renders white.
    /// </summary>
    public string DefaultLandscapeDiffuse { get; init; } = string.Empty;

    /// <inheritdoc cref="DefaultLandscapeDiffuse" />
    public string DefaultLandscapeNormal { get; init; } = string.Empty;
}
