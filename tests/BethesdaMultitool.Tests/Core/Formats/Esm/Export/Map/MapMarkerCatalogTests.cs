using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export.Map;

/// <summary>
///     Pins the per-game marker taxonomy. The same raw TNAM value means different things per game, so a
///     single cross-game enum is wrong — these tests lock the divergence (and the graceful fallback) that
///     <see cref="MapMarkerCatalog" /> exists to provide.
/// </summary>
public class MapMarkerCatalogTests
{
    [Fact]
    public void Resolve_SameRawValue_DiffersByGame()
    {
        // The exact bug this feature fixes: raw 3 is "Encampment" in FO3/FNV but "City" in Oblivion.
        Assert.Equal("Encampment", MapMarkerCatalog.Resolve(BethesdaGame.FalloutNewVegas, 3).DisplayName);
        Assert.Equal("Encampment", MapMarkerCatalog.Resolve(BethesdaGame.Fallout3, 3).DisplayName);
        Assert.Equal("City", MapMarkerCatalog.Resolve(BethesdaGame.Oblivion, 3).DisplayName);
    }

    [Fact]
    public void Resolve_KnownValues_MatchXEditTables()
    {
        Assert.Equal("City", MapMarkerCatalog.Resolve(BethesdaGame.FalloutNewVegas, 1).DisplayName);
        Assert.Equal("Vault", MapMarkerCatalog.Resolve(BethesdaGame.FalloutNewVegas, 14).DisplayName);
        Assert.Equal("Cave", MapMarkerCatalog.Resolve(BethesdaGame.Oblivion, 2).DisplayName);
        Assert.Equal("Oblivion Gate", MapMarkerCatalog.Resolve(BethesdaGame.Oblivion, 11).DisplayName);
    }

    [Fact]
    public void Resolve_IndexZero_IsNone_ForTabledGames()
    {
        Assert.Equal("None", MapMarkerCatalog.Resolve(BethesdaGame.FalloutNewVegas, 0).DisplayName);
        Assert.Equal("None", MapMarkerCatalog.Resolve(BethesdaGame.Oblivion, 0).DisplayName);
    }

