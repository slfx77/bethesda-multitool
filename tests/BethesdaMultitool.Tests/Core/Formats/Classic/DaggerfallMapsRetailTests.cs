using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of the MAPS.BSA world index against the retail ARENA2 (<c>RUN_BUCKET_B=1</c>).
///     The counts pinned here were measured directly on the archive with an independent Python
///     walk (2026-09-02) before the parser was written.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallMapsRetailTests
{
    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root!;
    }

    private static DaggerfallMapsFile OpenMaps()
    {
        var path = Path.Combine(RequireArena2(), "MAPS.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("MAPS.BSA"));
        return DaggerfallMapsFile.Open(path);
    }

    [Fact]
    public void AllRegions_TileTheirTables_AndEveryMapIdIsItsWorldPixel()
    {
        var maps = OpenMaps();

        Assert.Equal(62, maps.Regions.Count);
        Assert.Equal(15_251, maps.LocationCount);
        Assert.Equal(17, maps.Regions.Count(r => r.Locations.Count == 0));
        Assert.Equal(1_833, maps.Regions[16].Locations.Count);
        Assert.Equal(1_331, maps.Regions[17].Locations.Count);
        Assert.Equal(25, maps.Regions[19].Locations.Count);

        foreach (var location in maps.Locations)
        {
            Assert.Equal(location.MapPixelY * 1000 + location.MapPixelX, location.WorldPixelId);
            Assert.InRange(location.MapPixelX, 0, DaggerfallMapsFile.MapWidth - 1);
            Assert.InRange(location.MapPixelY, 0, DaggerfallMapsFile.MapHeight - 1);
        }

        var byType = maps.Locations.GroupBy(l => l.LocationType).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(15, byType.Count);
        Assert.Equal(410, byType[DaggerfallLocationType.TownCity]);
        Assert.Equal(1_841, byType[DaggerfallLocationType.HomeFarms]);
        Assert.Equal(2, byType[DaggerfallLocationType.HomeYourShips]);
    }

    [Fact]
    public void EveryDungeonTypedLocation_HasADungeonRecord_WithOneStartingBlock()
    {
        var maps = OpenMaps();

        var withDungeon = maps.Locations.Where(l => l.Dungeon is not null).ToList();
        Assert.Equal(4_232, withDungeon.Count);
        Assert.Equal(4_232, maps.Locations.Count(l => l.DungeonType != DaggerfallDungeonType.None));
        Assert.All(withDungeon, l => Assert.NotEqual(DaggerfallDungeonType.None, l.DungeonType));

        foreach (var location in withDungeon)
        {
            var dungeon = location.Dungeon!;
            Assert.InRange(dungeon.Blocks.Count, 1, DaggerfallMapsFile.DungeonBlockSlots);
            Assert.Equal(1, dungeon.Blocks.Count(b => b.IsStartingBlock));
            Assert.All(dungeon.Blocks, b => Assert.InRange(Math.Max(Math.Abs(b.X), Math.Abs(b.Z)), 0, 5));
        }

        Assert.Equal(22, withDungeon.Max(l => l.Dungeon!.Blocks.Count));
        Assert.Equal(774, withDungeon.Count(l => l.DungeonType == DaggerfallDungeonType.Cemetery));
    }

    [Fact]
    public void DaggerfallCity_IsThePinnedRecord()
    {
        var maps = OpenMaps();
        var region = maps.Regions[17];
        Assert.Equal("Daggerfall", region.Name);

        var city = Assert.Single(region.Locations, l => l.Name == "Daggerfall");
        Assert.Equal(1_231, city.Index);
        Assert.Equal(DaggerfallLocationType.TownCity, city.LocationType);
        Assert.Equal(DaggerfallDungeonType.HumanStronghold, city.DungeonType);
        Assert.True(city.Discovered);
        Assert.Equal(50_026, city.LocationId);
        Assert.Equal(207, city.MapPixelX);
        Assert.Equal(213, city.MapPixelY);
        Assert.Equal(8, city.BlocksWide);
        Assert.Equal(8, city.BlocksHigh);
        Assert.Equal(316, city.Buildings.Count);
        Assert.Equal(424, city.DoorCount);
        Assert.Equal(31_981_328u, city.Key);

        // A port city with bit 0 clear — the reason no port flag is derived from this byte.
        Assert.Equal(0x10, city.PortTownAndUnknown);

        Assert.NotNull(city.Dungeon);
        Assert.Contains(city.Buildings, b => b.BuildingType == DaggerfallBuildingType.Palace);
    }

    [Fact]
    public async Task Analyzer_SynthesizesARecordPerRegionAndLocation()
    {
        var arena2 = RequireArena2();
        var installRoot = Path.GetDirectoryName(arena2)!;

        var result = await ClassicGameAnalyzer.LoadAsync(installRoot);

        Assert.Equal(BethesdaGame.Daggerfall, result.Records.Game);
        Assert.Equal(62 + 15_251, result.Records.GenericRecords.Count);
        Assert.Equal(62, result.Records.GenericRecords.Count(r => r.RecordType == DaggerfallRecordSource.RegionRecordType));
        Assert.Equal(result.Records.GenericRecords.Count, result.Records.GenericRecords.Select(r => r.FormId).Distinct().Count());
    }
}
