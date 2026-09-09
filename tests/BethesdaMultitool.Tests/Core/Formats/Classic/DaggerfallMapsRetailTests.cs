using System.Globalization;
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
        return root;
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

        var result = await ClassicGameAnalyzer.LoadAsync(installRoot, TestContext.Current.CancellationToken);

        Assert.Equal(BethesdaGame.Daggerfall, result.Records.Game);
        Assert.Equal(15_251,
            result.Records.GenericRecords.Count(r => r.RecordType == DaggerfallRecordSource.LocationRecordType));
        Assert.Equal(62,
            result.Records.GenericRecords.Count(r => r.RecordType == DaggerfallRecordSource.RegionRecordType));
        Assert.Equal(result.Records.GenericRecords.Count,
            result.Records.GenericRecords.Select(r => r.FormId).Distinct().Count());
    }

    [Fact]
    public void EveryDungeonBlockReferenceResolvesToABlockThatExists()
    {
        // ⚑ THE oracle for the block-name mapping (2026-09-06): every (BlockIndex, BlockNumber)
        // pair any dungeon carries must name an RDB entry that is actually present in BLOCKS.BSA.
        // Retail: 4,232 dungeons and 40,263 references, all resolving. A wrong letter table cannot
        // survive this — the families have different sizes, so a shift strands hundreds of names.
        var maps = OpenMaps();
        var blocksPath = Path.Combine(RequireArena2(), DaggerfallBlocksFile.FileName);
        Assert.SkipWhen(!File.Exists(blocksPath), RealAssetPaths.SkipMessage("BLOCKS.BSA"));
        var blocks = DaggerfallBlocksFile.Open(blocksPath);

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < blocks.Count; i++)
        {
            present.Add(blocks.Name(i));
        }

        var dungeons = 0;
        var references = 0;
        var unresolved = new List<string>();

        foreach (var location in maps.Regions.SelectMany(r => r.Locations))
        {
            if (location.Dungeon is not { } dungeon)
            {
                continue;
            }

            dungeons++;
            foreach (var block in dungeon.Blocks)
            {
                references++;
                var name = DaggerfallDungeonBlockName.Resolve(block);
                // The cap only bounds the failure message; the assertion below is on emptiness.
                if ((name is null || !present.Contains(name)) && unresolved.Count < 12)
                {
                    unresolved.Add($"index {block.BlockIndex} number {block.BlockNumber} -> {name ?? "(no family)"}");
                }
            }
        }

        Assert.True(dungeons > 4_000, $"expected ~4,232 dungeons, saw {dungeons}");
        Assert.True(references > 40_000, $"expected ~40,263 block references, saw {references}");
        Assert.Empty(unresolved);
    }

    [Fact]
    public void TheBlockFamiliesUsedByDungeonsAreExactlyThoseTheArchiveShips()
    {
        // ⚑ Stronger than "every reference resolves": the number SET each index uses must EQUAL the
        // set that letter actually has in the archive — no unused blocks, no missing ones. That
        // two-way equality is what rules out a letter table that merely happens to cover the refs.
        // ⚠ The S family carries three non-contiguous strays (204, 205, 999) and they appear on
        // both sides; that is the case a coincidental mapping cannot reproduce.
        var maps = OpenMaps();
        var blocksPath = Path.Combine(RequireArena2(), DaggerfallBlocksFile.FileName);
        Assert.SkipWhen(!File.Exists(blocksPath), RealAssetPaths.SkipMessage("BLOCKS.BSA"));
        var blocks = DaggerfallBlocksFile.Open(blocksPath);

        var archiveByLetter = new Dictionary<char, HashSet<int>>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var name = blocks.Name(i);
            if (blocks.TypeAt(i) != DaggerfallBlockType.Rdb)
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            if (!archiveByLetter.TryGetValue(stem[0], out var set))
            {
                archiveByLetter[stem[0]] = set = [];
            }

            set.Add(int.Parse(stem[1..], CultureInfo.InvariantCulture));
        }

        var referencedByIndex = new Dictionary<byte, HashSet<int>>();
        foreach (var block in maps.Regions
                     .SelectMany(r => r.Locations)
                     .Where(l => l.Dungeon is not null)
                     .SelectMany(l => l.Dungeon!.Blocks))
        {
            if (!referencedByIndex.TryGetValue(block.BlockIndex, out var set))
            {
                referencedByIndex[block.BlockIndex] = set = [];
            }

            set.Add(block.BlockNumber);
        }

        Assert.Equal<byte[]>([0, 1, 3, 4, 5], [.. referencedByIndex.Keys.Order()]);
        foreach (var (index, numbers) in referencedByIndex)
        {
            var letter = DaggerfallDungeonBlockName.Letters[index];
            Assert.True(
                numbers.SetEquals(archiveByLetter[letter]),
                $"index {index} ('{letter}') references {numbers.Count} numbers, the archive ships {archiveByLetter[letter].Count}");
        }

        // ⚠ 'L' (index 2) is shipped by neither side; the slot exists only to keep S/B/M aligned.
        Assert.DoesNotContain('L', archiveByLetter.Keys);
    }

    [Fact]
    public void AWholeDungeonAssemblesFromItsBlockGrid()
    {
        // ⚑ The capability gate: a named dungeon must come out as one placed scene, not a pile of
        // blocks. Privateer's Hold is the game's starting dungeon and the best-known fixture —
        // 5 blocks in a plus shape, its start block being the quest-family stray S0000999.
        var maps = OpenMaps();
        var arena2 = RequireArena2();
        var blocksPath = Path.Combine(arena2, DaggerfallBlocksFile.FileName);
        Assert.SkipWhen(!File.Exists(blocksPath), RealAssetPaths.SkipMessage("BLOCKS.BSA"));
        Assert.SkipWhen(
            !File.Exists(Path.Combine(arena2, DaggerfallArch3DFile.FileName)),
            RealAssetPaths.SkipMessage(DaggerfallArch3DFile.FileName));

        var location = maps.Regions
            .SelectMany(r => r.Locations)
            .First(l => l.Dungeon is not null && l.Name.Contains("Privateer", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(5, location.Dungeon!.Blocks.Count);
        var start = location.Dungeon.Blocks.Single(b => b.IsStartingBlock);
        Assert.Equal("S0000999.RDB", DaggerfallDungeonBlockName.Resolve(start));
        Assert.Equal(0, start.X);
        Assert.Equal(0, start.Z);

        var blocks = DaggerfallBlocksFile.Open(blocksPath);
        var meshes = DaggerfallMeshLibrary.Open(arena2);
        var assembly = DaggerfallDungeonSceneAssembler.Assemble(
            location.Name,
            location.Dungeon.Blocks,
            name =>
            {
                var index = blocks.IndexOf(name);
                return index < 0 ? null : blocks.ParseRdb(index);
            },
            meshes.Resolve);

        Assert.Equal(5, assembly.BlocksPlaced);
        Assert.Empty(assembly.MissingBlockNames);
        Assert.Equal(assembly.Placed, assembly.Instances.Count);
        Assert.True(assembly.Placed > 300, $"expected ~365 placements, saw {assembly.Placed}");

        // ⚠ The grid must SPREAD the blocks. Blocks sit at X and Z indices -1..1, so the placements
        // span close to three blocks each way; if the grid offset were dropped they would all stack
        // inside one 2,048-unit block and the dungeon would render as a single room.
        var xs = assembly.Instances.Select(i => i.Transform.Translation.X).ToList();
        var zs = assembly.Instances.Select(i => i.Transform.Translation.Z).ToList();
        Assert.True(xs.Max() - xs.Min() > 2 * DaggerfallDungeonSceneAssembler.BlockGridUnits,
            $"X spans only {xs.Max() - xs.Min()} units across a 3-block-wide dungeon");
        Assert.True(zs.Max() - zs.Min() > 2 * DaggerfallDungeonSceneAssembler.BlockGridUnits,
            $"Z spans only {zs.Max() - zs.Min()} units across a 3-block-deep dungeon");
    }
}