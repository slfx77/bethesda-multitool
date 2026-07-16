using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

/// <summary>
///     Engine load-order CELL merge semantics: an override record in a later file replaces header
///     fields but its CHILDREN (placed references, LAND) merge with the base file's. FO4's DLCs
///     re-ship thousands of Commonwealth CELL headers (precombine regeneration) with no LAND and no
///     base REFRs — whole-record replacement erased downtown Boston when all DLC ESMs were loaded.
/// </summary>
public class RecordCollectionCellMergeTests
{
    private const uint CommonwealthWs = 0x3C;

    private static LandHeightmap Land(float offset = 1000f) => new()
    {
        HeightOffset = offset,
        HeightDeltas = new sbyte[33 * 33]
    };

    private static CellRecord Cell(
        uint formId,
        LandHeightmap? land,
        params PlacedReference[] refs) => new()
    {
        FormId = formId,
        WorldspaceFormId = CommonwealthWs,
        GridX = 1,
        GridY = 2,
        Heightmap = land,
        PlacedObjects = [.. refs]
    };

    private static PlacedReference Ref(uint formId, uint baseFormId) => new()
    {
        FormId = formId,
        BaseFormId = baseFormId
    };

    [Fact]
    public void MergeWith_LandlessCellOverride_KeepsBaseLandAndRefs()
    {
        // Base cell: terrain + two refs. DLC override: no LAND, one added ref, one re-shipped ref.
        var baseCell = Cell(0x10, Land(), Ref(0x100, 0xA), Ref(0x101, 0xB));
        var overrideCell = Cell(0x10, land: null, Ref(0x101, 0xBB), Ref(0x900, 0xC));

        var merged = new RecordCollection { Cells = [baseCell] }
            .MergeWith(new RecordCollection { Cells = [overrideCell] });

        var cell = Assert.Single(merged.Cells);
        Assert.NotNull(cell.Heightmap);                       // base LAND survives
        Assert.Equal(3, cell.PlacedObjects.Count);            // 0x100 kept, 0x101 overridden, 0x900 added
        Assert.Equal(0xAu, cell.PlacedObjects.Single(r => r.FormId == 0x100).BaseFormId);
        Assert.Equal(0xBBu, cell.PlacedObjects.Single(r => r.FormId == 0x101).BaseFormId);
        Assert.Contains(cell.PlacedObjects, r => r.FormId == 0x900);
    }

    [Fact]
    public void MergeWith_CellOverrideWithLand_OverrideLandWins()
    {
        var baseCell = Cell(0x10, Land(offset: 1000f));
        var overrideCell = Cell(0x10, Land(offset: 2000f));

        var merged = new RecordCollection { Cells = [baseCell] }
            .MergeWith(new RecordCollection { Cells = [overrideCell] });

        Assert.Equal(2000f, Assert.Single(merged.Cells).Heightmap!.HeightOffset);
    }

    [Fact]
    public void MergeWith_CellOverride_BackfillsMissingHeaderFields()
    {
        var baseCell = Cell(0x10, Land()) with
        {
            EditorId = "BaseCell",
            WaterHeight = 5f,
            ClimateFormId = 0x1234,
        };
        var overrideCell = new CellRecord { FormId = 0x10, FullName = "Renamed" };

        var merged = new RecordCollection { Cells = [baseCell] }
            .MergeWith(new RecordCollection { Cells = [overrideCell] });

        var cell = Assert.Single(merged.Cells);
        Assert.Equal("Renamed", cell.FullName);               // override header field wins
        Assert.Equal("BaseCell", cell.EditorId);              // absent fields backfill from base
        Assert.Equal(5f, cell.WaterHeight);
        Assert.Equal(0x1234u, cell.ClimateFormId);
        Assert.Equal(CommonwealthWs, cell.WorldspaceFormId);
        Assert.Equal(1, cell.GridX);
    }

