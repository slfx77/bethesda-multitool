using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

/// <summary>
///     Pins the load-order WRLD merge: worldspace overrides are ADDITIVE for cell children. A DLC's
///     Commonwealth override carries only its own added cells; replacing the base record wholesale
///     (the generic MergeList semantics) shrank the merged Commonwealth from 36,864 cells to the
///     DLC's 72 — a near-empty worldspace on any merged FO4 Data-dir load.
/// </summary>
public sealed class WorldspaceMergeTests
{
    private static CellRecord Cell(uint formId, int gx, int gy)
    {
        return new CellRecord { FormId = formId, GridX = gx, GridY = gy };
    }

    private static WorldspaceRecord Worldspace(uint formId, string editorId, params CellRecord[] cells)
    {
        return new WorldspaceRecord { FormId = formId, EditorId = editorId, Cells = [.. cells] };
    }

    [Fact]
    public void MergeWith_WorldspaceOverride_UnionsCellChildren()
    {
        var baseRecords = new RecordCollection
        {
            Worldspaces = [Worldspace(0x3C, "Commonwealth", Cell(0x100, 0, 0), Cell(0x101, 1, 0))]
        };
        // The DLC override: one colliding cell (its version must win) + one DLC-only cell.
        var overlay = new RecordCollection
        {
            Worldspaces =
            [
                Worldspace(0x3C, "Commonwealth", Cell(0x101, 1, 0) with { EditorId = "DlcOverride" }, Cell(0x200, 5, 5))
            ]
        };

        var merged = baseRecords.MergeWith(overlay);

        var ws = Assert.Single(merged.Worldspaces);
        Assert.Equal(3, ws.Cells.Count);
        Assert.Contains(ws.Cells, c => c.FormId == 0x100);
        Assert.Contains(ws.Cells, c => c.FormId == 0x200);
        Assert.Equal("DlcOverride", ws.Cells.Single(c => c.FormId == 0x101).EditorId);
    }

    [Fact]
    public void MergeWith_WorldspacesWithoutCollision_KeepBothIntact()
    {
        var baseRecords = new RecordCollection
        {
            Worldspaces = [Worldspace(0x3C, "Commonwealth", Cell(0x100, 0, 0))]
        };
        var overlay = new RecordCollection
        {
            Worldspaces = [Worldspace(0xF94, "DiamondCity", Cell(0x300, 2, 2))]
        };

        var merged = baseRecords.MergeWith(overlay);

        Assert.Equal(2, merged.Worldspaces.Count);
        Assert.Single(merged.Worldspaces.Single(w => w.FormId == 0x3C).Cells);
        Assert.Single(merged.Worldspaces.Single(w => w.FormId == 0xF94).Cells);
    }
}