using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public class CellLinkageWorldspaceInferenceTests
{
    [Fact]
    public void InferCellWorldspaces_DoesNotAssignWhenBoundsOverlap()
    {
        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x1000,
                GridX = 3,
                GridY = -2
            }
        };
        var worldspaces = new List<WorldspaceRecord>
        {
            CreateWorldspace(0x10, "Large", -3, 5, 7, -8),
            CreateWorldspace(0x20, "Small", -1, 5, 3, -3)
        };

        CellLinkageHandler.InferCellWorldspaces(cells, worldspaces);

        Assert.Null(cells[0].WorldspaceFormId);
        Assert.Equal("AmbiguousBounds", cells[0].WorldspaceAssignmentSource);
        Assert.Equal(new uint[] { 0x10, 0x20 }, cells[0].CandidateWorldspaceFormIds);
    }

    [Fact]
    public void ResolveRuntimeAnchoredCellRuns_AssignsAdjacentAmbiguousCellsToRuntimeAnchor()
    {
        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x1000,
                GridX = 3,
                GridY = -1,
                WorldspaceFormId = 0x10,
                WorldspaceAssignmentSource = "RuntimeCellMap"
            },
            new()
            {
                FormId = 0x1001,
                GridX = 3,
                GridY = -2,
                WorldspaceAssignmentSource = "AmbiguousBounds",
                CandidateWorldspaceFormIds = [0x10, 0x20]
            },
            new()
            {
                FormId = 0x1002,
                GridX = 3,
                GridY = -3,
                WorldspaceAssignmentSource = "AmbiguousBounds",
                CandidateWorldspaceFormIds = [0x10, 0x20]
            }
        };
        var worldspaces = new List<WorldspaceRecord>
        {
            CreateWorldspace(0x10, "TheStripWorld", -3, 5, 7, -8),
            CreateWorldspace(0x20, "GreenhouseWorld01", -1, 5, 3, -3)
        };
        var runtimeMaps = new Dictionary<uint, RuntimeWorldspaceData>
        {
            [0x10] = new()
            {
                FormId = 0x10,
                Cells =
                [
                    new RuntimeCellMapEntry
                    {
                        CellFormId = 0x1000,
                        GridX = 3,
                        GridY = -1,
                        WorldspaceFormId = 0x10
                    }
                ]
            }
        };

        var reassigned = CellLinkageHandler.ResolveRuntimeAnchoredCellRuns(cells, worldspaces, runtimeMaps);

        Assert.Equal(2, reassigned);
        Assert.Equal(0x10u, cells[1].WorldspaceFormId);
        Assert.Equal(0x10u, cells[2].WorldspaceFormId);
        Assert.Equal("FragmentRun", cells[1].WorldspaceAssignmentSource);
        Assert.Equal("FragmentRun", cells[2].WorldspaceAssignmentSource);
    }

    [Fact]
    public void ResolveRuntimeAnchoredCellRuns_DoesNotAssignWhenAnchorWorldspaceIsNotCandidate()
    {
        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x1000,
                GridX = 3,
                GridY = -1,
                WorldspaceFormId = 0x10,
                WorldspaceAssignmentSource = "RuntimeCellMap"
            },
            new()
            {
                FormId = 0x1001,
                GridX = 3,
                GridY = -2,
                WorldspaceAssignmentSource = "AmbiguousBounds",
                CandidateWorldspaceFormIds = [0x20]
            }
        };
        var worldspaces = new List<WorldspaceRecord>
        {
            CreateWorldspace(0x10, "TheStripWorld", -3, 5, 7, -8),
            CreateWorldspace(0x20, "Other", -1, 5, 3, -3)
        };
        var runtimeMaps = new Dictionary<uint, RuntimeWorldspaceData>
        {
            [0x10] = new()
            {
                FormId = 0x10,
                Cells =
                [
                    new RuntimeCellMapEntry
                    {
                        CellFormId = 0x1000,
                        GridX = 3,
                        GridY = -1,
                        WorldspaceFormId = 0x10
                    }
                ]
            }
        };

        var reassigned = CellLinkageHandler.ResolveRuntimeAnchoredCellRuns(cells, worldspaces, runtimeMaps);

        Assert.Equal(0, reassigned);
        Assert.Null(cells[1].WorldspaceFormId);
    }

    [Fact]
    public void CreateVirtualCells_UsesOffsetClusterAdjacencyForMissingExteriorTile()
    {
        var existingCells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x1000,
                GridX = -4,
                GridY = -1,
                WorldspaceFormId = 0x10,
                Offset = 0x2000,
                IsBigEndian = true
            },
            new()
            {
                FormId = 0x1001,
                GridX = -4,
                GridY = -2,
                WorldspaceFormId = 0x10,
                Offset = 0x2800,
                IsBigEndian = true
            }
        };
        var orphan = new ExtractedRefrRecord
        {
            Header = new DetectedMainRecord("REFR", 0, 0, 0x3000, 0x1F00, true),
            BaseFormId = 0x4000,
            Position = new PositionSubrecord(-5136, -3488, 1040, 0, 0, 0, 0x1F00, true)
        };
        var context = new RecordParserContext(new EsmRecordScanResult
        {
            RefrRecords = [orphan]
        });

        var virtualCells = CellLinkageHandler.CreateVirtualCells(existingCells, [orphan], context);

        var virtualCell = Assert.Single(virtualCells);
        Assert.False(virtualCell.IsUnresolvedBucket);
        Assert.True(virtualCell.IsVirtual);
        Assert.Equal(0x10u, virtualCell.WorldspaceFormId);
        Assert.Equal(-2, virtualCell.GridX);
        Assert.Equal(-1, virtualCell.GridY);
        var placed = Assert.Single(virtualCell.PlacedObjects);
        Assert.Equal(0x3000u, placed.FormId);
        Assert.Equal("OffsetCluster", placed.AssignmentSource);
    }

    [Fact]
    public void AssignRuntimeCellMapOwners_ResolvesCellPresentOnlyInPCellMapWithNoCandidates()
    {
        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x000E456B
                // no worldspace, no grid, no candidates — only present in the pCellMap below
            }
        };
        var runtimeMaps = new Dictionary<uint, RuntimeWorldspaceData>
        {
            [0x10] = new()
            {
                FormId = 0x10,
                Cells =
                [
                    new RuntimeCellMapEntry { CellFormId = 0x000E456B, GridX = 5, GridY = -3, WorldspaceFormId = 0x10 }
                ]
            }
        };

        var assigned = CellLinkageHandler.AssignRuntimeCellMapOwners(cells, runtimeMaps);

        Assert.Equal(1, assigned);
        Assert.Equal(0x10u, cells[0].WorldspaceFormId);
        Assert.Equal("RuntimeCellMap", cells[0].WorldspaceAssignmentSource);
        Assert.Equal(5, cells[0].GridX);
        Assert.Equal(-3, cells[0].GridY);
        Assert.Equal(new uint[] { 0x10 }, cells[0].CandidateWorldspaceFormIds);
    }

    [Fact]
    public void AssignRuntimeCellMapOwners_DoesNotOverrideOwnedSkipsInteriorAndPersistent()
    {
        var cells = new List<CellRecord>
        {
            new() { FormId = 0x1000, WorldspaceFormId = 0x99 }, // already owned
            new() { FormId = 0x1001, Flags = 0x01 }, // interior
            new() { FormId = 0x1002, IsPersistentCell = true }, // persistent
            new() { FormId = 0x1003, GridX = 2, GridY = 2 } // null ws, resolvable
        };
        var runtimeMaps = new Dictionary<uint, RuntimeWorldspaceData>
        {
            [0x10] = new()
            {
                FormId = 0x10,
                Cells =
                [
                    new RuntimeCellMapEntry { CellFormId = 0x1000, GridX = 0, GridY = 0, WorldspaceFormId = 0x10 },
                    new RuntimeCellMapEntry { CellFormId = 0x1001, GridX = 0, GridY = 0, WorldspaceFormId = 0x10 },
                    new RuntimeCellMapEntry { CellFormId = 0x1002, GridX = 0, GridY = 0, WorldspaceFormId = 0x10 },
                    new RuntimeCellMapEntry { CellFormId = 0x1003, GridX = 2, GridY = 2, WorldspaceFormId = 0x10 }
                ]
            }
        };

        var assigned = CellLinkageHandler.AssignRuntimeCellMapOwners(cells, runtimeMaps);

        Assert.Equal(1, assigned);
        Assert.Equal(0x99u, cells[0].WorldspaceFormId); // unchanged
        Assert.Null(cells[1].WorldspaceFormId); // interior skipped
        Assert.Null(cells[2].WorldspaceFormId); // persistent skipped
        Assert.Equal(0x10u, cells[3].WorldspaceFormId); // resolved, grid preserved
        Assert.Equal(2, cells[3].GridX);
    }

    [Fact]
    public void NormalizeStructurallyInteriorCells_ReclassifiesWorldspaceLessGridLessExteriorCell()
    {
        var cells = new List<CellRecord>
        {
            new() { FormId = 0x000E456B, Flags = 0x52 }, // exterior, no ws, no grid → interior
            new()
            {
                FormId = 0x1001, Flags = 0x52, WorldspaceFormId = 0x10, GridX = 1, GridY = 1
            }, // valid exterior → untouched
            new() { FormId = 0x1002, Flags = 0x53 }, // already interior → untouched
            new() { FormId = 0x1003, Flags = 0x52, GridX = 0, GridY = 0 }, // has grid → untouched
            new() { FormId = 0x1004, Flags = 0x52, IsVirtual = true } // virtual → untouched
        };

        var reclassified = CellLinkageHandler.NormalizeStructurallyInteriorCells(cells);

        Assert.Equal(1, reclassified);
        Assert.True(cells[0].IsInterior);
        Assert.Equal(0x53, cells[0].Flags); // 0x52 | 0x01
        Assert.False(cells[1].IsInterior);
        Assert.True(cells[2].IsInterior);
        Assert.False(cells[3].IsInterior);
        Assert.False(cells[4].IsInterior);
    }

    [Fact]
    public void MergeColocatedVirtualOrphanCells_MovesOrphanRefsIntoNonVirtualKeeper()
    {
        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x0010EC42,
                WorldspaceFormId = 0x10,
                GridX = 0,
                GridY = 0,
                IsPersistentCell = true,
                PlacedObjects = [new PlacedReference { FormId = 0x5000 }]
            },
            new()
            {
                FormId = 0xFE800062,
                WorldspaceFormId = 0x10,
                GridX = 0,
                GridY = 0,
                IsVirtual = true,
                PlacedObjects =
                [
                    new PlacedReference { FormId = 0x5000 }, // duplicate — must not be re-added
                    new PlacedReference { FormId = 0x6001 },
                    new PlacedReference { FormId = 0x6002 }
                ]
            },
            new()
            {
                FormId = 0xFE800099,
                WorldspaceFormId = 0x10,
                GridX = 9,
                GridY = 9, // no co-located keeper → left as-is
                IsVirtual = true,
                PlacedObjects = [new PlacedReference { FormId = 0x7000 }]
            }
        };

        var merged = CellLinkageHandler.MergeColocatedVirtualOrphanCells(cells);

        Assert.Equal(2, merged); // 0x6001, 0x6002 (0x5000 deduped)
        var keeper = Assert.Single(cells, c => c.FormId == 0x0010EC42);
        Assert.Equal(3, keeper.PlacedObjects.Count);
        Assert.Contains(keeper.PlacedObjects, p => p.FormId == 0x6001);
        Assert.Contains(keeper.PlacedObjects, p => p.FormId == 0x6002);
        Assert.DoesNotContain(cells, c => c.FormId == 0xFE800062); // consumed virtual cell removed
        Assert.Contains(cells, c => c.FormId == 0xFE800099); // non-co-located virtual cell retained
    }

    [Fact]
    public void MergeColocatedVirtualOrphanCells_DoesNotMergeIntoMasterKeeper()
    {
        // A master cell (e.g. a live worldspace's persistent cell) must NOT receive orphan refs —
        // merging into it corrupts a real game cell and crashes on GridCellArray attach.
        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x0013B310, // master persistent cell (TheStripWorldNew)
                WorldspaceFormId = 0x0013B308,
                GridX = 0,
                GridY = 0,
                IsPersistentCell = true,
                PlacedObjects = []
            },
            new()
            {
                FormId = 0xFE800050,
                WorldspaceFormId = 0x0013B308,
                GridX = 0,
                GridY = 0,
                IsVirtual = true,
                PlacedObjects = [new PlacedReference { FormId = 0x6001 }, new PlacedReference { FormId = 0x6002 }]
            }
        };
        var masterFormIds = new HashSet<uint> { 0x0013B310 };

        var merged = CellLinkageHandler.MergeColocatedVirtualOrphanCells(cells, masterFormIds);

        Assert.Equal(0, merged);
        var master = Assert.Single(cells, c => c.FormId == 0x0013B310);
        Assert.Empty(master.PlacedObjects); // master cell untouched
        Assert.Contains(cells, c => c.FormId == 0xFE800050); // virtual cell left for emission dedup to drop
    }

    private static WorldspaceRecord CreateWorldspace(
        uint formId,
        string editorId,
        short nwX,
        short nwY,
        short seX,
        short seY)
    {
        return new WorldspaceRecord
        {
            FormId = formId,
            EditorId = editorId,
            MapNWCellX = nwX,
            MapNWCellY = nwY,
            MapSECellX = seX,
            MapSECellY = seY
        };
    }
}