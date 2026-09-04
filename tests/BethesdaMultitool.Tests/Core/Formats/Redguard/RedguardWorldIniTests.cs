using System.Linq;
using BethesdaMultitool.Core.Formats.Redguard;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Grammar vectors for <c>WORLD.INI</c>, shaped to what retail actually contains (measured
///     2026-09-04: 29 worlds, 70 indexed keys, non-contiguous indices, CRLF, pure ASCII).
/// </summary>
public sealed class RedguardWorldIniTests
{
    private const string Sample = """
        [world]
        start_world=1
        start_marker=0
        ;test_map_order = 0,1,2

        world_map[0]=MAPS\start.rgm
        world_world[0]=MAPS\hideout.WLD
        world_palette[0]=3DART\sunset.COL
        world_sky[0]=system\sunset.GXA
        world_redbook[0]=7

        world_map[1]=MAPS\ISLAND.rgm
        world_palette[1]=3DART\island.COL
        world_node_map1[1]=maps\islan001.noo
        world_node_map2[1]=maps\islan002.noo
        world_node_map10[1]=maps\islan010.noo
        """;

    [Fact]
    public void ParseReadsTheLooseSectionKeys()
    {
        var ini = RedguardWorldIni.Parse(Sample);

        Assert.Equal("1", ini.Settings["start_world"]);
        Assert.Equal("0", ini.Settings["start_marker"]);

        // The commented-out key must not appear.
        Assert.False(ini.Settings.ContainsKey("test_map_order"));
    }

    [Fact]
    public void ParseGroupsIndexedKeysIntoWorlds()
    {
        var ini = RedguardWorldIni.Parse(Sample);

        Assert.Equal([0, 1], ini.Worlds.Select(w => w.Index));

        var first = ini.Worlds[0];
        Assert.Equal(@"MAPS\start.rgm", first.MapPath);
        Assert.Equal(@"MAPS\hideout.WLD", first.TerrainPath);
        Assert.Equal(@"3DART\sunset.COL", first.PalettePath);
        Assert.Equal(@"system\sunset.GXA", first.SkyPath);
        Assert.Equal(7, first.RedbookTrack);
    }

    [Fact]
    public void AWorldReportsNullForWhatItDoesNotDeclare()
    {
        // Only the 7 outdoor worlds carry terrain; an indoor one must answer null, not throw.
        var world = RedguardWorldIni.Parse(Sample).Worlds[1];

        Assert.Null(world.TerrainPath);
        Assert.Null(world.SkyPath);
        Assert.Null(world.RedbookTrack);
        Assert.Equal(@"MAPS\ISLAND.rgm", world.MapPath);
    }

    [Fact]
    public void NodeMapsSortNumericallyNotOrdinally()
    {
        // Retail reaches world_node_map32, so an ordinal sort would place 10 before 2.
        var world = RedguardWorldIni.Parse(Sample).Worlds[1];

        Assert.Equal(
            [@"maps\islan001.noo", @"maps\islan002.noo", @"maps\islan010.noo"],
            world.NodeMaps);
    }

    [Fact]
    public void StartWorldResolvesThroughTheIndex()
    {
        var ini = RedguardWorldIni.Parse(Sample);

        Assert.NotNull(ini.StartWorld);
        Assert.Equal(1, ini.StartWorld!.Index);
    }

    [Fact]
    public void StartWorldIsNullWhenItNamesNoLoadedWorld()
    {
        var ini = RedguardWorldIni.Parse("[world]\nstart_world=42\nworld_map[0]=a.rgm");

        Assert.Null(ini.StartWorld);
    }

    [Fact]
    public void IndicesNeedNotBeContiguous()
    {
        // Retail skips 9, 10 and 16 and then uses 99, so position must never stand in for index.
        var ini = RedguardWorldIni.Parse("[world]\nworld_map[0]=a.rgm\nworld_map[8]=b.rgm\nworld_map[99]=c.rgm");

        Assert.Equal([0, 8, 99], ini.Worlds.Select(w => w.Index));
        Assert.Equal("c.rgm", ini.Worlds[2].MapPath);
    }

    [Fact]
    public void CrLfAndSurroundingSpaceAreTolerated()
    {
        var ini = RedguardWorldIni.Parse("[world]\r\n  world_flash_filename [ 3 ]  =  system\\island.gxa  \r\n");

        var world = Assert.Single(ini.Worlds);
        Assert.Equal(3, world.Index);
        Assert.Equal(@"system\island.gxa", world.Values["world_flash_filename"]);
    }

    [Fact]
    public void EveryValueIsKeptEvenWhenItHasNoTypedAccessor()
    {
        // 70 indexed keys exist and only a handful are modelled; nothing may be silently dropped.
        var ini = RedguardWorldIni.Parse("[world]\nworld_sunrgb[0]=255,220,200,2\nworld_wave[0]=24,28,5");

        var world = Assert.Single(ini.Worlds);
        Assert.Equal("255,220,200,2", world.Values["world_sunrgb"]);
        Assert.Equal("24,28,5", world.Values["world_wave"]);
    }
}
