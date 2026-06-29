using BethesdaMultitool.Core.Formats.Esm.Parsing;
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
///     How the 2D world map sources marker icon art for a game. The marker <c>TNAM</c> type value is
///     game-specific (resolved via <c>MapMarkerCatalog</c>); this picks where the icon picture comes from.
/// </summary>
public enum MarkerArtStrategy
{
    /// <summary>No authentic art wired yet — draw the catalog's type-distinct glyph/color dot.</summary>
    GlyphOnly,

    /// <summary>FO3/FNV: bundled white-silhouette PNGs, tinted to the map color scheme at draw time.</summary>
    EmbeddedTinted,

    /// <summary>
    ///     Skyrim/FO4/FO76 (and eventually Oblivion): per-type icons extracted offline from the game's
    ///     map SWF/atlas and embedded as pre-styled PNGs (drawn untinted). Works for asset-less memory
    ///     dumps too, since the art ships with the tool.
    /// </summary>
    EmbeddedColored
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
    ///     True when the worldspace WRLD record carries a DNAM block with a default land/water height
    ///     (introduced in Fallout 3). When false (Oblivion), the engine has no per-worldspace water
    ///     default: an exterior cell only stores an explicit XCLW when it overrides sea level, so a
    ///     water-referencing worldspace (NAM2) with no DNAM renders its ocean at Z 0 by convention.
    ///     Lets the parser key the default-water decision on the detected game instead of inferring it
    ///     from the absence of a subrecord. See <c>WorldspaceRecordHandler.ParseWorldspaceFromAccessor</c>.
    /// </summary>
    public bool HasWorldspaceDefaultWaterHeight { get; init; }

    /// <summary>
    ///     Engine-default landscape diffuse texture (the <c>SDefaultLandDiffuseTexture</c> ini value),
    ///     bound for a quadrant whose LAND has no BTXT. Game-keyed: binding another game's path loads
    ///     nothing and the base renders white.
    /// </summary>
    public string DefaultLandscapeDiffuse { get; init; } = string.Empty;

    /// <inheritdoc cref="DefaultLandscapeDiffuse" />
    public string DefaultLandscapeNormal { get; init; } = string.Empty;

    // ---- Map markers (consumed by the 2D world map; see MapMarkerCatalog + IMapMarkerIconSet) ----

    /// <summary>
    ///     True when this game places world-map markers (every TES4 game does; Morrowind has none).
    ///     Marker type values are decoded per game by <c>MapMarkerCatalog</c>.
    /// </summary>
    public bool HasMapMarkers { get; init; }

    /// <summary>Where the 2D map sources this game's marker icon art. Defaults to glyph-only.</summary>
    public MarkerArtStrategy MarkerArt { get; init; } = MarkerArtStrategy.GlyphOnly;

    /// <summary>True only for the embedded white-silhouette set (FO3/FNV), which is tinted at draw time.</summary>
    public bool MarkersAreTinted => MarkerArt == MarkerArtStrategy.EmbeddedTinted;

    /// <summary>
    ///     Multiplier on the 2D map's base marker icon size for this game. FO3/FNV's bold silhouettes
    ///     read well at the base size (1.0); games whose icons are taller/finer (Skyrim) are bumped so
    ///     they don't render cramped. Tune here per game; the draw also preserves each icon's aspect ratio.
    /// </summary>
    public float MarkerIconScale { get; init; } = 1.0f;
}

