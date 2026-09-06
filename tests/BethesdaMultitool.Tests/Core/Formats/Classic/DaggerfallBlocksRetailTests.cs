using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of BLOCKS.BSA against the retail ARENA2 (<c>RUN_BUCKET_B=1</c>). Every number
///     was measured with an independent Python walk (2026-09-03) before the parsers were written.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallBlocksRetailTests
{
    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root!;
    }

    private static DaggerfallBlocksFile OpenArchive()
    {
        var path = Path.Combine(RequireArena2(), DaggerfallBlocksFile.FileName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BLOCKS.BSA"));
        return DaggerfallBlocksFile.Open(path);
    }

    [Fact]
    public void BlockAssembly_KeepsEveryPlacementInsideItsOwnBlockSquare()
    {
        var blocks = OpenArchive();

        // ⚠ This is the check that discriminates the Z-mirror reading. A block is 4096 units
        // square. Applying the mirror ONCE, as a frame conversion on the summed position, keeps
        // every placement inside that square. Mirroring the model AND the sub-block position
        // separately re-mirrors and pushes placements out past 4096 — while still producing a
        // building that looks entirely plausible on its own.
        var inspected = 0;
        var placements = 0;
        var outside = new List<string>();

        for (var i = 0; i < blocks.Count && inspected < 40; i++)
        {
            if (blocks.TypeAt(i) != DaggerfallBlockType.Rmb)
            {
                continue;
            }

            var block = blocks.ParseRmb(i);
            if (block.SubRecords.Count == 0)
            {
                continue;
            }

            inspected++;
            var assembly = DaggerfallBlockSceneAssembler.Assemble(block.Name, block.SubRecords, _ => null);
            placements += assembly.Placed;

            foreach (var sub in block.SubRecords)
            {
                foreach (var model in sub.Exterior.Models)
                {
                    var at = DaggerfallBlockSceneAssembler.TransformFor(sub, model).Translation;
                    if (at.Z is < -DaggerfallBlockSceneAssembler.BlockSideUnits
                        or > (2 * DaggerfallBlockSceneAssembler.BlockSideUnits))
                    {
                        outside.Add($"{block.Name}: z={at.Z:F0}");
                    }
                }
            }
        }

        Assert.True(inspected >= 20, $"only {inspected} RMB blocks inspected");
        Assert.True(placements > 0, "no models placed at all");
        Assert.Empty(outside);
    }

    [Fact]
    public void Archive_HasTheRetailPopulation_IncludingTheStrayListing()
    {
        var blocks = OpenArchive();

        Assert.Equal(1_295, blocks.Count);
        var byType = Enumerable.Range(0, blocks.Count).GroupBy(blocks.TypeAt).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(920, byType[DaggerfallBlockType.Rmb]);
        Assert.Equal(187, byType[DaggerfallBlockType.Rdb]);
        Assert.Equal(187, byType[DaggerfallBlockType.Rdi]);
        Assert.Equal(1, byType[DaggerfallBlockType.Unknown]);

        Assert.Equal("WALLAA03.RMB", blocks.Name(0));
        var stray = blocks.IndexOf("FOO");
        Assert.Equal(669, stray);
        Assert.Equal(52_350, blocks.RecordBytes(stray).Length);
        Assert.StartsWith("\r\n Volume in dri", System.Text.Encoding.ASCII.GetString(blocks.RecordBytes(stray).Span[..16]), System.StringComparison.Ordinal);

        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks.TypeAt(i) == DaggerfallBlockType.Rdi)
            {
                Assert.Equal(512, blocks.RdiBytes(i).Length);
            }
        }
    }

    [Fact]
    public void EveryRmb_TilesExactly_WithTheRetailTotals()
    {
        var blocks = OpenArchive();

        var subRecords = 0;
        var trailing = 0;
        var models = 0;
        var flats = 0;
        var people = 0;
        var doors = 0;
        var section3 = 0;
        var loose3d = 0;
        var looseFlats = 0;
        var modelIds = new HashSet<uint>();
        var idCounts = new Dictionary<uint, int>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks.TypeAt(i) != DaggerfallBlockType.Rmb)
            {
                continue;
            }

            var block = blocks.ParseRmb(i);
            Assert.Equal(blocks.RecordBytes(i).Length, block.ParsedLength);
            subRecords += block.SubRecords.Count;
            trailing += block.SubRecords.Count(s => s.TrailingByte is not null);
            foreach (var set in block.SubRecords.SelectMany(s => new[] { s.Exterior, s.Interior }))
            {
                models += set.Models.Count;
                flats += set.Flats.Count;
                people += set.People.Count;
                doors += set.Doors.Count;
                section3 += set.Section3.Count;
            }

            loose3d += block.Misc3dObjects.Count;
            looseFlats += block.MiscFlats.Count;
            foreach (var model in block.AllModels)
            {
                modelIds.Add(model.ModelId);
                idCounts[model.ModelId] = idCounts.GetValueOrDefault(model.ModelId) + 1;
            }
        }

        Assert.Equal(9_005, subRecords);
        Assert.Equal(5_549, trailing);
        Assert.Equal(236_250, models);
        Assert.Equal(109_260, flats);
        Assert.Equal(14_174, people);
        Assert.Equal(25_922, doors);
        Assert.Equal(173_147, section3);
        Assert.Equal(9_153, loose3d);
        Assert.Equal(11_732, looseFlats);
        Assert.Equal(1_575, modelIds.Count);
        Assert.Equal(9_365, idCounts[31006]);

        // The alchemist block: twelve buildings, the shop first, a house rotated a quarter turn.
        var alchemist = blocks.ParseRmb(blocks.IndexOf("ALCHBM00.RMB"));
        Assert.Equal("ALCHBM00.RMB", alchemist.HeaderName);
        Assert.Equal(12, alchemist.SubRecords.Count);
        Assert.Equal(6, alchemist.Misc3dObjects.Count);
        Assert.Equal(20, alchemist.MiscFlats.Count);
        Assert.Equal(DaggerfallBuildingType.Alchemist, alchemist.Buildings[0].BuildingType);
        Assert.Equal(5, alchemist.Buildings[0].Quality);
        Assert.Equal((1728, 1792, 0), (alchemist.SubRecords[0].XPos, alchemist.SubRecords[0].ZPos, alchemist.SubRecords[0].YRotation));
        Assert.Equal(DaggerfallBuildingType.House2, alchemist.Buildings[1].BuildingType);
        Assert.Equal(512, alchemist.SubRecords[1].YRotation);
        Assert.Equal(90f, alchemist.SubRecords[1].YRotation / DaggerfallRmbBlock.RotationDivisor, 3);
    }

    [Fact]
    public void EveryRdb_Parses_WithTheRetailObjectCensus()
    {
        var blocks = OpenArchive();

        var grids = new Dictionary<(int, int), int>();
        var models = 0;
        var flats = 0;
        var lights = 0;
        var actions = 0;
        var cellsUsed = 0;
        var cells = 0;
        var longest = 0;
        var dagr = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks.TypeAt(i) != DaggerfallBlockType.Rdb)
            {
                continue;
            }

            var block = blocks.ParseRdb(i);
            grids[(block.Width, block.Height)] = grids.GetValueOrDefault((block.Width, block.Height)) + 1;
            cells += block.ObjectRoots.Count;
            cellsUsed += block.ObjectRoots.Count(r => r.Objects.Count > 0);
            longest = System.Math.Max(longest, block.ObjectRoots.Max(r => r.Objects.Count));
            if (block.ObjectHeader.Dagr == "DAGR")
            {
                dagr++;
            }

            foreach (var rdbObject in block.AllObjects)
            {
                switch (rdbObject.Type)
                {
                    case DaggerfallRdbResourceType.Model:
                        models++;
                        Assert.InRange(rdbObject.Model!.ModelIndex, 0, DaggerfallRdbBlock.ModelReferenceCount - 1);
                        if (rdbObject.Model.Action is not null)
                        {
                            actions++;
                        }

                        break;
                    case DaggerfallRdbResourceType.Flat:
                        flats++;
                        break;
                    default:
                        lights++;
                        break;
                }
            }
        }

        Assert.Equal(182, grids[(4, 4)]);
        Assert.Equal(5, grids[(8, 8)]);
        Assert.Equal(3_232, cells);
        Assert.Equal(820, cellsUsed);
        Assert.Equal(167, longest);
        Assert.Equal((22_961, 12_238, 4_268), (models, flats, lights));
        Assert.Equal(1_469, actions);

        // Eight blocks carry 0xFFFFFFFF where the others spell DAGR; the tag is reported, not required.
        Assert.Equal(179, dagr);
        Assert.NotEqual("DAGR", blocks.ParseRdb(blocks.IndexOf("N0000021.RDB")).ObjectHeader.Dagr);
    }

    [Fact]
    public async Task Analyzer_AddsABlockRecordPerArchiveEntry()
    {
        var arena2 = RequireArena2();
        var installRoot = Path.GetDirectoryName(arena2)!;

        var result = await ClassicGameAnalyzer.LoadAsync(installRoot, TestContext.Current.CancellationToken);

        var records = result.Records.GenericRecords;
        // Each family pins its own count in its own test; here only the blocks and the shared
        // invariant that no two synthesized records collide on a FormID.
        Assert.Equal(1_295, records.Count(r => r.RecordType == DaggerfallRecordSource.BlockRecordType));
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());
    }
}
