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
    ///     The Gamebryo/Creation world unit: 1 unit = 1.42875 cm, so ~70 units span a metre. Every
    ///     human-scale camera constant in this codebase was authored against it (a 112-unit eye is a
    ///     1.6 m human).
    /// </summary>
    private const float ClassicWorldUnitsPerMetre = 70f;

    private static readonly GameProfile UnknownProfile = new()
    {
        Game = BethesdaGame.Unknown,
        Engine = EngineFamily.Tes4,
        RecordHeaderSize = 24,
        GroupHeaderSize = 24,
        HasRecordVersionTrailer = true,
        DefaultLandscapeDiffuse = FalloutDiffuse,
        DefaultLandscapeNormal = FalloutNormal
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
                DefaultLandscapeNormal = string.Empty
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
                DefaultLandscapeNormal = OblivionNormal
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
                DefaultLandscapeNormal = FalloutNormal
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
                DefaultLandscapeNormal = FalloutNormal
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
                DefaultLandscapeNormal = SkyrimNormal
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
                DefaultLandscapeNormal = CommonwealthNormal
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
                DefaultLandscapeNormal = CommonwealthNormal
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
                // Metric: measured from retail mesh bounds — ChairPlastic01 is 1.02 tall, ChairUtilityB01
                // 0.98, GenIntRmSmWallMid_DoorA00 2.84, the InvisibleDoor01 marker 2.41 × 1.60. Those are
                // metres. Do NOT infer this from the 100-unit cell: that is 40.96× where this is 70×.
                WorldUnitsPerMetre = 1f,
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
                ClassicArchiveGlobs = ["GLOBAL.BSA"]
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
                ClassicArchiveGlobs = [@"ARENA2\*.BSA", @"ARENA2\DAGGER.SND"]
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
                ClassicArchiveGlobs = [@"GAMEDATA\*.BSA", @"GAMEDATA\3D.BS6", @"GAMEDATA\SPIRE.SND"]
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
                InstallMarkers = ["WORLD.INI", "ENGLISH.RTX"]
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
                ClassicArchiveGlobs = ["CRITTER.DAT", "MASTER.DAT"]
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
                ClassicArchiveGlobs = ["f2_res.dat", "patch*.dat", "critter.dat", "master.dat"]
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
                ClassicArchiveGlobs = [@"core\*.bos"]
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

    /// <summary>World units per metre for <paramref name="game" />, resolving "unset" to the classic ~70.</summary>
    public static float UnitsPerMetreOrDefault(BethesdaGame game)
    {
        var units = For(game).WorldUnitsPerMetre;
        return units > 0f ? units : ClassicWorldUnitsPerMetre;
    }

    /// <summary>
    ///     Multiplier converting a classic-units human-scale constant into <paramref name="game" />'s
    ///     units. Exactly <c>1.0</c> for every game that uses the classic unit (the constant divides by
    ///     itself), so scaling by this is a bit-exact no-op outside Starfield, where it is 1/70.
    /// </summary>
    public static float HumanScaleFactor(BethesdaGame game)
    {
        return UnitsPerMetreOrDefault(game) / ClassicWorldUnitsPerMetre;
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
