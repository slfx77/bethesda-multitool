namespace BethesdaMultitool.Core.Games;

/// <summary>
///     The registry of <see cref="GameProfile" />s — one per <see cref="BethesdaGame" /> and the
///     single place game-specific data lives. Supporting a new game is adding one entry here.
///     Pure data + resolution helpers; depends on nothing in the format layer (so the format layer
///     can depend on it without a cycle).
/// </summary>
public static class GameProfiles
{
    /// <summary>
    ///     The app-wide fallback game when detection fails or a caller has no game context
    ///     (the tool was originally FNV-only). Use this instead of a literal
    ///     <see cref="BethesdaGame.FalloutNewVegas" /> so the convention stays greppable.
    /// </summary>
    public const BethesdaGame DefaultGame = BethesdaGame.FalloutNewVegas;

    // Engine-default landscape textures (the SDefaultLandDiffuseTexture ini value). FO3/FNV share the
    // FNV path, which is also the fallback for games without a verified default (Starfield).
    private const string FalloutDiffuse = @"textures\landscape\DirtWasteland01.dds";
    private const string FalloutNormal = @"textures\landscape\DirtWasteland01_N.dds";
    private const string CommonwealthDiffuse = @"textures\landscape\ground\CommonwealthDefault01_d.dds";
    private const string CommonwealthNormal = @"textures\landscape\ground\CommonwealthDefault01_N.dds";
    private const string SkyrimDiffuse = @"textures\landscape\Dirt01.dds";
    private const string SkyrimNormal = @"textures\landscape\Dirt01_n.dds";
    private const string OblivionDiffuse = @"textures\landscape\TerrainHDDirt01.dds";

    private const string OblivionNormal = @"textures\landscape\TerrainHDDirt01_n.dds";

    // Morrowind hardcodes its default (no ini setting): "_land_default.tga" is embedded in
    // Morrowind.exe at 0x3A7750 beside the LandTexture error strings ("Land (%i, %i) unable to load
    // texture idx %i"), used for VTEX index 0 / unresolvable texture indices. The BSA ships the asset
    // as textures\_land_default.dds (the engine's .tga references resolve to .dds — standard
    // Morrowind behavior the texture loaders already handle). No normal: the 2002 fixed-function
    // renderer predates normal mapping, so terrain has none.
    private const string MorrowindDiffuse = @"textures\_land_default.dds";

    /// <summary>
    ///     The Bethesda-standard exterior cell edge. Mirrors <c>WorldGridConstants.CellSize</c>, which
    ///     this file deliberately does not reference — GameProfiles is pure data and depends on nothing
    ///     outside itself (see the type doc).
    /// </summary>
    private const float StandardExteriorCellWorldSize = 4096f;

    /// <summary>
    ///     The viewer's Gamebryo/Creation camera convention, 70 units per meter: the ONE place that
    ///     number lives. <see cref="GameProfile.ViewerUnitsPerMeter" /> defaults to it and
    ///     <see cref="HumanScaleFactor" /> divides by it, so every human-scale camera constant (a
    ///     112-unit eye is a 1.6 m human) stays a bit-exact no-op. It is a convention, not the measured
    ///     unit: the executables define 128 units = 6 feet (RE-1, docs/world_scale_units_re1.md), which
    ///     <see cref="GameProfile.Units" /> carries per game with provenance. The older doc figure
    ///     "1 unit = 1.42875 cm" is 0.9144 / 64, the same chain.
    /// </summary>
    private const float ClassicWorldUnitsPerMeter = 70f;

    /// <summary>
    ///     Game units per meter read from the FNV and Skyrim executables (RE-1): the engine computes
    ///     <c>bhkConvert::fHk2BSScaleSC = (128 / 6) x 3.2808399 / fHkScaleSC</c>, i.e. 128 units to
    ///     6 feet, which is 69.99125 units per meter (0.0142875 m per unit, exactly 1.8288 m / 128).
    /// </summary>
    private const double FnvSkyrimUnitsPerMeter = 69.99125;

    /// <summary>
    ///     Game units per meter read from the FO3 and Oblivion executables (RE-1): the same chain with a
    ///     four-digit feet-per-meter literal, <c>(128 / 6) x 3.2808 / 10 = 6.99904</c> units per Havok
    ///     unit at 10 Havok units per meter, i.e. 69.9904 units per meter (0.0012% above FNV's).
    /// </summary>
    private const double Fo3OblivionUnitsPerMeter = 69.9904;

    // The unit values are declared BEFORE UnknownProfile and Registry on purpose:
    // C# runs static field initializers in textual order and both of those initializers read them.
    // Moving either field below the registry would hand every profile a null Units and the first
    // Units read would throw.

