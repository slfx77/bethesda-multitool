using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Games;

/// <summary>
///     Pins the game registry: every game has a profile (so a new <see cref="BethesdaGame" /> value
///     can't be added without a matching entry), and the two pure resolvers behave as the detector
///     relies on. The HEDR-version switch mirrors the historical <c>PluginFormat.DetectTes4Game</c>
///     thresholds — changing it shifts coarse detection for every 24-byte game.
/// </summary>
public class GameProfilesTests
{
    [Theory]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    [InlineData(BethesdaGame.Starfield)]
    public void For_EveryKnownGame_ReturnsMatchingProfile(BethesdaGame game)
    {
        var profile = GameProfiles.For(game);
        Assert.Equal(game, profile.Game);
    }

    [Fact]
    public void Registry_CoversEveryEnumValueExceptUnknown()
    {
        var expected = Enum.GetValues<BethesdaGame>().Where(g => g != BethesdaGame.Unknown).ToHashSet();
        var actual = GameProfiles.All.Select(p => p.Game).ToHashSet();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void For_Unknown_ReturnsNeutral24ByteDefault()
    {
        var profile = GameProfiles.For(BethesdaGame.Unknown);
        Assert.Equal(BethesdaGame.Unknown, profile.Game);
        Assert.Equal(24, profile.RecordHeaderSize);
    }

    [Theory]
    [InlineData(263.0f, BethesdaGame.Fallout76)] // SeventySix.esm — unambiguously high
    [InlineData(1.34f, BethesdaGame.FalloutNewVegas)]
    [InlineData(1.0f, BethesdaGame.Starfield)] // [0.955, 1.30) band
    [InlineData(0.96f, BethesdaGame.Starfield)]
    [InlineData(0.95f, BethesdaGame.Fallout4)] // [0.945, 0.955)
    [InlineData(0.94f, BethesdaGame.Fallout3)] // [0.93, 0.945) — overlaps Skyrim; name refinement disambiguates
    [InlineData(0.5f, BethesdaGame.FalloutNewVegas)] // default
    public void ResolveByHedrVersion_MatchesHistoricalThresholds(float version, BethesdaGame expected)
    {
        Assert.Equal(expected, GameProfiles.ResolveByHedrVersion(version));
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim, "Skyrim.esm")]
    [InlineData(BethesdaGame.FalloutNewVegas, "FalloutNV.esm")]
    [InlineData(BethesdaGame.Fallout76, "SeventySix.esm")]
    [InlineData(BethesdaGame.Starfield, "Starfield.esm")]
    public void ResolveByNames_MatchesGameByHint(BethesdaGame expected, string name)
    {
        Assert.Equal(expected, GameProfiles.ResolveByNames([name]));
    }

    [Fact]
    public void ResolveByNames_PrefersNewerGameWhenMultipleMatch()
    {
        // A plugin mastered on both FO4 and FNV is an FO4 plugin (FO4 outranks FNV in name priority).
        Assert.Equal(BethesdaGame.Fallout4, GameProfiles.ResolveByNames(["FalloutNV.esm", "Fallout4.esm"]));
    }

    [Theory]
    [InlineData("RandomMod.esp")]
    [InlineData("")]
    public void ResolveByNames_NoMatch_ReturnsNull(string name)
    {
        Assert.Null(GameProfiles.ResolveByNames([name]));
    }

    [Fact]
    public void Profiles_PinKeyCapabilities()
    {
        // The worldspace DNAM default-water-height field was added in Fallout 3; Oblivion lacks it
        // (its oceans default to Z 0). Drives the deterministic default-water decision in
        // WorldspaceRecordHandler instead of inferring engine era from a missing subrecord.
        Assert.False(GameProfiles.For(BethesdaGame.Oblivion).HasWorldspaceDefaultWaterHeight);
        Assert.True(GameProfiles.For(BethesdaGame.Fallout3).HasWorldspaceDefaultWaterHeight);
        Assert.True(GameProfiles.For(BethesdaGame.FalloutNewVegas).HasWorldspaceDefaultWaterHeight);
        Assert.True(GameProfiles.For(BethesdaGame.Skyrim).HasWorldspaceDefaultWaterHeight);

        // Only Morrowind is the TES3 family.
        Assert.True(GameProfiles.For(BethesdaGame.Morrowind).IsTes3);
        Assert.False(GameProfiles.For(BethesdaGame.Oblivion).IsTes3);
    }

