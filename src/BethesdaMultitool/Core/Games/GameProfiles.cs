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

    // NIF stream identities are 20.2.0.7 for the whole FO3→Starfield run; only UserVersion/BsVersion
    // distinguish games. Morrowind is 4.0.0.2; Oblivion ~20.0.0.5 (mixed on disk — expectation only).
    private const uint Nif202 = 0x14020007;

    private static readonly GameProfile UnknownProfile = new()
    {
        Game = BethesdaGame.Unknown,
        DisplayName = "Unknown",
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
                DisplayName = "Morrowind",
                Engine = EngineFamily.Tes3,
                RecordHeaderSize = 16,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                MasterFileHints = ["Morrowind"],
                ExpectedNif = new NifExpectation(0x04000002, 0, 0),
                ArchiveFormat = ArchiveFormat.MorrowindBsa,
                DefaultLandscapeDiffuse = FalloutDiffuse,
                DefaultLandscapeNormal = FalloutNormal
            },
            [BethesdaGame.Oblivion] = new()
            {
                Game = BethesdaGame.Oblivion,
                DisplayName = "Oblivion",
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 20,
                GroupHeaderSize = 20,
                HasRecordVersionTrailer = false,
                MasterFileHints = ["Oblivion"],
                ExpectedNif = new NifExpectation(0x14000005, 11, 0),
                ArchiveFormat = ArchiveFormat.Bsa103,
                DefaultLandscapeDiffuse = OblivionDiffuse,
                DefaultLandscapeNormal = OblivionNormal
            },
            [BethesdaGame.Fallout3] = new()
            {
                Game = BethesdaGame.Fallout3,
                DisplayName = "Fallout 3",
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["Fallout3"],
                ExpectedNif = new NifExpectation(Nif202, 0, 34),
                ArchiveFormat = ArchiveFormat.Bsa104,
                DefaultLandscapeDiffuse = FalloutDiffuse,
                DefaultLandscapeNormal = FalloutNormal
            },
            [BethesdaGame.FalloutNewVegas] = new()
            {
                Game = BethesdaGame.FalloutNewVegas,
                DisplayName = "Fallout: New Vegas",
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["FalloutNV"],
                HasArmorDamageThreshold = true,
                ExpectedNif = new NifExpectation(Nif202, 0, 34),
                ArchiveFormat = ArchiveFormat.Bsa104,
                DefaultLandscapeDiffuse = FalloutDiffuse,
                DefaultLandscapeNormal = FalloutNormal
            },
            [BethesdaGame.Skyrim] = new()
            {
                Game = BethesdaGame.Skyrim,
                DisplayName = "Skyrim",
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["Skyrim"],
                UsesLocalizedStrings = true,
                ExpectedNif = new NifExpectation(Nif202, 12, 83),
                ArchiveFormat = ArchiveFormat.Bsa105,
                DefaultLandscapeDiffuse = SkyrimDiffuse,
                DefaultLandscapeNormal = SkyrimNormal
            },
            [BethesdaGame.Fallout4] = new()
            {
                Game = BethesdaGame.Fallout4,
                DisplayName = "Fallout 4",
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["Fallout4"],
                UsesLocalizedStrings = true,
                ExpectedNif = new NifExpectation(Nif202, 12, 130),
                ArchiveFormat = ArchiveFormat.Ba2,
                DefaultLandscapeDiffuse = CommonwealthDiffuse,
                DefaultLandscapeNormal = CommonwealthNormal
            },
            [BethesdaGame.Fallout76] = new()
            {
                Game = BethesdaGame.Fallout76,
                DisplayName = "Fallout 76",
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["SeventySix", "Fallout76"],
                UsesLocalizedStrings = true,
                ExpectedNif = new NifExpectation(Nif202, 12, 155),
                ArchiveFormat = ArchiveFormat.Ba2,
                DefaultLandscapeDiffuse = CommonwealthDiffuse,
                DefaultLandscapeNormal = CommonwealthNormal
            },
            [BethesdaGame.Starfield] = new()
            {
                Game = BethesdaGame.Starfield,
                DisplayName = "Starfield",
                Engine = EngineFamily.Tes4,
                RecordHeaderSize = 24,
                GroupHeaderSize = 24,
                HasRecordVersionTrailer = true,
                MasterFileHints = ["Starfield"],
                UsesLocalizedStrings = true,
                ExpectedNif = new NifExpectation(Nif202, 12, 170),
                ArchiveFormat = ArchiveFormat.Ba2,
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