    /// <summary>
    ///     The Gamebryo convention as an assumption, for the profiles no executable read covers:
    ///     Morrowind (no Havok; the community's 1/70 convention) and the neutral Unknown profile.
    /// </summary>
    private static readonly WorldUnitScale GamebryoConventionUnits = new(
        1.0 / ClassicWorldUnitsPerMeter,
        UnitProvenance.Assumed,
        "Gamebryo 70 units per meter, the convention the viewer's camera constants were authored against; " +
        "not read from this game's executable. The Havok-era games measure 69.99125 or 69.9904 " +
        "(docs/world_scale_units_re1.md); Morrowind has no Havok and no read yet (RE-1)");

    /// <summary>
    ///     Fallout: New Vegas, read from code (RE-1 §3-4): <c>bhkConvert::fHk2BSScaleSC</c> is
    ///     initialized as 69.99125 / <c>fHkScaleSC</c> (10.0) and the world gravity as -9.81 x 10, so
    ///     one unit is 1 / 69.99125 m. Identical on the PC 1.4.0.525 executable, its runtime image and
    ///     the X360 MemDebug build (PDB-named statics).
    /// </summary>
    private static readonly WorldUnitScale FalloutNewVegasUnits = new(
        1.0 / FnvSkyrimUnitsPerMeter,
        UnitProvenance.ReverseEngineered,
        "Read from FalloutNV.exe 1.4.0.525 (sha256 3a87f92f011e5dc9...) and its runtime image (e46b43cdaa32d9b7...): " +
        "initializer 0x00F35270 stores fHk2BSScaleSC = 69.99125671 (double at 0x010120E8) / fHkScaleSC 10.0 " +
        "(0x01012050) = 6.9991255 units per Havok unit, fGravitySC = -9.81 x 10 (initializer 0x00F352B0); " +
        "X360 MemDebug (ff2188b9dc168d49...) fHk2BSScaleSC 6.999125 (0x40DFF8D5 at 0x83220678), fHkScaleSC 10.0 " +
        "(0x820D01E0), gravity (0, 0, -98.1) in TESObjectCELL::CreateWorld 0x823943E0. 128 units = 6 feet " +
        "(docs/world_scale_units_re1.md)");

    /// <summary>
    ///     Fallout 3, read from data with code references (RE-1 §6): the folded literals 6.99904 units
    ///     per Havok unit and its reciprocal 0.1428767, gravity (0, 0, -98.1) = -9.81 x 10; the Havok
    ///     units-per-meter of 10 is inferred from that product.
    /// </summary>
    private static readonly WorldUnitScale Fallout3Units = new(
        1.0 / Fo3OblivionUnitsPerMeter,
        UnitProvenance.ReverseEngineered,
        "Read from Fallout3.exe 1.7.0.4 (sha256 c3f97c2255fa041a...): fHk2BSScaleSC 6.99904 (0x40DFF823 at " +
        "0x00F5174C, 169 code references), fBS2HkScaleSC 0.1428767 (0x3E124E47 at 0x00F410F0, 232 references), " +
        "gravity vec4 (0, 0, -98.1, 0) at 0x00F75740 loaded by CreateWorld 0x00742703; Havok units per meter 10 " +
        "inferred from -98.1 = -9.81 x 10. 128 units = 6 feet with a four-digit 3.2808 ft/m " +
        "(docs/world_scale_units_re1.md)");

    /// <summary>
    ///     Oblivion, read from data with code references (RE-1 §5): the same 6.99904 / 0.1428767 pair
    ///     as Fallout 3. Its world gravity is -73.575 = 0.75 x 9.81 x 10 Havok units/s^2, so either
    ///     Oblivion runs at 0.75 g on this chain or, under Earth gravity, at 52.5 units per meter; the
    ///     executable cannot discriminate and the chain (the engine's unit definition) is used, with the
    ///     0.75 g reading recorded. A data oracle (race height, the skeleton capsule) settles it.
    /// </summary>
    private static readonly WorldUnitScale OblivionUnits = new(
        1.0 / Fo3OblivionUnitsPerMeter,
        UnitProvenance.ReverseEngineered,
        "Read from Oblivion.exe 1.2.0416 (sha256 a8f313845c1545e9...): fHk2BSScaleSC 6.99904 (double at 0x00A372E0, " +
        "27 references; HK2NI-shaped 0x0043F3E0), fBS2HkScaleSC 0.1428767 (0x00A39088, 88 references), gravity " +
        "(0, 0, -73.575, 0) from 0x00A46B20 in CreateWorld = 0.75 x 98.1: the chain value is used and the " +
        "0.75 g versus 52.5-units-per-meter ambiguity stays open until a data oracle settles it " +
        "(docs/world_scale_units_re1.md)");