    [Fact]
    public void Profiles_PinExternalBtdTerrain()
    {
        // Fallout 76 and Starfield keep exterior heights in .btd files rather than a LAND record's
        // VHGT — xEdit's Starfield definitions contain no LAND record at all. Every other game would
        // render flat if the injector ran for it, so the flag must stay exactly these two.
        Assert.True(GameProfiles.For(BethesdaGame.Fallout76).HasExternalBtdTerrain);
        Assert.True(GameProfiles.For(BethesdaGame.Starfield).HasExternalBtdTerrain);
        Assert.False(GameProfiles.For(BethesdaGame.FalloutNewVegas).HasExternalBtdTerrain);
        Assert.False(GameProfiles.For(BethesdaGame.Skyrim).HasExternalBtdTerrain);

        // Fallout 76 ships Appalachia.btd loose, so it needs no archive probe at all; Starfield ships
        // zero loose assets and hides its 753 terrain files across Terrain01..04 + TerrainPatch, with
        // more in the DLC/update archives (hence the whole-Data fallback).
        Assert.Empty(GameProfiles.For(BethesdaGame.Fallout76).TerrainArchiveNamePatterns);
        Assert.False(GameProfiles.For(BethesdaGame.Fallout76).TerrainSearchesAllDataArchives);
        Assert.Equal(["*Terrain*.ba2"], GameProfiles.For(BethesdaGame.Starfield).TerrainArchiveNamePatterns);
        Assert.True(GameProfiles.For(BethesdaGame.Starfield).TerrainSearchesAllDataArchives);
    }

    [Fact]
    public void Profiles_PinExteriorCellWorldSize()
    {
        // Creation Engine 2 went metric: Starfield's exterior cell is 100 world units, not 4096.
        // xEdit scales WRLD NAM0/NAM9 by IsSF1(1/100, 1/4096), and sampled REFR positions satisfy
        // floor(pos / 100) == XCLC. Everything else keeps the engine default (0 => caller's default).
        Assert.Equal(100f, GameProfiles.For(BethesdaGame.Starfield).ExteriorCellWorldSize);
        Assert.Equal(0f, GameProfiles.For(BethesdaGame.FalloutNewVegas).ExteriorCellWorldSize);
        Assert.Equal(0f, GameProfiles.For(BethesdaGame.Fallout4).ExteriorCellWorldSize);
        Assert.Equal(0f, GameProfiles.For(BethesdaGame.Fallout76).ExteriorCellWorldSize);
    }

    /// <summary>
    ///     The world UNIT is a separate axis from the cell size, and conflating them is the specific
    ///     bug this pins: Starfield's cell shrank 40.96× (4096→100) while its unit grew 70× (1.42875 cm
    ///     → 1 m), because a Starfield cell spans 100 m where a Fallout cell spans ~58 m. Scaling a
    ///     human-scale constant by the CELL ratio therefore leaves it 1.5625× too big — a walk-mode eye
    ///     2.7 m off the ground instead of 1.6 m.
    ///     <para>
    ///         Measured from retail mesh bounds: ChairPlastic01 1.02 tall, ChairUtilityB01 0.98,
    ///         GenIntRmSmWallMid_DoorA00 2.84, InvisibleDoor01 2.41 × 1.60. Those are metres.
    ///     </para>
    /// </summary>
    [Fact]
    public void Profiles_PinWorldUnitsPerMetre()
    {
        Assert.Equal(1f, GameProfiles.For(BethesdaGame.Starfield).WorldUnitsPerMetre);
        Assert.Equal(1f, GameProfiles.UnitsPerMetreOrDefault(BethesdaGame.Starfield));

        // Unset everywhere else => the classic Gamebryo/Creation unit.
        Assert.Equal(0f, GameProfiles.For(BethesdaGame.FalloutNewVegas).WorldUnitsPerMetre);
        Assert.Equal(70f, GameProfiles.UnitsPerMetreOrDefault(BethesdaGame.FalloutNewVegas));
    }

    /// <summary>
    ///     The human-scale multiplier must be EXACTLY 1 for every classic-unit game, so applying it to
    ///     the camera constants is a bit-exact no-op outside Starfield rather than a near-miss that
    ///     silently shifts every existing game's walk height.
    /// </summary>
    [Theory]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void HumanScaleFactor_IsExactlyOneForClassicUnitGames(BethesdaGame game)
    {
        Assert.Equal(1f, GameProfiles.HumanScaleFactor(game));
    }