    [Fact]
    public void MergeWorldspaces_FoldsOverriddenCellChildren_WithoutRelink()
    {
        // The renderer-profiler path reads ws.Cells directly (it never calls RelinkWorldspaceCells),
        // so the worldspace-level cell stitch must apply the same child merge.
        var baseCell = Cell(0x10, Land(), Ref(0x100, 0xA));
        var baseWs = new WorldspaceRecord { FormId = CommonwealthWs, Cells = [baseCell] };
        var baseRc = new RecordCollection { Worldspaces = [baseWs], Cells = [baseCell] };

        var overrideCell = Cell(0x10, land: null);
        var overlayWs = new WorldspaceRecord { FormId = CommonwealthWs, Cells = [overrideCell] };
        var overlayRc = new RecordCollection { Worldspaces = [overlayWs], Cells = [overrideCell] };

        var merged = baseRc.MergeWith(overlayRc);

        var wsCell = Assert.Single(merged.Worldspaces.Single().Cells);
        Assert.NotNull(wsCell.Heightmap);
        Assert.Single(wsCell.PlacedObjects);
    }
}

/// <summary>
///     TES4-family load-order FormID rebasing: inside a plugin, a FormID's high byte indexes the
///     file's OWN master list, so every DLC's new records ship raw 0x01-prefixed and would collide
///     across DLCs in a FormID-keyed merge. The mapper folds each file's local indices into shared
///     load-order slots (overrides keep targeting their master; own records get a disjoint block).
/// </summary>
public class Tes4LoadOrderFormIdMapperTests
{
    private static RecordCollection CellCollection(uint cellFormId, uint worldspaceFormId) => new()
    {
        Cells =
        [
            new CellRecord
            {
                FormId = cellFormId,
                WorldspaceFormId = worldspaceFormId,
                PlacedObjects = [new PlacedReference { FormId = cellFormId + 1, BaseFormId = 0x00000A00 }]
            }
        ]
    };

    private static Func<string, IReadOnlyList<string>> Masters(Dictionary<string, string[]> byName) =>
        path => byName.TryGetValue(Path.GetFileName(path), out var masters) ? masters : [];

    [Fact]
    public void TryCreate_SingleSourceWithoutPrimary_ReturnsNull()
    {
        Assert.Null(Tes4LoadOrderFormIdMapper.TryCreate(["Fallout4.esm"]));
    }