    /// <summary>
    ///     Skyrim LE and SE, read from code (RE-1 §7): LE's initializer computes 128 / 6 x 3.2808399 /
    ///     <c>fHkScaleSC</c> (1.0) = 69.99125 (named through TESV.map) and gravity -9.81 x 1.0; SE carries
    ///     the folded literals 69.99125 and 0.0142875 with code references.
    /// </summary>
    private static readonly WorldUnitScale SkyrimUnits = new(
        1.0 / FnvSkyrimUnitsPerMeter,
        UnitProvenance.ReverseEngineered,
        "Read from TESV.exe 1.1.21.0 (sha256 311e71737b597ddc...) with TESV.map: ??__EfHk2BSScaleSC@bhkConvert " +
        "0x0118FB20 computes 128 / 6 x 3.2808399 / fHkScaleSC 1.0 = 69.99125, fGravitySC = -9.81 x 1.0 " +
        "(0x0118FB70); SkyrimSE.exe 1.7.104.0 (846efccf0c1374d7...) literals 69.99125 (0x428BFB85 at 0x1417F423C, " +
        "118 references) and 0.0142875 (0x3C6A161E at 0x1417FDE5C), gravity (0, 0, -9.81, 0) at 0x141A7D480 " +
        "(docs/world_scale_units_re1.md)");

    /// <summary>
    ///     Fallout 4 and 76 share Skyrim SE's Creation Engine chain by assumption: not read yet
    ///     (Fallout4.exe and Fallout4.pdb are in Sample/DebugSymbols for the RE-1 follow-up).
    /// </summary>
    private static readonly WorldUnitScale CreationAssumedUnits = new(
        1.0 / FnvSkyrimUnitsPerMeter,
        UnitProvenance.Assumed,
        "Same 128-units-per-6-feet chain as Skyrim SE (69.99125 units per meter) by engine lineage; not read " +
        "from this game's executable yet (Fallout4.exe + Fallout4.pdb in Sample/DebugSymbols; RE-1 follow-up)");

    /// <summary>
    ///     Starfield's metric unit with its provenance (design §4.1 row 2). Assumed: measured from
    ///     retail mesh bounds, not read from the executable (RE-1 covered the Havok-era executables
    ///     and did not open Starfield's).
    /// </summary>
    private static readonly WorldUnitScale StarfieldUnits = new(
        1.0,
        UnitProvenance.Assumed,
        "Creation Engine 2 is metric: retail mesh bounds put ChairPlastic01 at 1.02 tall, ChairUtilityB01 " +
        "at 0.98, GenIntRmSmWallMid_DoorA00 at 2.84 and the InvisibleDoor01 marker at 2.41 x 1.60, which " +
        "are meters; not derivable from the 100-unit cell (the cell moved 40.96x where the unit moved " +
        "70x); executable read pending (RE-1)");

    /// <summary>
    ///     The unit a classic (pre-plugin-era) profile carries. It is the viewer's assumption, stated
    ///     as one: the 3D level pane applies its classic-unit camera constants to the format's native
    ///     units unchanged, so <see cref="HumanScaleFactor" /> must stay exactly 1 for these games
    ///     (pinned by the GameProfiles tests). Where the format has its own meters-per-unit it is a
    ///     design §4.1 row and belongs to the per-format registry the design schedules from cut 1c
    ///     (§9 row 14); <paramref name="nativeUnit" /> names that row, its cut and its RE item, or
    ///     says the title is 2D and has none.
    /// </summary>
    private static WorldUnitScale ClassicViewerUnits(string nativeUnit)
    {
        return new WorldUnitScale(
            1.0 / ClassicWorldUnitsPerMeter,
            UnitProvenance.Assumed,
            "Not a measurement of this game: the profile keeps the classic viewer unit so HumanScaleFactor " +
            "stays exactly 1 (the 3D level pane, where this game has one, applies its classic-unit camera " +
            "constants to native units unchanged); the format's own unit, where it has one, belongs to the " +
            "per-format registry the design schedules from cut 1c. " + nativeUnit);
    }

    private static readonly GameProfile UnknownProfile = new()
    {
        Game = BethesdaGame.Unknown,
        Engine = EngineFamily.Tes4,
        RecordHeaderSize = 24,
        GroupHeaderSize = 24,
        HasRecordVersionTrailer = true,
        DefaultLandscapeDiffuse = FalloutDiffuse,
        DefaultLandscapeNormal = FalloutNormal,
        Units = GamebryoConventionUnits
    };