    [Fact]
    public void HumanScaleFactor_ScalesStarfieldToMetres()
    {
        var factor = GameProfiles.HumanScaleFactor(BethesdaGame.Starfield);

        // A 112-unit classic eye height is 1.6 m; in Starfield's units that must read as ~1.6, not 112
        // (taller than the whole 100-unit cell) and not 2.73 (what the cell ratio would have given).
        Assert.Equal(1.6f, 112f * factor, 2);
        Assert.Equal(1.43f, 100f * factor, 2); // walk pace, m/s
    }

    [Fact]
    public void Profiles_PinMapMarkerStrategy()
    {
        // Morrowind has no world-map markers; every TES4 game does.
        Assert.False(GameProfiles.For(BethesdaGame.Morrowind).HasMapMarkers);
        Assert.True(GameProfiles.For(BethesdaGame.Oblivion).HasMapMarkers);

        // FO3/FNV use the bundled white-silhouette set (tinted to the map scheme).
        Assert.Equal(MarkerArtStrategy.EmbeddedTinted, GameProfiles.For(BethesdaGame.Fallout3).MarkerArt);
        Assert.Equal(MarkerArtStrategy.EmbeddedTinted, GameProfiles.For(BethesdaGame.FalloutNewVegas).MarkerArt);
        Assert.True(GameProfiles.For(BethesdaGame.FalloutNewVegas).MarkersAreTinted);

        // Skyrim icons were extracted from map.swf and embedded pre-styled (never tinted), and bumped
        // larger than FNV's bold silhouettes so the taller/finer icons don't render cramped.
        Assert.Equal(MarkerArtStrategy.EmbeddedColored, GameProfiles.For(BethesdaGame.Skyrim).MarkerArt);
        Assert.False(GameProfiles.For(BethesdaGame.Skyrim).MarkersAreTinted);
        Assert.True(GameProfiles.For(BethesdaGame.Skyrim).MarkerIconScale > 1.0f);
        Assert.Equal(1.0f, GameProfiles.For(BethesdaGame.FalloutNewVegas).MarkerIconScale);

        // Oblivion's 32×32 parchment-tile icons render small at the 1.0 default, so they get the same
        // detailed-icon upscale as Skyrim.
        Assert.Equal(MarkerArtStrategy.EmbeddedColored, GameProfiles.For(BethesdaGame.Oblivion).MarkerArt);
        Assert.True(GameProfiles.For(BethesdaGame.Oblivion).MarkerIconScale > 1.0f);

        // Engine ambient scale is 1.0 everywhere: the SLS shader consumes the ambient register at full
        // strength (no scale constant exists in any SLS variant) and Sun::Update stores the NAM0 Ambient
        // band unscaled. The old 0.3 was a misread of the lightning-flash boost fraction — see
        // GameProfile.AmbientLightScale.
        Assert.Equal(1.0f, GameProfiles.For(BethesdaGame.FalloutNewVegas).AmbientLightScale);
        Assert.Equal(1.0f, GameProfiles.For(BethesdaGame.Oblivion).AmbientLightScale);

        // FO4 embeds white silhouettes and tints them to the scheme like FO3/FNV. FO76's extracted
        // sprites already carry their authored palette, so tinting would flatten blue/black/yellow art.
        Assert.Equal(MarkerArtStrategy.EmbeddedTinted, GameProfiles.For(BethesdaGame.Fallout4).MarkerArt);
        Assert.True(GameProfiles.For(BethesdaGame.Fallout4).MarkersAreTinted);
        Assert.Equal(MarkerArtStrategy.EmbeddedColored, GameProfiles.For(BethesdaGame.Fallout76).MarkerArt);
        Assert.False(GameProfiles.For(BethesdaGame.Fallout76).MarkersAreTinted);

        // Oblivion uses its parchment-tile icons drawn untinted (EmbeddedColored, like Skyrim).
        Assert.Equal(MarkerArtStrategy.EmbeddedColored, GameProfiles.For(BethesdaGame.Oblivion).MarkerArt);
        Assert.False(GameProfiles.For(BethesdaGame.Oblivion).MarkersAreTinted);

        // Morrowind has no map markers (glyph-only default, never reached).
        Assert.False(GameProfiles.For(BethesdaGame.Morrowind).HasMapMarkers);
    }

    [Fact]
    public void DefaultGame_IsFalloutNewVegas()
    {
        Assert.Equal(BethesdaGame.FalloutNewVegas, GameProfiles.DefaultGame);
    }
}