namespace BethesdaMultitool.Core.Games;

/// <summary>
///     The registry of <see cref="GameProfile" />s — one per <see cref="BethesdaGame" /> and the
///     single place game-specific data lives. Supporting a new game is adding one entry here.
///     Pure data + resolution helpers; depends on nothing in the format layer (so the format layer
///     can depend on it without a cycle).
/// </summary>
public static class GameProfiles
{
    // Engine-default landscape textures (the SDefaultLandDiffuseTexture ini value). FO3/FNV share the
    // FNV path, which is also the fallback for games without a verified default (Morrowind/Starfield).
    private const string FalloutDiffuse = @"textures\landscape\DirtWasteland01.dds";
    private const string FalloutNormal = @"textures\landscape\DirtWasteland01_N.dds";
    private const string CommonwealthDiffuse = @"textures\landscape\ground\CommonwealthDefault01_d.dds";
    private const string CommonwealthNormal = @"textures\landscape\ground\CommonwealthDefault01_N.dds";
    private const string SkyrimDiffuse = @"textures\landscape\Dirt01.dds";
    private const string SkyrimNormal = @"textures\landscape\Dirt01_n.dds";
    private const string OblivionDiffuse = @"textures\landscape\TerrainHDDirt01.dds";
    private const string OblivionNormal = @"textures\landscape\TerrainHDDirt01_n.dds";

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
                DefaultLandscapeDiffuse = FalloutDiffuse,
                DefaultLandscapeNormal = FalloutNormal
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
                MarkerArt = MarkerArtStrategy.EmbeddedColored, // parchment-tile icons from menus\map\world (oblivion_marker_NN.png)
                MarkerIconScale = 1.5f, // 32×32 parchment tiles render small at 1.0; match Skyrim's detailed-icon scale
                // AmbientLightScale: engine default 1.0. The old 0.7 here compensated for the misread
                // FNV "0.3 ambient scale" baseline (since refuted — see GameProfile.AmbientLightScale).
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
                MarkerArt = MarkerArtStrategy.EmbeddedTinted, // white silhouettes from MapMarkers.swf (fo4_marker_NN.png)
                HasWorldspaceDefaultWaterHeight = true,
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
                MarkerArt = MarkerArtStrategy.EmbeddedTinted, // white silhouettes from mapmarkerslibrary.swf (fo76_marker_NNN.png)
                HasWorldspaceDefaultWaterHeight = true,
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
                DefaultLandscapeDiffuse = FalloutDiffuse,
                DefaultLandscapeNormal = FalloutNormal
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

    /// <summary>The profile for <paramref name="game" />; a neutral 24-byte default for <c>Unknown</c>.</summary>
    public static GameProfile For(BethesdaGame game) =>
        Registry.TryGetValue(game, out var profile) ? profile : UnknownProfile;

    /// <summary>
    ///     Best-effort game subtype for a 24-byte TES4 file from its HEDR version float. The ranges
    ///     overlap across games (Skyrim 0.94 ≈ FO3 0.94), so this is a coarse, priority-ordered guess —
    ///     master-name refinement (<see cref="ResolveByNames" />) is preferred when masters are available.
    ///     FO76 is the only unambiguous case (its version is an order of magnitude higher, e.g. 263.0).
    /// </summary>
    public static BethesdaGame ResolveByHedrVersion(float version) => version switch
    {
        >= 2.0f => BethesdaGame.Fallout76,
        >= 1.30f => BethesdaGame.FalloutNewVegas,
        >= 0.955f => BethesdaGame.Starfield,
        >= 0.945f => BethesdaGame.Fallout4,
        >= 0.93f => BethesdaGame.Fallout3,
        _ => BethesdaGame.FalloutNewVegas
    };

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