    private static readonly IReadOnlyDictionary<BethesdaGame, GameProfile> Registry =
        new Dictionary<BethesdaGame, GameProfile>
        {
            [BethesdaGame.Morrowind] = new()
            {
                Game = BethesdaGame.Morrowind,
                Engine = EngineFamily.Tes3,
                RecordHeaderSize = 16,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                MasterFileHints = ["Morrowind"],
                DefaultLandscapeDiffuse = MorrowindDiffuse,
                DefaultLandscapeNormal = string.Empty,
                Units = GamebryoConventionUnits
            },
            [BethesdaGame.Oblivion] = new()
            {
                Game = BethesdaGame.Oblivion,
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 20,
                GroupHeaderSize = 20,
                HasRecordVersionTrailer = false,
                MasterFileHints = ["Oblivion"],
                HasMapMarkers = true,
                MarkerArt = MarkerArtStrategy
                    .EmbeddedColored, // parchment-tile icons from menus\map\world (oblivion_marker_NN.png)
                MarkerIconScale = 1.5f, // 32×32 parchment tiles render small at 1.0; match Skyrim's detailed-icon scale
                // At zoom-to-fit the parchment tiles otherwise cover a disproportionate share of
                // Cyrodiil. Grow them back to the existing 1.5× size by the ordinary detail zoom.
                MarkerMinScreenScale = 0.55f,
                MarkerFullSizeZoom = 0.05f,
                // AmbientLightScale: engine default 1.0. The old 0.7 here compensated for the misread
                // FNV "0.3 ambient scale" baseline (since refuted — see GameProfile.AmbientLightScale).
                SupportsObscriptDecompilation = true,
                UsesLegacyCloudSpeedEncoding = true,
                UsesEngineImagespaceDefaults = true,
                DefaultLandscapeDiffuse = OblivionDiffuse,
                DefaultLandscapeNormal = OblivionNormal,
                Units = OblivionUnits
            },
            [BethesdaGame.Fallout3] = new()
            {
                Game = BethesdaGame.Fallout3,
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["Fallout3"],
                HasMapMarkers = true,
                MarkerArt = MarkerArtStrategy.EmbeddedTinted,
                HasWorldspaceDefaultWaterHeight = true,
                SupportsObscriptDecompilation = true,
                UsesLegacyCloudSpeedEncoding = true,
                HasOnamCloudSpeeds = true,
                UsesEngineImagespaceDefaults = true,
                UsesClassicHdrImagespace = true,
                ImageSpaceSkinDimmerFormVersion = 14,
                DefaultLandscapeDiffuse = FalloutDiffuse,
                DefaultLandscapeNormal = FalloutNormal,
                Units = Fallout3Units
            },
            [BethesdaGame.FalloutNewVegas] = new()
            {
                Game = BethesdaGame.FalloutNewVegas,
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["FalloutNV"],
                HasMapMarkers = true,
                MarkerArt = MarkerArtStrategy.EmbeddedTinted,
                HasWorldspaceDefaultWaterHeight = true,
                SupportsObscriptDecompilation = true,
                UsesLegacyCloudSpeedEncoding = true,
                HasOnamCloudSpeeds = true,
                UsesEngineImagespaceDefaults = true,
                UsesClassicHdrImagespace = true,
                ImageSpaceSkinDimmerFormVersion = 14,
                DefaultLandscapeDiffuse = FalloutDiffuse,
                DefaultLandscapeNormal = FalloutNormal,
                Units = FalloutNewVegasUnits
            },
            [BethesdaGame.Skyrim] = new()
            {
                Game = BethesdaGame.Skyrim,
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["Skyrim"],
                HasMapMarkers = true,
                MarkerArt = MarkerArtStrategy.EmbeddedColored, // icons extracted from map.swf (skyrim_marker_NN.png)
                MarkerIconScale = 1.5f, // map.swf icons are taller/finer than FNV's bold silhouettes
                HasWorldspaceDefaultWaterHeight = true,
                HasModernWeatherLayout = true,
                ImageSpaceFamily = ImageSpaceModernFamily.Skyrim,
                HasVerifiedModernWatrLayout = true,
                DefaultLandscapeDiffuse = SkyrimDiffuse,
                DefaultLandscapeNormal = SkyrimNormal,
                Units = SkyrimUnits
            },
            [BethesdaGame.Fallout4] = new()
            {
                Game = BethesdaGame.Fallout4,
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["Fallout4"],
                HasMapMarkers = true,
                MarkerArt = MarkerArtStrategy
                    .EmbeddedTinted, // white silhouettes from MapMarkers.swf (fo4_marker_NN.png)
                HasWorldspaceDefaultWaterHeight = true,
                HasModernWeatherLayout = true,
                ImageSpaceFamily = ImageSpaceModernFamily.Fallout4,
                WideTimeOfDayBandsFormVersion = 111,
                HasVerifiedModernWatrLayout = true,
                DefaultLandscapeDiffuse = CommonwealthDiffuse,
                DefaultLandscapeNormal = CommonwealthNormal,
                Units = CreationAssumedUnits
            },
            [BethesdaGame.Fallout76] = new()
            {
                Game = BethesdaGame.Fallout76,
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["SeventySix", "Fallout76"],
                HasMapMarkers = true,
                // mapmarkerslibrary.swf sprites carry authored blue/black/yellow palettes; preserve them.
                MarkerArt = MarkerArtStrategy.EmbeddedColored,
                HasWorldspaceDefaultWaterHeight = true,
                HasModernWeatherLayout = true,
                ImageSpaceFamily = ImageSpaceModernFamily.Fallout4,
                WideTimeOfDayBandsFormVersion = 111,
                HasVerifiedModernWatrLayout = true,
                // Appalachia.btd ships loose under Data\Terrain, so no archive patterns are needed.
                HasExternalBtdTerrain = true,
                DefaultLandscapeDiffuse = CommonwealthDiffuse,
                DefaultLandscapeNormal = CommonwealthNormal,
                Units = CreationAssumedUnits
            },
            [BethesdaGame.Starfield] = new()
            {
                Game = BethesdaGame.Starfield,
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["Starfield"],
                HasMapMarkers = true, // MarkerArt defaults to GlyphOnly (taxonomy + atlas TBD)
                HasWorldspaceDefaultWaterHeight = true,
                HasModernWeatherLayout = true,
                ImageSpaceFamily = ImageSpaceModernFamily.Fallout4,
                WideTimeOfDayBandsFormVersion = 111,
                // HasVerifiedModernWatrLayout stays false: Starfield's verified CE2 WATR layout has
                // no NAM2/NAM3/NAM4 texture-path set; its typed 152-byte DNAM is parsed separately.
                // Every terrain\<worldspaceEditorId>.btd lives in Starfield - Terrain01..04.ba2 /
                // TerrainPatch.ba2 (753 of them); the DLC and update archives carry more, hence the
                // whole-Data fallback.
                HasExternalBtdTerrain = true,
                TerrainArchiveNamePatterns = ["*Terrain*.ba2"],
                TerrainSearchesAllDataArchives = true,
                ExteriorCellWorldSize = 100f,
                // Metric: 1 m per unit, measured from retail mesh bounds (the evidence text on the
                // value). Do NOT infer this from the 100-unit cell: that is 40.96× where this is 70×.
                Units = StarfieldUnits,
                ViewerUnitsPerMeter = 1f,
                // Starfield ships NO usable engine-default landscape texture for us to point at: its
                // terrain diffuse is reached only through the material database, and the inherited FNV
                // DirtWasteland01 does not exist in any Starfield archive — so every unresolved cell
                // silently fell back to a texture that could never load. Empty is honest: the resolver
                // then binds the white-pixel placeholder instead of chasing a path that cannot resolve.
                DefaultLandscapeDiffuse = string.Empty,
                DefaultLandscapeNormal = string.Empty
            },

            // ---- Classic (pre-plugin-era) games. Engine = None: no ESM/ESP record stream exists, the
            // framing members are sentinels (the Morrowind GroupHeaderSize = 0 precedent), and these
            // files never route through EsmParser. Identity comes from InstallMarkers via
            // ClassicGameLocator, never from plugin bytes. Install layouts verified on the Steam
            // re-releases 2026-08-31. ----

            [BethesdaGame.Arena] = new()
            {
                Game = BethesdaGame.Arena,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // The Steam wrapper nests the DOS game at ARENA\ beside DOSBox; markers identify that
                // inner directory. Saves (STATES.00 …) sit beside the game data in the same directory.
                InstallMarkers = ["GLOBAL.BSA", "TEMPLATE.DAT"],
                ClassicArchiveGlobs = ["GLOBAL.BSA"],
                Units = ClassicViewerUnits(
                    "Native unit: MIF/RMD voxel maps at 128 units per voxel, about 2 m per voxel (weak; " +
                    "design section 4.1 row, cut 2; RE-5).")
            },
            [BethesdaGame.Daggerfall] = new()
            {
                Game = BethesdaGame.Daggerfall,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // Root = DF\DAGGER (holds FALL.EXE + ARENA2). The DFCD tree is a duplicate CD mirror a
                // scanner should treat as the same content, not new data. DAGGER.SND is a number-record
                // XnGine BSA despite its extension; TEXTURE.nnn / SKYnn.DAT are files with internal
                // structure, not archives.
                InstallMarkers = [@"ARENA2\ARCH3D.BSA", @"ARENA2\MAPS.BSA"],
                ClassicLooseRoot = "ARENA2",
                ClassicArchiveGlobs = [@"ARENA2\*.BSA", @"ARENA2\DAGGER.SND"],
                Units = ClassicViewerUnits(
                    "Native unit: about 0.025 m per world unit, ARCH3D points at 1/256 world unit " +
                    "(design section 4.1 row, cut 1c; RE-2).")
            },
            [BethesdaGame.Battlespire] = new()
            {
                Game = BethesdaGame.Battlespire,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // Root = the install dir itself (GAME.EXE + GAMEDATA). 3D.BS6 is a BSA container despite
                // the extension (2,115 mesh records); the DM*.BS6 deathmatch levels are raw chunked files
                // and deliberately not mounted. SPIRE.SND is a number-record BSA of RIFF WAVs.
                InstallMarkers = [@"GAMEDATA\3D.BS6", @"GAMEDATA\BSI.BSA"],
                ClassicLooseRoot = "GAMEDATA",
                ClassicArchiveGlobs = [@"GAMEDATA\*.BSA", @"GAMEDATA\3D.BS6", @"GAMEDATA\SPIRE.SND"],
                Units = ClassicViewerUnits(
                    "Native unit: about 1/64 m per world unit, meshes at 1/256 world unit (weak; " +
                    "design section 4.1 row, cut 1c; RE-4).")
            },
            [BethesdaGame.Redguard] = new()
            {
                Game = BethesdaGame.Redguard,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // Root = ...\Redguard\Redguard (WORLD.INI is the master registry every viewer starts
                // from). Loose-file based: no general-purpose archive to mount — the per-map ROB
                // archives are mesh-pipeline containers, and movies/music live only inside the CUE/BIN
                // CD image beside the root.
                InstallMarkers = ["WORLD.INI", "ENGLISH.RTX"],
                Units = ClassicViewerUnits(
                    "Native unit: 1/80 m per world unit, meshes at 1/256 world unit; .3DC actors are " +
                    "normalized to the int16 range and need a per-actor runtime scale (design section 4.1 " +
                    "row, cut 1c; RE-3).")
            },
            [BethesdaGame.Fallout1] = new()
            {
                Game = BethesdaGame.Fallout1,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // MASTER.DAT + CRITTER.DAT also exist in Fallout 2, so the third marker entry pins the
                // FO1 executable/config (Steam ships FALLOUTW.EXE; classic CDs used FALLOUT.EXE).
                // ClassicGameLocator probes Fallout 2 first, so its FALLOUT2.EXE install can never
                // fall through to this profile.
                InstallMarkers = ["MASTER.DAT", "CRITTER.DAT", "FALLOUTW.EXE|FALLOUT.EXE|fallout.cfg"],
                // Loose DATA\ overrides the DATs (official 1.x patches + Hi-Res patch ship loose).
                ClassicLooseRoot = "DATA",
                ClassicArchiveGlobs = ["CRITTER.DAT", "MASTER.DAT"],
                Units = ClassicViewerUnits(
                    "Isometric 2D: hex tiles and sprites, no 3D world unit; no design section 4.1 row.")
            },
            [BethesdaGame.Fallout2] = new()
            {
                Game = BethesdaGame.Fallout2,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                InstallMarkers = ["master.dat", "critter.dat", "FALLOUT2.EXE|fallout2.cfg"],
                // fallout2.cfg precedence: loose data\ (master_patches/critter_patches) shadows the
                // archives; among archives the Hi-Res f2_res.dat overlays patch*.dat overlays
                // critter.dat overlays master.dat.
                ClassicLooseRoot = "data",
                ClassicArchiveGlobs = ["f2_res.dat", "patch*.dat", "critter.dat", "master.dat"],
                Units = ClassicViewerUnits(
                    "Isometric 2D: hex tiles and sprites, no 3D world unit; no design section 4.1 row.")
            },
            [BethesdaGame.FalloutTactics] = new()
            {
                Game = BethesdaGame.FalloutTactics,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // Root = the install dir (BOS.exe + core\). BOS archives are plain PKZIP; the 1.27
                // patch ships loose core\ overrides that shadow same-path archive entries. game.pck
                // holds the pen-and-paper PDF supplements — real data, but not game content to mount.
                InstallMarkers = [@"core\game.pck", @"core\bos.cfg"],
                ClassicLooseRoot = "core",
                ClassicArchiveGlobs = [@"core\*.bos"],
                Units = ClassicViewerUnits(
                    "Isometric 2D: .til tiles and .spr sprites, no 3D world unit; no design section 4.1 row.")
            },

            // ---- The Elder Scrolls Travels (mobile). The three J2ME titles ship as ONE JAR each: the
            // JAR is the install, so ClassicGameLocator.DetectFromArchive matches these markers
            // against the PKZIP entry names as well as against a directory the JAR was unpacked
            // into. Fixture layouts measured 2026-09-04/05 (docs/handoff_mobile_travels_2026_09_04.md). ----

            [BethesdaGame.Stormhold] = new()
            {
                Game = BethesdaGame.Stormhold,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // Every data file sits loose at the JAR root: 9 big-endian .dat tables, 37 .cus
                // sprites, 16 PNGs. ESGame.class is the MIDlet both Stormhold and Dawnstar share;
                // the loose charin.dat + monsterfilenamesin.dat pair is what Dawnstar (which packs
                // them into datfiles.lmp) never has loose.
                InstallMarkers = ["ESGame.class", "charin.dat", "monsterfilenamesin.dat"],
                Units = ClassicViewerUnits("2D J2ME title: sprites and tables, no 3D world unit; no design section 4.1 row.")
            },
            [BethesdaGame.Dawnstar] = new()
            {
                Game = BethesdaGame.Dawnstar,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // The tables (except the loose npcstrings.dat) live in datfiles.lmp and the PNGs in
                // imgfiles.lmp — text-delimited lumps ("-name-" + BE u32 offset + BE u16 length)
                // that mount as archive layers UNDER the loose files, so a reader asks the mounted
                // install for "charin.dat" and never cares which side served it.
                InstallMarkers = ["ESGame.class", "datfiles.lmp", "imgfiles.lmp"],
                ClassicArchiveGlobs = ["datfiles.lmp", "imgfiles.lmp"],
                Units = ClassicViewerUnits("2D J2ME title: sprites and tables, no 3D world unit; no design section 4.1 row.")
            },
            [BethesdaGame.Shadowkey] = new()
            {
                Game = BethesdaGame.Shadowkey,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // Root = the Symbian application directory itself (...\system\apps\6R51), which holds
                // the ARM E32 executable beside all 21 zones' per-zone files, the global packs
                // (models.huge/idx, global.spr), the six StringTable.<lang> files and 1,533 Simkin
                // scripts. The azra zone is the first town; every zone has a .zon. Loose-file based —
                // the packs are mesh/sprite-pipeline containers, not general-purpose archives.
                InstallMarkers = ["6R51.APP", "azra.zon", "StringTable.eng"],
                Units = ClassicViewerUnits(
                    "Native unit: 0.5 m tiles at 256 mesh units per tile, measured against a door that fills " +
                    "the standard 4-tile doorway (ShadowkeyZoneSceneBuilder.MeshUnitsPerTile; design section " +
                    "4.1 row, cut 2; RE-6).")
            },
            [BethesdaGame.OblivionMobile] = new()
            {
                Game = BethesdaGame.OblivionMobile,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // Everything is loose at the JAR root: level stems l01..l14 across .scr/.cml/.jtm,
                // 13 lang_N.txt string tables, PNG tilesets. eso.ver carries the build ("2.424").
                InstallMarkers = ["eso.ver", "startup.scr", "lang_0.txt"],
                Units = ClassicViewerUnits("2D J2ME title: tile maps and sprites, no 3D world unit; no design section 4.1 row.")
            },
            [BethesdaGame.OblivionPsp] = new()
            {
                Game = BethesdaGame.OblivionPsp,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // Root = an extracted UMD tree. PSP_GAME\PARAM.SFO plus the SYSDIR boot image are the
                // disc conventions every PSP title shares; USRDIR\GR.ARC is this game's single data
                // pack (13.1 MB in the Jun 2006 beta, 39.1 MB by Apr 2007) and is what makes the tree
                // Oblivion's rather than some other PSP title. Beside it USRDIR\DATA holds RenderWare
                // .RWS music and, from 2007, Bink movies.
                // ⚠ The boot image is named per build: five of the seven staged betas ship
                // SYSDIR\EBOOT.BIN, but the 11 January 2007 disc ships an UNENCRYPTED SYSDIR\BOOT.BIN
                // instead (a debug/test build — the PSP loads BOOT.BIN when EBOOT.BIN is absent), so
                // requiring EBOOT.BIN alone silently loses that build.
                InstallMarkers =
                [
                    @"PSP_GAME\PARAM.SFO",
                    @"PSP_GAME\SYSDIR\EBOOT.BIN|PSP_GAME\SYSDIR\BOOT.BIN",
                    @"PSP_GAME\USRDIR\GR.ARC"
                ],
                ClassicLooseRoot = @"PSP_GAME\USRDIR",
                // Mount the pack itself so its entries browse alongside the loose tree, instead
                // of GR.ARC sitting there as one opaque leaf. Globs resolve against the install
                // root, not against ClassicLooseRoot, so the path is spelled in full.
                ClassicArchiveGlobs = [@"PSP_GAME\USRDIR\GR.ARC"],
                Units = ClassicViewerUnits(
                    "Native unit: RenderWare, 1 m per unit by the RenderWare convention (weak, recalled; " +
                    "design section 4.1 row, cut 4; RE-7).")
            },

            // ---- Console spin-off. The install is a PS2 or Xbox disc image. The locator matches
            // these markers against mounted ISO9660/XDVDFS entry names or an extracted directory. ----

            [BethesdaGame.FalloutBrotherhoodOfSteel] = new()
            {
                Game = BethesdaGame.FalloutBrotherhoodOfSteel,
                Engine = EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                // Either console's boot marker must accompany this game's master record table.
                // A boot marker alone would claim unrelated PS2 or Xbox titles.
                InstallMarkers = ["SYSTEM.CNF|default.xbe", @"DATA\ALL.DDF|resx\all.ddf"],
                // The disc root IS the data root (as for Arena): leaving this empty makes an
                // extracted disc directory address files by the same DATA\… or resx\… paths the
                // mounted disc reports.
                ClassicLooseRoot = "",
                Units = ClassicViewerUnits(
                    "Not registered as a mesh source: the Xbox position scale is an undecoded shader constant " +
                    "(BosXboxMesh; design section 4.1: not registered; RE-8).")
            }
        };

