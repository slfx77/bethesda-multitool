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

    /// <summary>
    ///     FO3/FNV/FO4: white-silhouette art, tinted to the map color scheme at draw time. Direct DDS
    ///     assets are loaded from the game archive when available; bundled PNGs are the fallback.
    /// </summary>
    EmbeddedTinted,

    /// <summary>
    ///     Oblivion/Skyrim/FO76: pre-styled per-type icons (drawn untinted). Oblivion's individual DDS
    ///     assets can load directly from its game archive; SWF-only Skyrim/FO76 use the offline-rasterized
    ///     bundled PNGs, which also keep asset-less memory dumps functional.
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
    ///     True when this game's SCPT records carry inline Obscript bytecode our decompiler (with the
    ///     game's command table from <c>ScriptFunctionTables</c>) can decompile: Oblivion, FO3, FNV.
    ///     Skyrim+ compile to external Papyrus <c>.pex</c> instead. On the schema-primary parse path
    ///     (Oblivion) this flag keeps <c>ParseScripts()</c> running even though the other typed
    ///     collections are viewer-only.
    /// </summary>
    public bool SupportsObscriptDecompilation { get; init; }

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

    /// <summary>True only for white-silhouette art (FO3/FNV/FO4), which is tinted at draw time.</summary>
    public bool MarkersAreTinted => MarkerArt == MarkerArtStrategy.EmbeddedTinted;

    /// <summary>
    ///     Multiplier on the 2D map's base marker icon size for this game. FO3/FNV's bold silhouettes
    ///     read well at the base size (1.0); games whose icons are taller/finer (Skyrim) are bumped so
    ///     they don't render cramped. Tune here per game; the draw also preserves each icon's aspect ratio.
    /// </summary>
    public float MarkerIconScale { get; init; } = 1.0f;

    /// <summary>
    ///     Smallest screen-space scale applied to markers at overview zoom. The default 1 keeps the
    ///     historical constant-pixel-size behavior; games whose detailed icon tiles overwhelm a
    ///     zoom-to-fit overview can shrink them while retaining full size when zoomed in.
    /// </summary>
    public float MarkerMinScreenScale { get; init; } = 1.0f;

    /// <summary>
    ///     View zoom at which markers reach their full screen-space size. Zero disables zoom-aware
    ///     scaling. Between zero zoom and this value the draw interpolates from
    ///     <see cref="MarkerMinScreenScale" /> to 1.
    /// </summary>
    public float MarkerFullSizeZoom { get; init; }

    // ---- Weather / imagespace record-generation capabilities (consumed by MiscEnvironmentHandler
    // and the atmosphere/tonemap pipeline) ----

    /// <summary>
    ///     True when WTHR uses the Skyrim-and-later <c>wbWeatherTimeOfDay</c> struct layout
    ///     (JNAM/IMSP/DALC era) instead of the fixed 10-category NAM0 color block:
    ///     Skyrim, FO4, FO76, Starfield.
    /// </summary>
    public bool HasModernWeatherLayout { get; init; }

    /// <summary>
    ///     Which of the two incompatible semantic layouts this game's modern 36-byte IMGS HNAM
    ///     uses — <see cref="ImageSpaceModernFamily.Skyrim" /> or <see cref="ImageSpaceModernFamily.Fallout4" />
    ///     (FO4/FO76/Starfield). <c>null</c> for the classic single-DNAM games (Morrowind through FNV),
    ///     which also disables the modern split ENAM/HNAM/CNAM/TNAM parse path entirely.
    /// </summary>
    public ImageSpaceModernFamily? ImageSpaceFamily { get; init; }

    /// <summary>
    ///     Form version at which WTHR time-of-day structs widen from 4 to 8 bands (xEdit
    ///     <c>wbWeatherTimeOfDay</c>'s <c>wbFromVersion(111, …)</c>): 111 for FO4/FO76/Starfield,
    ///     <c>null</c> for games that never widen (Skyrim stays at 4). Shared by the NAM0/PNAM color
    ///     and JNAM cloud-alpha strides so the rule can't drift between them.
    /// </summary>
    public int? WideTimeOfDayBandsFormVersion { get; init; }

    /// <summary>
    ///     Form version at which the FO3/FNV IMGS DNAM gains Skin Dimmer (and so shifts every field
    ///     after +56 by four bytes): 14 for Fallout 3 and Fallout: New Vegas, <c>null</c> for games
    ///     whose IMGS is not the single-DNAM layout. ⚠ Measured from retail data across both
    ///     masters (132 B = v1-9, 148 B = v11-13, 152 B = v14-15) — xEdit's FO3/FNV definition
    ///     declares <c>wbFromVersion(10, …)</c>, which does not match either shipped file.
    ///     Decoding keys on the DNAM length; this threshold only powers a mismatch warning, so a
    ///     wrong value can never corrupt a parse.
    /// </summary>
    public int? ImageSpaceSkinDimmerFormVersion { get; init; }

    /// <summary>
    ///     True when WATR ships its noise/normal layers as NAM2/NAM3/NAM4 zstrings and that layout
    ///     has been verified against retail data: Skyrim, FO4, FO76. Deliberately false for
    ///     Starfield — its WATR layout is unverified; flip when the Starfield WATR RE lands.
    /// </summary>
    public bool HasVerifiedModernWatrLayout { get; init; }

    /// <summary>
    ///     True when WTHR cloud speeds are the legacy unsigned scalar magnitude bytes
    ///     (<c>b / 255</c>, FNV MemDebug <c>Clouds::Update</c> contract): Oblivion, FO3, FNV.
    ///     Skyrim-and-later QNAM/RNAM bytes are signed axes biased around 127 instead.
    /// </summary>
    public bool UsesLegacyCloudSpeedEncoding { get; init; }

    /// <summary>
    ///     True when WTHR ONAM carries four scalar cloud speeds rather than a FormID: FO3 and FNV
    ///     (some transitional FNV records retain the FO3 layout).
    /// </summary>
    public bool HasOnamCloudSpeeds { get; init; }

    /// <summary>
    ///     True when the game resolves engine-default IMGS records (interior 0x160 / exterior 0x161,
    ///     à la <c>GetUsableImageSpace</c>) for cells that author none: Oblivion, FO3, FNV.
    /// </summary>
    public bool UsesEngineImagespaceDefaults { get; init; }

    /// <summary>
    ///     True when runtime HDR is the classic Gamebryo FO3/FNV imagespace stage: WTHR time-band
    ///     IMADs apply after the base IMGS, and SunlightDimmer is gated by
    ///     <c>bHDR &amp;&amp; !bInterior</c> (exteriors only).
    /// </summary>
    public bool UsesClassicHdrImagespace { get; init; }

    /// <summary>
    ///     Multiplier on the ambient ("fill") term in the scene lighting sum
    ///     <c>lit = AmbientLightScale·ambient + saturate(N·L)·sun</c> (objects + terrain). The engine value
    ///     is 1.0: the SLS pixel shader consumes the ambient register at full strength
    ///     (pc_basic_sls_shader_disassembly.txt: <c>mad r1, NdotL, PSLightColor, AmbientColor</c> — no scale
    ///     constant exists in any SLS variant), and Sun::Update stores the NAM0 Ambient band into the sun
    ///     light's m_kAmb UNSCALED (atmosphere_decompiled.txt:1911). The 0.3 previously used here was a
    ///     misread of <c>fRam8323ca10</c>, which is actually the LIGHTNING-FLASH ambient boost fraction —
    ///     Sky::SetColor ADDS <c>0.3·flashIntensity</c> (fadds, clamped to the weather's Lightning Color)
    ///     and the flash decays to zero within seconds (atmosphere_decompiled.txt:842-846, 3468-3480,
    ///     4365-4371) — it never multiplies the band. Nights stay correct at 1.0 because FNV authors the
    ///     night look into the NAM0 bands themselves (bright moonlit Night ambient is vanilla-faithful).
    ///     Bound into <c>uAmbientColor.w</c>; the shader falls back to 1.0 when the slot is unset.
    /// </summary>
    public float AmbientLightScale { get; init; } = 1.0f;
}

