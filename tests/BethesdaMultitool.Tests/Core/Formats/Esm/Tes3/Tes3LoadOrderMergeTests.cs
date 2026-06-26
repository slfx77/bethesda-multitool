using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Tes3;
using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tes3;

/// <summary>
///     Morrowind (TES3) plugins carry no real FormIDs, so each is parsed with file-local synthetic ones
///     that collide across plugins, and each synthesizes its own exterior worldspace. These tests cover
///     the load-order merge fix: a shared exterior worldspace FormID (so every plugin's exterior cells
///     fold into one worldspace) plus per-plugin namespacing (so unrelated records don't overwrite each
///     other), matching how Morrowind.esm + Tribunal.esm + Bloodmoon.esm should combine.
/// </summary>
public class Tes3LoadOrderMergeTests
{
    [Fact]
    public void Namespace_LeavesSharedIdsButStampsPerPluginRecords()
    {
        // The shared exterior worldspace and the land-texture palette (0xFF-prefixed) must survive
        // unchanged so the merge folds them; everything else is stamped with the load index.
        Assert.Equal(
            WorldspaceRecord.Tes3SyntheticExteriorFormId,
            Tes3FormIdScheme.Namespace(WorldspaceRecord.Tes3SyntheticExteriorFormId, 5));
        Assert.Equal(
            Tes3FormIdScheme.LtexFormIdBase + 3,
            Tes3FormIdScheme.Namespace(Tes3FormIdScheme.LtexFormIdBase + 3, 5));
        Assert.Equal(0x05000001u, Tes3FormIdScheme.Namespace(0x00000001u, 5));
        Assert.Equal(0u, Tes3FormIdScheme.Namespace(0u, 5));
    }

    [Fact]
    public void Namespacer_IsNoOpForNonTes3AndIndexZero()
    {
        var nonTes3 = new RecordCollection { IsTes3 = false, Cells = [ExteriorCell(1, 0, 0)] };
        Assert.Same(nonTes3, Tes3LoadOrderNamespacer.Namespaced(nonTes3, 3));

        var basePlugin = MakeTes3Plugin(ExteriorCell(1, 0, 0));
        Assert.Same(basePlugin, Tes3LoadOrderNamespacer.Namespaced(basePlugin, 0));
    }

    [Fact]
    public void TwoPlugins_FoldIntoOneWorldspace_WithoutCollidingCells()
    {
        // Both plugins independently number cells 1,2 (file-local) — without namespacing the merge would
        // treat plugin B's cells as overrides of plugin A's and drop two cells.
        var vvardenfell = MakeTes3Plugin(ExteriorCell(1, 0, 0), ExteriorCell(2, 1, 0));
        var solstheim = MakeTes3Plugin(ExteriorCell(1, 20, 20), ExteriorCell(2, 21, 20));

        var a = Tes3LoadOrderNamespacer.Namespaced(vvardenfell, 0); // base (unstamped)
        var b = Tes3LoadOrderNamespacer.Namespaced(solstheim, 1); // stamped 0x01
        var merged = a.MergeWith(b).RelinkWorldspaceCells();

        var ws = Assert.Single(merged.Worldspaces);
        Assert.Equal(WorldspaceRecord.Tes3SyntheticExteriorFormId, ws.FormId);
        Assert.Equal(4, ws.Cells.Count); // all four cells survive; nothing overwritten

        // Bounds span both plugins (X 0..21, Y 0..20). Morrowind map is +Y north: NW=(minX,maxY),
        // SE=(maxX,minY).
        Assert.Equal((short)0, ws.MapNWCellX);
        Assert.Equal((short)20, ws.MapNWCellY);
        Assert.Equal((short)21, ws.MapSECellX);
        Assert.Equal((short)0, ws.MapSECellY);
    }

    [Fact]
    public void WithoutNamespacing_CollidingCellsAreLost()
    {
        // Guards the regression: merging two TES3 plugins on raw file-local FormIDs (no namespacing)
        // drops the colliding cells — this is the bug the namespacer fixes.
        var a = MakeTes3Plugin(ExteriorCell(1, 0, 0), ExteriorCell(2, 1, 0));
        var b = MakeTes3Plugin(ExteriorCell(1, 20, 20), ExteriorCell(2, 21, 20));

        var merged = a.MergeWith(b).RelinkWorldspaceCells();

        Assert.Equal(2, Assert.Single(merged.Worldspaces).Cells.Count); // collision overwrote two cells
    }

    private static RecordCollection MakeTes3Plugin(params CellRecord[] cells)
    {
        var cellList = cells.ToList();
        return new RecordCollection
        {
            IsTes3 = true,
            Cells = cellList,
            Worldspaces = Tes3RecordParser.BuildWorldspaces(cellList, WorldspaceRecord.Tes3SyntheticExteriorFormId)
        };
    }

    private static CellRecord ExteriorCell(uint formId, int gridX, int gridY) => new()
    {
        FormId = formId,
        GridX = gridX,
        GridY = gridY,
        Flags = 0x00,
        WorldspaceFormId = WorldspaceRecord.Tes3SyntheticExteriorFormId,
        CellWorldSize = 8192f
    };
}