    /// <summary>
    ///     Name-match priority for <see cref="ResolveByNames" /> — newest/most-specific first so a
    ///     plugin mastered on a base game wins over an incidental substring. Mirrors the original
    ///     WorldMapOverlayBuilder ordering.
    /// </summary>
    private static readonly BethesdaGame[] NamePriority =
    [
        BethesdaGame.Starfield,
        BethesdaGame.Fallout76,
        BethesdaGame.Skyrim,
        BethesdaGame.Fallout4,
        BethesdaGame.Oblivion,
        BethesdaGame.Fallout3,
        BethesdaGame.FalloutNewVegas
    ];

    /// <summary>Every known game profile (excludes <see cref="BethesdaGame.Unknown" />).</summary>
    public static IReadOnlyCollection<GameProfile> All => (IReadOnlyCollection<GameProfile>)Registry.Values;

    /// <summary>
    ///     The exterior cell edge in world units for <paramref name="game" />, resolving the profile's
    ///     "unset" 0 to the Bethesda-standard 4096.
    ///     <para>
    ///         ⚠ <see cref="GameProfile.ExteriorCellWorldSize" /> is populated ONLY where it differs
    ///         from that standard (today just Starfield's metric 100), so reading the raw property and
    ///         multiplying by it yields ZERO for every other game. Anything scaling a distance by the
    ///         cell size must go through here — reading the property directly is the bug this method
    ///         exists to prevent.
    ///     </para>
    /// </summary>
    public static float CellWorldSizeOrDefault(BethesdaGame game)
    {
        var size = For(game).ExteriorCellWorldSize;
        return size > 0f ? size : StandardExteriorCellWorldSize;
    }