    [Theory]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Unknown)]
    public void Resolve_UnwiredGame_FallsBackToTypeDistinctEntry(BethesdaGame game)
    {
        var entry = MapMarkerCatalog.Resolve(game, 5);
        Assert.Equal("Type 5", entry.DisplayName);
        Assert.Empty(entry.IconKey);
        // Type-distinct: a different raw value yields a different fallback color.
        var other = MapMarkerCatalog.Resolve(game, 6);
        Assert.NotEqual((entry.Fallback.R, entry.Fallback.G, entry.Fallback.B),
            (other.Fallback.R, other.Fallback.G, other.Fallback.B));
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Oblivion)]
    public void Resolve_OutOfRange_FallsBackInsteadOfThrowing(BethesdaGame game)
    {
        var entry = MapMarkerCatalog.Resolve(game, 999);
        Assert.Equal("Type 999", entry.DisplayName);

        var negative = MapMarkerCatalog.Resolve(game, -1);
        Assert.Equal("Type -1", negative.DisplayName);
    }

    [Fact]
    public void HasMarkers_TrueOnlyForWiredTables()
    {
        Assert.True(MapMarkerCatalog.HasMarkers(BethesdaGame.FalloutNewVegas));
        Assert.True(MapMarkerCatalog.HasMarkers(BethesdaGame.Fallout3));
        Assert.True(MapMarkerCatalog.HasMarkers(BethesdaGame.Oblivion));
        Assert.True(MapMarkerCatalog.HasMarkers(BethesdaGame.Skyrim));
        Assert.True(MapMarkerCatalog.HasMarkers(BethesdaGame.Fallout4));
        Assert.True(MapMarkerCatalog.HasMarkers(BethesdaGame.Fallout76));

        // Morrowind has no map markers.
        Assert.False(MapMarkerCatalog.HasMarkers(BethesdaGame.Morrowind));
    }

    [Fact]
    public void Resolve_Fallout76_HumanizesNamesAndKeysIcons()
    {
        // FO76's itU16 enum reuses FO4's base range then adds Appalachia-specific types; labels are the
        // AS class names, humanized for display.
        Assert.Equal("Cave", MapMarkerCatalog.Resolve(BethesdaGame.Fallout76, 0).DisplayName);
        Assert.Equal("Vault", MapMarkerCatalog.Resolve(BethesdaGame.Fallout76, 14).DisplayName);
        Assert.Equal("Vault 76", MapMarkerCatalog.Resolve(BethesdaGame.Fallout76, 68).DisplayName);
        Assert.Equal("Arktos Pharma", MapMarkerCatalog.Resolve(BethesdaGame.Fallout76, 73).DisplayName);

        Assert.Equal("fo76_marker_000", MapMarkerCatalog.Resolve(BethesdaGame.Fallout76, 0).IconKey);
        Assert.Equal("fo76_marker_068", MapMarkerCatalog.Resolve(BethesdaGame.Fallout76, 68).IconKey);
        // FO4-enum leftover with no FO76 sprite → no icon key (labeled dot).
        Assert.Empty(MapMarkerCatalog.Resolve(BethesdaGame.Fallout76, 53).IconKey);
        // Runtime markers (>=100) aren't ESM XMRK markers → names only.
        Assert.Empty(MapMarkerCatalog.Resolve(BethesdaGame.Fallout76, 105).IconKey);
    }

    [Fact]
    public void Resolve_Fallout4_MatchesEnumAndCarriesIconKeys()
    {
        // FO4's enum starts at Cave (index 0 is NOT None, unlike FO3/FNV/Oblivion/Skyrim).
        Assert.Equal("Cave", MapMarkerCatalog.Resolve(BethesdaGame.Fallout4, 0).DisplayName);
        Assert.Equal("Diamond City", MapMarkerCatalog.Resolve(BethesdaGame.Fallout4, 2).DisplayName);
        Assert.Equal("Vault", MapMarkerCatalog.Resolve(BethesdaGame.Fallout4, 15).DisplayName);
        Assert.Equal("Town", MapMarkerCatalog.Resolve(BethesdaGame.Fallout4, 49).DisplayName);

        // All 81 types (0..80) carry an embedded icon — base 0..49 from MapMarkers.swf, faction/DLC
        // 50..80 from Pipboy_MapPage.swf.
        Assert.Equal("fo4_marker_00", MapMarkerCatalog.Resolve(BethesdaGame.Fallout4, 0).IconKey);
        Assert.Equal("fo4_marker_49", MapMarkerCatalog.Resolve(BethesdaGame.Fallout4, 49).IconKey);
        Assert.Equal("Brotherhood of Steel", MapMarkerCatalog.Resolve(BethesdaGame.Fallout4, 50).DisplayName);
        Assert.Equal("fo4_marker_50", MapMarkerCatalog.Resolve(BethesdaGame.Fallout4, 50).IconKey);
        Assert.Equal("Pack", MapMarkerCatalog.Resolve(BethesdaGame.Fallout4, 80).DisplayName);
        Assert.Equal("fo4_marker_80", MapMarkerCatalog.Resolve(BethesdaGame.Fallout4, 80).IconKey);
    }

    [Fact]
    public void Resolve_Skyrim_MatchesEnumAndCarriesIconKeys()
    {
        Assert.Equal("Town", MapMarkerCatalog.Resolve(BethesdaGame.Skyrim, 2).DisplayName);
        Assert.Equal("Cave", MapMarkerCatalog.Resolve(BethesdaGame.Skyrim, 4).DisplayName);
        Assert.Equal("Dawnstar Capitol", MapMarkerCatalog.Resolve(BethesdaGame.Skyrim, 52).DisplayName);
        // Skyrim diverges from FO3/FNV at the same raw value (4 = Cave here, Natural Landmark there).
        Assert.Equal("Natural Landmark", MapMarkerCatalog.Resolve(BethesdaGame.FalloutNewVegas, 4).DisplayName);

        // Base-game types 1..52 carry an embedded icon key; None (0) and DLC02 (53..59) don't.
        Assert.Equal("skyrim_marker_04", MapMarkerCatalog.Resolve(BethesdaGame.Skyrim, 4).IconKey);
        Assert.Equal("skyrim_marker_52", MapMarkerCatalog.Resolve(BethesdaGame.Skyrim, 52).IconKey);
        Assert.Empty(MapMarkerCatalog.Resolve(BethesdaGame.Skyrim, 0).IconKey);
        Assert.Equal("Raven Rock", MapMarkerCatalog.Resolve(BethesdaGame.Skyrim, 54).DisplayName);
        Assert.Empty(MapMarkerCatalog.Resolve(BethesdaGame.Skyrim, 54).IconKey);

        var table = MapMarkerCatalog.For(BethesdaGame.Skyrim);
        Assert.Equal(60, table.Count);
        for (var i = 0; i < table.Count; i++) Assert.Equal(i, table[i].RawValue);
    }

    [Fact]
    public void For_TabledGames_AreDenseByRawValue()
    {
        var fnv = MapMarkerCatalog.For(BethesdaGame.FalloutNewVegas);
        Assert.Equal(15, fnv.Count);
        for (var i = 0; i < fnv.Count; i++) Assert.Equal(i, fnv[i].RawValue);

        var oblivion = MapMarkerCatalog.For(BethesdaGame.Oblivion);
        Assert.Equal(13, oblivion.Count);
        for (var i = 0; i < oblivion.Count; i++) Assert.Equal(i, oblivion[i].RawValue);
    }
}
