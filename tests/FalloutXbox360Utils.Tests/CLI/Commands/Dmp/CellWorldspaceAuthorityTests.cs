using System.Text.Json;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using Xunit;

namespace FalloutXbox360Utils.Tests.CLI.Commands.Dmp;

public class CellWorldspaceAuthorityTests
{
    [Fact]
    public void Load_ReadsHexCellMap()
    {
        var path = WriteTempJson(
            """
            {
              "worldspaces": {
                "0x0000003C": "TestWorld"
              },
              "cells": {
                "0x00001000": "0x0000003C",
                "00001001": "0000003D",
                "not-a-form-id": "0x0000003E"
              }
            }
            """);
        try
        {
            var result = CellWorldspaceAuthorityJson.Load(path);

            Assert.Null(result.Warning);
            Assert.Equal(path, result.Path);
            Assert.NotNull(result.Cells);
            Assert.Equal(2, result.Cells.Count);
            Assert.Equal(0x3Cu, result.Cells[0x1000]);
            Assert.Equal(0x3Du, result.Cells[0x1001]);
            Assert.NotNull(result.WorldspaceNames);
            Assert.Equal("TestWorld", result.WorldspaceNames[0x3C]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Merge_PcEsmEntriesWinOverCorpusAuthority()
    {
        var pcEsm = new Dictionary<uint, uint>
        {
            [0x1000] = 0x3C,
            [0x1001] = 0x3D
        };
        var authority = new Dictionary<uint, uint>
        {
            [0x1000] = 0x99,
            [0x1002] = 0x40
        };

        var merged = CellWorldspaceAuthorityJson.Merge(pcEsm, authority);

        Assert.NotNull(merged);
        Assert.Equal(0x3Cu, merged[0x1000]);
        Assert.Equal(0x3Du, merged[0x1001]);
        Assert.Equal(0x40u, merged[0x1002]);
    }

    [Fact]
    public void Builder_KeepsFirstAssignmentAndRecordsConflicts()
    {
        var builder = new CellWorldspaceAuthorityBuilder();

        Assert.True(builder.TryAddOrFlag(0x1000, 0x3C, "first"));
        Assert.False(builder.TryAddOrFlag(0x1000, 0x3D, "second"));

        Assert.Equal(0x3Cu, builder.CellToWorldspace[0x1000]);
        var conflicts = Assert.Single(builder.Conflicts);
        Assert.Equal(0x1000u, conflicts.Key);
        Assert.Equal([0x3Cu, 0x3Du], conflicts.Value.OrderBy(v => v).ToArray());
    }

    [Fact]
    public async Task WriteAsync_EmitsCellsWorldspacesConflictsAndSources()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cell-authority-{Guid.NewGuid():N}.json");
        try
        {
            await CellWorldspaceAuthorityJson.WriteAsync(
                path,
                new Dictionary<uint, uint> { [0x1000] = 0x3C },
                new Dictionary<uint, HashSet<uint>> { [0x1000] = [0x3C, 0x3D] },
                new Dictionary<uint, string> { [0x3C] = "TestWorld" },
                [new CellWorldspaceAuthoritySource("dmp", @"Dumps\sample.dmp", 1, 2)],
                TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(
                path,
                TestContext.Current.CancellationToken));
            Assert.Equal("0x0000003C", doc.RootElement.GetProperty("cells").GetProperty("0x00001000").GetString());
            Assert.Equal("TestWorld", doc.RootElement.GetProperty("worldspaces").GetProperty("0x0000003C").GetString());
            Assert.Equal(
                ["0x0000003C", "0x0000003D"],
                doc.RootElement.GetProperty("conflicts").GetProperty("0x00001000")
                    .EnumerateArray()
                    .Select(e => e.GetString()!)
                    .ToArray());
            Assert.Equal("Dumps/sample.dmp",
                doc.RootElement.GetProperty("sources")[0].GetProperty("path").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Apply_UpdatesCellsAndRebuildsWorldspaceChildren()
    {
        var records = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x1000,
                    GridX = -1,
                    GridY = 2,
                    WorldspaceFormId = 0x99,
                    WorldspaceAssignmentSource = "RuntimeCellMap"
                },
                new CellRecord
                {
                    FormId = 0x1001,
                    GridX = 3,
                    GridY = 4
                }
            ],
            Worldspaces =
            [
                new WorldspaceRecord
                {
                    FormId = 0x99,
                    EditorId = "WrongWorld",
                    Cells =
                    [
                        new CellRecord { FormId = 0x1000, WorldspaceFormId = 0x99 }
                    ]
                }
            ]
        };

        var result = CellWorldspaceAuthorityApplier.Apply(
            records,
            new Dictionary<uint, uint>
            {
                [0x1000] = 0x3C,
                [0x1001] = 0x3C
            },
            new Dictionary<uint, string> { [0x3C] = "TestWorld" });

        Assert.Equal(2, result.Applied);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Overrode);
        Assert.Equal(1, result.SynthesizedWorldspaces);
        Assert.All(records.Cells, c =>
        {
            Assert.Equal(0x3Cu, c.WorldspaceFormId);
            Assert.Equal("Authority", c.WorldspaceAssignmentSource);
        });
        var worldspace = Assert.Single(records.Worldspaces, w => w.FormId == 0x3C);
        Assert.Equal("TestWorld", worldspace.EditorId);
        Assert.Equal([0x1000u, 0x1001u], worldspace.Cells.Select(c => c.FormId).OrderBy(id => id).ToArray());
        Assert.Empty(Assert.Single(records.Worldspaces, w => w.FormId == 0x99).Cells);
    }

    [Fact]
    public void Apply_ReattachesLandAfterAuthorityChangesWorldspace()
    {
        var heightmap = new LandHeightmap
        {
            HeightDeltas = Enumerable.Repeat<sbyte>(1, 33 * 33).ToArray()
        };
        var visualData = new LandVisualData
        {
            TextureIndices = [0x100u],
            TextureLayers =
            [
                new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Base,
                    TextureFormId = 0x100u,
                    Quadrant = 0,
                    PlatformFlag = 0,
                    Layer = 0
                }
            ]
        };
        var records = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x2000,
                    GridX = -18,
                    GridY = 0,
                    WorldspaceFormId = 0x9999,
                    WorldspaceAssignmentSource = "RuntimeCellMap"
                }
            ]
        };
        var scanResult = new EsmRecordScanResult
        {
            LandRecords =
            [
                new ExtractedLandRecord
                {
                    Header = new DetectedMainRecord("LAND", 0, 0, 0x3000, 0x4000, false),
                    ParentCellFormId = 0x2000,
                    WorldspaceFormId = 0x9999,
                    RuntimeCellX = -18,
                    RuntimeCellY = 0,
                    Heightmap = heightmap,
                    VisualData = visualData
                }
            ]
        };

        var result = CellWorldspaceAuthorityApplier.Apply(
            records,
            new Dictionary<uint, uint> { [0x2000] = 0x3Cu },
            new Dictionary<uint, string> { [0x3C] = "TheStripWorld" },
            scanResult);

        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.Overrode);
        Assert.Equal(1, result.TerrainCellsAttached);
        var cell = Assert.Single(records.Cells);
        Assert.Equal(0x3Cu, cell.WorldspaceFormId);
        Assert.Same(heightmap, cell.Heightmap);
        Assert.Same(visualData, cell.LandVisualData);
        Assert.Equal(0x3Cu, Assert.Single(scanResult.LandRecords).WorldspaceFormId);
        var worldspace = Assert.Single(records.Worldspaces);
        Assert.Equal(0x3Cu, worldspace.FormId);
        Assert.Equal("TheStripWorld", worldspace.EditorId);
        Assert.Same(cell.Heightmap, Assert.Single(worldspace.Cells).Heightmap);
    }

    private static string WriteTempJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cell-authority-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