    /// <summary>
    ///     World units per meter for <paramref name="game" />: 70 for the Gamebryo/Creation games and
    ///     the classic profiles (the viewer assumption their <see cref="GameProfile.Units" /> states),
    ///     1 for Starfield. Every profile carries a unit, so "default" now only means that an
    ///     unregistered game resolves through <see cref="For" /> to the Gamebryo value. For the
    ///     provenance behind the number read <see cref="GameProfile.Units" />.
    /// </summary>
    public static float UnitsPerMeterOrDefault(BethesdaGame game)
    {
        return For(game).ViewerUnitsPerMeter;
    }

    /// <summary>
    ///     Multiplier converting a classic-units human-scale constant into <paramref name="game" />'s
    ///     units. Exactly <c>1.0</c> for every game that uses the classic unit (the constant divides by
    ///     itself: the float view of <c>1 / (1 / 70)</c> is exactly <c>70f</c>), so scaling by this is
    ///     a bit-exact no-op outside Starfield, where it is 1/70.
    /// </summary>
    public static float HumanScaleFactor(BethesdaGame game)
    {
        return UnitsPerMeterOrDefault(game) / ClassicWorldUnitsPerMeter;
    }

    /// <summary>The profile for <paramref name="game" />; a neutral 24-byte default for <c>Unknown</c>.</summary>
    public static GameProfile For(BethesdaGame game)
    {
        return Registry.TryGetValue(game, out var profile) ? profile : UnknownProfile;
    }