    [Fact]
    public void Namespaced_BaseMaster_IsIdentity()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["Fallout4.esm", "DLCCoast.esm"],
            mastersReader: Masters(new Dictionary<string, string[]>
            {
                ["Fallout4.esm"] = [],
                ["DLCCoast.esm"] = ["Fallout4.esm"]
            }))!;

        var records = CellCollection(0x00000FC9, 0x3C);
        Assert.Same(records, mapper.Namespaced(records, "Fallout4.esm"));
    }

    [Fact]
    public void Namespaced_SecondDlc_MovesOwnRecordsToItsSlot_AndKeepsOverridesOnMaster()
    {
        var mastersReader = Masters(new Dictionary<string, string[]>
        {
            ["Fallout4.esm"] = [],
            ["DLCCoast.esm"] = ["Fallout4.esm"],
            ["DLCNukaWorld.esm"] = ["Fallout4.esm"]
        });
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["Fallout4.esm", "DLCCoast.esm", "DLCNukaWorld.esm"], mastersReader: mastersReader)!;

        // DLCCoast at slot 1: local 0x01 (own) already equals its slot → identity.
        var coast = CellCollection(0x01000B0F, 0x01000B0F);
        Assert.Same(coast, mapper.Namespaced(coast, "DLCCoast.esm"));

        // DLCNukaWorld at slot 2: own records move 0x01 → 0x02, master references stay 0x00.
        var nuka = new RecordCollection
        {
            Cells =
            [
                new CellRecord // its own new cell in its own worldspace
                {
                    FormId = 0x01000FEF,
                    WorldspaceFormId = 0x01000FE0,
                    PlacedObjects =
                    [
                        new PlacedReference { FormId = 0x01000FF0, BaseFormId = 0x00000A00 }
                    ]
                },
                new CellRecord { FormId = 0x00000FC9, WorldspaceFormId = 0x3C } // Commonwealth override
            ]
        };

        var rebased = mapper.Namespaced(nuka, "DLCNukaWorld.esm");
        Assert.NotSame(nuka, rebased);
        var own = rebased.Cells.Single(c => (c.FormId >> 24) == 0x02);
        Assert.Equal(0x02000FEFu, own.FormId);
        Assert.Equal(0x02000FE0u, own.WorldspaceFormId);
        var ownRef = Assert.Single(own.PlacedObjects);
        Assert.Equal(0x02000FF0u, ownRef.FormId);
        Assert.Equal(0x00000A00u, ownRef.BaseFormId); // master reference untouched
        Assert.Contains(rebased.Cells, c => c.FormId == 0x00000FC9); // override still targets the base
    }

    [Fact]
    public void Namespaced_PrimaryFileOccupiesSlotZero()
    {
        // Supplementary set [DLCCoast] with primary Fallout4.esm: Coast lands at slot 1, and its
        // master reference resolves to the primary's slot 0 — overrides keep folding onto the base.
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["DLCCoast.esm"],
            primaryFileName: "Fallout4.esm",
            mastersReader: Masters(new Dictionary<string, string[]>
            {
                ["DLCCoast.esm"] = ["Fallout4.esm"]
            }))!;

        var records = CellCollection(0x00000FC9, 0x3C);
        Assert.Same(records, mapper.Namespaced(records, "DLCCoast.esm")); // identity: 0→0, own 1→1
    }

    [Fact]
    public void Namespaced_MissingMaster_GetsStableSharedSlot()
    {
        var mastersReader = Masters(new Dictionary<string, string[]>
        {
            ["A.esm"] = [],
            ["B.esm"] = ["Missing.esm"],
            ["C.esm"] = ["Missing.esm"]
        });
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["A.esm", "B.esm", "C.esm"], mastersReader: mastersReader)!;

        // Both B and C reference records of the absent master; the synthetic slot must match so
        // their overrides of it still fold together (slot 3 = first past the real files).
        var b = mapper.Namespaced(CellCollection(0x00001111, 0x3C), "B.esm");
        var c = mapper.Namespaced(CellCollection(0x00001111, 0x3C), "C.esm");
        Assert.Equal(0x03001111u, b.Cells[0].FormId);
        Assert.Equal(0x03001111u, c.Cells[0].FormId);
    }

    [Fact]
    public void Namespaced_NullAndSentinelFormIds_PassThrough()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["Fallout4.esm", "DLCNukaWorld.esm", "X.esm"],
            mastersReader: Masters(new Dictionary<string, string[]>
            {
                ["Fallout4.esm"] = [],
                ["DLCNukaWorld.esm"] = ["Fallout4.esm"],
                ["X.esm"] = ["Fallout4.esm"]
            }))!;

        var records = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x01001234,
                    PlacedObjects =
                    [
                        new PlacedReference { FormId = 0x01005678, BaseFormId = 0 },
                        new PlacedReference { FormId = 0xFFFFFFFF, BaseFormId = 0x0100AAAA }
                    ]
                }
            ]
        };

        var rebased = mapper.Namespaced(records, "X.esm");
        var cell = Assert.Single(rebased.Cells);
        Assert.Equal(0x02001234u, cell.FormId);
        Assert.Equal(0u, cell.PlacedObjects[0].BaseFormId);          // null ref untouched
        Assert.Equal(0xFFFFFFFFu, cell.PlacedObjects[1].FormId);     // sentinel untouched
        Assert.Equal(0x0200AAAAu, cell.PlacedObjects[1].BaseFormId); // own-record ref moved with the file
    }

    [Fact]
    public void Namespaced_NonPluginSource_IsIdentity()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["Fallout4.esm", "capture.dmp"],
            mastersReader: Masters(new Dictionary<string, string[]> { ["Fallout4.esm"] = [] }))!;

        var records = CellCollection(0x01001234, 0x3C);
        Assert.Same(records, mapper.Namespaced(records, "capture.dmp"));
    }
}
