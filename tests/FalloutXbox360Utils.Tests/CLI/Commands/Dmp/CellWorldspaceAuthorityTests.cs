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
    public void Load_ReadsCellMetadata()
    {
        var path = WriteTempJson(
            """
            {
              "schema_version": 2,
              "worldspaces": {
                "0x0000003C": "TestWorld"
              },
              "references": {
                "0x00003000": "0x00001000",
                "not-a-reference": "0x00001001"
              },
              "reference_windows": [
                {
                  "cell": "0x00001001",
                  "anchor_reference": "0x00003001",
                  "radius": "0x80",
                  "label": "interior test"
                },
                {
                  "cell": "0x00001000",
                  "center_offset": "0x123400",
                  "radius_before": "0x20",
                  "radius_after": "0x40"
                }
              ],
              "cells": {
                "0x00001000": {
                  "worldspace": "0x0000003C",
                  "is_interior": false,
                  "grid_x": -1,
                  "grid_y": 2,
                  "editor_id": "CellA",
                  "full_name": "Cell A"
                },
                "00001001": {
                  "is_interior": true,
                  "editor_id": "InteriorA"
                },
                "not-a-form-id": {
                  "worldspace": "0x0000003E"
                }
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
            Assert.Equal(0x3Cu, result.Cells[0x1000].WorldspaceFormId);
            Assert.False(result.Cells[0x1000].IsInterior);
            Assert.Equal(-1, result.Cells[0x1000].GridX);
            Assert.Equal(2, result.Cells[0x1000].GridY);
            Assert.Equal("CellA", result.Cells[0x1000].EditorId);
            Assert.Equal("Cell A", result.Cells[0x1000].FullName);
            Assert.Null(result.Cells[0x1001].WorldspaceFormId);
            Assert.True(result.Cells[0x1001].IsInterior);
            Assert.Equal("InteriorA", result.Cells[0x1001].EditorId);
            Assert.NotNull(result.CellToWorldspace);
            Assert.Single(result.CellToWorldspace);
            Assert.Equal(0x3Cu, result.CellToWorldspace[0x1000]);
            Assert.NotNull(result.WorldspaceNames);
            Assert.Equal("TestWorld", result.WorldspaceNames[0x3C]);
            Assert.NotNull(result.RefToCell);
            Assert.Single(result.RefToCell);
            Assert.Equal(0x1000u, result.RefToCell[0x3000]);
            Assert.NotNull(result.RefWindows);
            Assert.Equal(2, result.RefWindows.Count);
            Assert.Equal(0x1001u, result.RefWindows[0].CellFormId);
            Assert.Equal(0x3001u, result.RefWindows[0].AnchorReferenceFormId);
            Assert.Equal(0x80, result.RefWindows[0].RadiusBeforeBytes);
            Assert.Equal(0x80, result.RefWindows[0].RadiusAfterBytes);
            Assert.Equal("interior test", result.RefWindows[0].Label);
            Assert.Equal(0x1000u, result.RefWindows[1].CellFormId);
            Assert.Equal(0x123400L, result.RefWindows[1].CenterOffset);
            Assert.Equal(0x20, result.RefWindows[1].RadiusBeforeBytes);
            Assert.Equal(0x40, result.RefWindows[1].RadiusAfterBytes);
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
    public void Builder_KeepsFirstReferenceParentAndRecordsConflicts()
    {
        var builder = new CellWorldspaceAuthorityBuilder();

        Assert.True(builder.TryAddReferenceParent(0x3000, 0x1000, "first"));
        Assert.False(builder.TryAddReferenceParent(0x3000, 0x1001, "second"));

        Assert.Equal(0x1000u, builder.ReferenceParents[0x3000]);
        var conflicts = Assert.Single(builder.ReferenceConflicts);
        Assert.Equal(0x3000u, conflicts.Key);
        Assert.Equal([0x1000u, 0x1001u], conflicts.Value.OrderBy(v => v).ToArray());
    }

    [Fact]
    public async Task WriteAsync_EmitsCellsWorldspacesReferencesConflictsAndSources()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cell-authority-{Guid.NewGuid():N}.json");
        try
        {
            await CellWorldspaceAuthorityJson.WriteAsync(
                path,
                new Dictionary<uint, CellAuthorityMetadata>
                {
                    [0x1000] = new()
                    {
                        WorldspaceFormId = 0x3C,
                        IsInterior = false,
                        GridX = -1,
                        GridY = 2,
                        EditorId = "CellA"
                    }
                },
                new Dictionary<uint, uint> { [0x3000] = 0x1000 },
                [
                    new CellReferenceParentWindow
                    {
                        CellFormId = 0x1000,
                        AnchorReferenceFormId = 0x3001,
                        RadiusBeforeBytes = 0x20,
                        RadiusAfterBytes = 0x40,
                        Label = "sample window"
                    }
                ],
                new Dictionary<uint, HashSet<uint>> { [0x1000] = [0x3C, 0x3D] },
                new Dictionary<uint, HashSet<uint>> { [0x3001] = [0x1000, 0x1001] },
                new Dictionary<uint, string> { [0x3C] = "TestWorld" },
                [new CellWorldspaceAuthoritySource("dmp", @"Dumps\sample.dmp", 1, 2)],
                TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(
                path,
                TestContext.Current.CancellationToken));
            var cell = doc.RootElement.GetProperty("cells").GetProperty("0x00001000");
            Assert.Equal(2, doc.RootElement.GetProperty("schema_version").GetInt32());
            Assert.Equal("0x0000003C", cell.GetProperty("worldspace").GetString());
            Assert.False(cell.GetProperty("is_interior").GetBoolean());
            Assert.Equal(-1, cell.GetProperty("grid_x").GetInt32());
            Assert.Equal(2, cell.GetProperty("grid_y").GetInt32());
            Assert.Equal("CellA", cell.GetProperty("editor_id").GetString());
            Assert.Equal("TestWorld", doc.RootElement.GetProperty("worldspaces").GetProperty("0x0000003C").GetString());
            Assert.Equal(
                "0x00001000",
                doc.RootElement.GetProperty("references").GetProperty("0x00003000").GetString());
            var window = doc.RootElement.GetProperty("reference_windows")[0];
            Assert.Equal("0x00001000", window.GetProperty("cell").GetString());
            Assert.Equal("0x00003001", window.GetProperty("anchor_reference").GetString());
            Assert.Equal("0x20", window.GetProperty("radius_before").GetString());
            Assert.Equal("0x40", window.GetProperty("radius_after").GetString());
            Assert.Equal("sample window", window.GetProperty("label").GetString());
            Assert.Equal(
                ["0x0000003C", "0x0000003D"],
                doc.RootElement.GetProperty("conflicts").GetProperty("0x00001000")
                    .EnumerateArray()
                    .Select(e => e.GetString()!)
                    .ToArray());
            Assert.Equal(
                ["0x00001000", "0x00001001"],
                doc.RootElement.GetProperty("reference_conflicts").GetProperty("0x00003001")
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
    public void Apply_AppliesCellMetadataInteriorGridAndNames()
    {
        var records = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x1000,
                    Flags = 0x01,
                    EditorId = null,
                    FullName = null
                },
                new CellRecord
                {
                    FormId = 0x1001,
                    Flags = 0,
                    GridX = 10,
                    GridY = 11,
                    WorldspaceFormId = 0x99
                }
            ]
        };

        var result = CellWorldspaceAuthorityApplier.Apply(
            records,
            null,
            new Dictionary<uint, string> { [0x3C] = "TestWorld" },
            cellMetadata: new Dictionary<uint, CellAuthorityMetadata>
            {
                [0x1000] = new()
                {
                    WorldspaceFormId = 0x3C,
                    IsInterior = false,
                    GridX = -1,
                    GridY = 2,
                    EditorId = "ExteriorCell",
                    FullName = "Exterior Cell"
                },
                [0x1001] = new()
                {
                    IsInterior = true,
                    EditorId = "InteriorCell",
                    FullName = "Interior Cell"
                }
            });

        Assert.Equal(2, result.Applied);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Overrode);

        var exterior = records.Cells.Single(c => c.FormId == 0x1000);
        Assert.False(exterior.IsInterior);
        Assert.Equal(0x3Cu, exterior.WorldspaceFormId);
        Assert.Equal(-1, exterior.GridX);
        Assert.Equal(2, exterior.GridY);
        Assert.Equal("ExteriorCell", exterior.EditorId);
        Assert.Equal("Exterior Cell", exterior.FullName);

        var interior = records.Cells.Single(c => c.FormId == 0x1001);
        Assert.True(interior.IsInterior);
        Assert.Null(interior.WorldspaceFormId);
        Assert.Null(interior.GridX);
        Assert.Null(interior.GridY);
        Assert.Equal("InteriorCell", interior.EditorId);
        Assert.Equal("Interior Cell", interior.FullName);
    }

    [Fact]
    public void Apply_ReattachesUnresolvedReferencesByAuthority()
    {
        var records = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0xFE100001,
                    IsVirtual = true,
                    IsUnresolvedBucket = true,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = 0x3000,
                            BaseFormId = 0x9000,
                            X = 10,
                            Y = 20
                        }
                    ]
                }
            ]
        };

        var result = CellWorldspaceAuthorityApplier.Apply(
            records,
            null,
            cellMetadata: new Dictionary<uint, CellAuthorityMetadata>
            {
                [0x1000] = new()
                {
                    IsInterior = true,
                    EditorId = "InteriorA",
                    FullName = "Interior A"
                }
            },
            refToCell: new Dictionary<uint, uint> { [0x3000] = 0x1000 });

        Assert.Equal(1, result.ReferencesReattached);
        Assert.Equal(1, result.ReferenceCellsCreated);
        Assert.DoesNotContain(records.Cells, c => c.IsUnresolvedBucket);

        var targetCell = Assert.Single(records.Cells);
        Assert.Equal(0x1000u, targetCell.FormId);
        Assert.True(targetCell.IsInterior);
        Assert.Equal("InteriorA", targetCell.EditorId);
        var placed = Assert.Single(targetCell.PlacedObjects);
        Assert.Equal(0x3000u, placed.FormId);
        Assert.Equal("AuthorityRefParent", placed.AssignmentSource);
    }

    [Fact]
    public void Apply_ReattachesUnresolvedExteriorClusterByAuthorityOffsetProximity()
    {
        var records = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x1000,
                    GridX = -4,
                    GridY = -1,
                    WorldspaceFormId = 0x10,
                    WorldspaceAssignmentSource = "Authority",
                    Offset = 0x2000
                },
                new CellRecord
                {
                    FormId = 0x1001,
                    GridX = -4,
                    GridY = -2,
                    WorldspaceFormId = 0x10,
                    WorldspaceAssignmentSource = "Authority",
                    Offset = 0x2800
                },
                new CellRecord
                {
                    FormId = 0xFE100001,
                    IsVirtual = true,
                    IsUnresolvedBucket = true,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = 0x3000,
                            BaseFormId = 0x9000,
                            X = -5136,
                            Y = -3488,
                            Offset = 0x1F00
                        },
                        new PlacedReference
                        {
                            FormId = 0x3001,
                            BaseFormId = 0x9001,
                            X = -5200,
                            Y = -3400,
                            Offset = 0x1F40
                        }
                    ]
                }
            ]
        };

        var result = CellWorldspaceAuthorityApplier.Apply(
            records,
            new Dictionary<uint, uint>
            {
                [0x1000] = 0x10,
                [0x1001] = 0x10
            });

        Assert.Equal(2, result.ReferencesReattached);
        Assert.Equal(1, result.ReferenceCellsCreated);
        Assert.DoesNotContain(records.Cells, c => c.IsUnresolvedBucket);

        var virtualCell = Assert.Single(records.Cells, c => c.IsVirtual);
        Assert.Equal(0x10u, virtualCell.WorldspaceFormId);
        Assert.Equal(-2, virtualCell.GridX);
        Assert.Equal(-1, virtualCell.GridY);
        Assert.All(virtualCell.PlacedObjects, placed => Assert.Equal("AuthorityOffsetCluster", placed.AssignmentSource));
    }

    [Fact]
    public void Apply_ExactReferenceAuthorityOverridesHeuristicVirtualCell()
    {
        var records = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0xFE900001,
                    IsVirtual = true,
                    WorldspaceFormId = 0x20,
                    WorldspaceAssignmentSource = "OffsetCluster",
                    GridX = -2,
                    GridY = -1,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = 0x3000,
                            BaseFormId = 0x9000,
                            X = -5136,
                            Y = -3488,
                            AssignmentSource = "OffsetCluster"
                        }
                    ]
                }
            ]
        };

        var result = CellWorldspaceAuthorityApplier.Apply(
            records,
            null,
            cellMetadata: new Dictionary<uint, CellAuthorityMetadata>
            {
                [0x1000] = new()
                {
                    WorldspaceFormId = 0x10,
                    IsInterior = false,
                    GridX = -6,
                    GridY = 8,
                    EditorId = "CanonicalCell"
                }
            },
            refToCell: new Dictionary<uint, uint> { [0x3000] = 0x1000 });

        Assert.Equal(1, result.ReferencesReattached);
        Assert.Equal(1, result.ReferenceCellsCreated);
        Assert.DoesNotContain(records.Cells, c => c.FormId == 0xFE900001);

        var targetCell = Assert.Single(records.Cells);
        Assert.Equal(0x1000u, targetCell.FormId);
        Assert.Equal(0x10u, targetCell.WorldspaceFormId);
        Assert.Equal("CanonicalCell", targetCell.EditorId);
        var placed = Assert.Single(targetCell.PlacedObjects);
        Assert.Equal(0x3000u, placed.FormId);
        Assert.Equal("AuthorityRefParent", placed.AssignmentSource);
    }

    [Fact]
    public void Apply_ReattachesUnresolvedReferencesByPinnedWindowAfterExactAnchor()
    {
        var records = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0xFE100001,
                    IsVirtual = true,
                    IsUnresolvedBucket = true,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = 0x3000,
                            BaseFormId = 0x9000,
                            Offset = 0x5000
                        },
                        new PlacedReference
                        {
                            FormId = 0x3001,
                            BaseFormId = 0x9001,
                            Offset = 0x5050
                        },
                        new PlacedReference
                        {
                            FormId = 0x3002,
                            BaseFormId = 0x9002,
                            Offset = 0x5400
                        }
                    ]
                }
            ]
        };

        var result = CellWorldspaceAuthorityApplier.Apply(
            records,
            null,
            cellMetadata: new Dictionary<uint, CellAuthorityMetadata>
            {
                [0x1000] = new()
                {
                    IsInterior = true,
                    EditorId = "PinnedInterior"
                }
            },
            refToCell: new Dictionary<uint, uint> { [0x3000] = 0x1000 },
            refWindows:
            [
                new CellReferenceParentWindow
                {
                    CellFormId = 0x1000,
                    AnchorReferenceFormId = 0x3000,
                    RadiusBeforeBytes = 0x100,
                    RadiusAfterBytes = 0x100,
                    Label = "test window"
                }
            ]);

        Assert.Equal(2, result.ReferencesReattached);
        Assert.Equal(1, result.ReferenceCellsCreated);
        Assert.Equal(1, result.ReferenceWindowsApplied);

        var targetCell = records.Cells.Single(c => c.FormId == 0x1000);
        Assert.Equal("PinnedInterior", targetCell.EditorId);
        Assert.Equal(
            [0x3000u, 0x3001u],
            targetCell.PlacedObjects.Select(p => p.FormId).OrderBy(id => id).ToArray());
        Assert.Equal(
            "AuthorityRefParent",
            targetCell.PlacedObjects.Single(p => p.FormId == 0x3000).AssignmentSource);
        Assert.Equal(
            "AuthorityRefWindow",
            targetCell.PlacedObjects.Single(p => p.FormId == 0x3001).AssignmentSource);

        var unresolved = Assert.Single(records.Cells, c => c.IsUnresolvedBucket);
        Assert.Equal(0x3002u, Assert.Single(unresolved.PlacedObjects).FormId);
    }

    [Fact]
    public void Apply_SkipsPinnedWindowWhenOffsetMatchesMultipleCells()
    {
        var records = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x1000,
                    Flags = 0x01
                },
                new CellRecord
                {
                    FormId = 0x1001,
                    Flags = 0x01
                },
                new CellRecord
                {
                    FormId = 0xFE100001,
                    IsVirtual = true,
                    IsUnresolvedBucket = true,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = 0x3000,
                            BaseFormId = 0x9000,
                            Offset = 0x5000
                        }
                    ]
                }
            ]
        };

        var result = CellWorldspaceAuthorityApplier.Apply(
            records,
            null,
            refWindows:
            [
                new CellReferenceParentWindow
                {
                    CellFormId = 0x1000,
                    CenterOffset = 0x5000,
                    RadiusBeforeBytes = 0x100,
                    RadiusAfterBytes = 0x100
                },
                new CellReferenceParentWindow
                {
                    CellFormId = 0x1001,
                    CenterOffset = 0x5000,
                    RadiusBeforeBytes = 0x100,
                    RadiusAfterBytes = 0x100
                }
            ]);

        Assert.Equal(0, result.ReferencesReattached);
        Assert.Equal(2, result.ReferenceWindowsApplied);
        Assert.Equal(1, result.ReferenceWindowAmbiguousMatches);
        Assert.Single(records.Cells.Single(c => c.IsUnresolvedBucket).PlacedObjects);
        Assert.Empty(records.Cells.Single(c => c.FormId == 0x1000).PlacedObjects);
        Assert.Empty(records.Cells.Single(c => c.FormId == 0x1001).PlacedObjects);
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