    /// <summary>
    ///     Best-effort game subtype for a 24-byte TES4 file from its HEDR version float. The ranges
    ///     overlap across games (Skyrim 0.94 ≈ FO3 0.94), so this is a coarse, priority-ordered guess —
    ///     master-name refinement (<see cref="ResolveByNames" />) is preferred when masters are available.
    ///     FO76 is the only unambiguous case (its version is an order of magnitude higher, e.g. 263.0).
    /// </summary>
    public static BethesdaGame ResolveByHedrVersion(float version)
    {
        return version switch
        {
            >= 2.0f => BethesdaGame.Fallout76,
            >= 1.30f => BethesdaGame.FalloutNewVegas,
            >= 0.955f => BethesdaGame.Starfield,
            >= 0.945f => BethesdaGame.Fallout4,
            >= 0.93f => BethesdaGame.Fallout3,
            _ => BethesdaGame.FalloutNewVegas
        };
    }

    /// <summary>
    ///     Disambiguate by matching <paramref name="candidateNames" /> (a plugin's master list plus its
    ///     own filename) against each profile's <see cref="GameProfile.MasterFileHints" />, newest game
    ///     first. Returns <c>null</c> when nothing matches (caller keeps its structural/version guess).
    /// </summary>
    public static BethesdaGame? ResolveByNames(IEnumerable<string?> candidateNames)
    {
        var names = candidateNames.Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (names.Count == 0)
        {
            return null;
        }

        foreach (var game in NamePriority)
        {
            var hints = For(game).MasterFileHints;
            foreach (var name in names)
            {
                foreach (var hint in hints)
                {
                    if (name!.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        return game;
                    }
                }
            }
        }

        return null;
    }
}
