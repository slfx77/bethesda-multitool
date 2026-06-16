using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Models;

/// <summary>
///     Covers the merge/relink/filter helpers the world viewer relies on to combine a primary file
///     with Load-Order plugins (bugs: ESP edits not taking effect, DMP worldspace leakage).
/// </summary>
public class RecordCollectionWorldspaceLinkTests
{
    private const uint Worldspace1 = 0x1;
    private const uint Worldspace2 = 0x2;

    private static CellRecord ExteriorCell(uint formId, uint worldspaceFormId, int gridX, int gridY, int objectCount)
    {
        var placed = new List<PlacedReference>();
        for (var i = 0; i < objectCount; i++)
        {
            placed.Add(new PlacedReference { FormId = formId * 100 + (uint)i });
        }

        return new CellRecord
        {
            FormId = formId,
            WorldspaceFormId = worldspaceFormId,
            GridX = gridX,
            GridY = gridY,
            PlacedObjects = placed
        };
    }

    [Fact]
    public void MergeWith_OverlayWins_ForSharedCellFormId()
    {
        var baseCell = ExteriorCell(0x10, Worldspace1, 0, 0, objectCount: 2);
        var overlayCell = ExteriorCell(0x10, Worldspace1, 0, 0, objectCount: 5);

        var baseRc = new RecordCollection { Cells = [baseCell] };
        var overlayRc = new RecordCollection { Cells = [overlayCell] };

        var merged = baseRc.MergeWith(overlayRc);

        var cell = Assert.Single(merged.Cells);
        Assert.Equal(5, cell.PlacedObjects.Count); // overlay (later plugin) wins
    }

    [Fact]
    public void RelinkWorldspaceCells_SurfacesOverriddenCellThroughWorldspace()
    {
        // Primary worldspace links the pre-merge cell (2 objects); the overlay overrides that cell
        // FormID with 5 objects. Without re-link, ws.Cells still points at the stale 2-object cell.
        var staleCell = ExteriorCell(0x10, Worldspace1, 0, 0, objectCount: 2);
        var ws = new WorldspaceRecord { FormId = Worldspace1, Cells = [staleCell] };
        var baseRc = new RecordCollection { Worldspaces = [ws], Cells = [staleCell] };

        var overlayCell = ExteriorCell(0x10, Worldspace1, 0, 0, objectCount: 5);
        var overlayRc = new RecordCollection { Cells = [overlayCell] };

        var merged = baseRc.MergeWith(overlayRc).RelinkWorldspaceCells();

        var linkedCell = Assert.Single(merged.Worldspaces[0].Cells);
        Assert.Equal(5, linkedCell.PlacedObjects.Count);
    }

    [Fact]
    public void RelinkWorldspaceCells_AddsNewCellToExistingWorldspace()
    {
        var existing = ExteriorCell(0x10, Worldspace1, 0, 0, objectCount: 1);
        var ws = new WorldspaceRecord { FormId = Worldspace1, Cells = [existing] };
        var baseRc = new RecordCollection { Worldspaces = [ws], Cells = [existing] };

        var added = ExteriorCell(0x11, Worldspace1, 1, 0, objectCount: 1);
        var overlayRc = new RecordCollection { Cells = [added] };

        var merged = baseRc.MergeWith(overlayRc).RelinkWorldspaceCells();

        Assert.Equal(2, merged.Worldspaces[0].Cells.Count);
        Assert.Contains(merged.Worldspaces[0].Cells, c => c.FormId == 0x11);
    }

    [Fact]
    public void RelinkWorldspaceCells_DropsStaleCellWhenNoLongerInWorldspace()
    {
        // A worldspace whose only cell is removed from the merged set must not keep the stale link.
        var stale = ExteriorCell(0x10, Worldspace1, 0, 0, objectCount: 1);
        var ws = new WorldspaceRecord { FormId = Worldspace1, Cells = [stale] };
        var rc = new RecordCollection { Worldspaces = [ws], Cells = [] };

        rc.RelinkWorldspaceCells();

        Assert.Empty(rc.Worldspaces[0].Cells);
    }

    [Fact]
    public void WithWorldspacesFilteredTo_KeepsOnlyRequestedWorldspaces()
    {
        var ws1 = new WorldspaceRecord { FormId = Worldspace1 };
        var ws2 = new WorldspaceRecord { FormId = Worldspace2 };
        var cell = ExteriorCell(0x10, Worldspace2, 0, 0, objectCount: 1);
        var rc = new RecordCollection { Worldspaces = [ws1, ws2], Cells = [cell] };

        var filtered = rc.WithWorldspacesFilteredTo(new HashSet<uint> { Worldspace1 });

        var keptWs = Assert.Single(filtered.Worldspaces);
        Assert.Equal(Worldspace1, keptWs.FormId);
        Assert.Single(filtered.Cells); // cells untouched, only the worldspace list is scoped
    }

    [Fact]
    public void WithWorldspacesFilteredTo_ReturnsSelf_WhenAllKept()
    {
        var ws1 = new WorldspaceRecord { FormId = Worldspace1 };
        var rc = new RecordCollection { Worldspaces = [ws1] };

        var filtered = rc.WithWorldspacesFilteredTo(new HashSet<uint> { Worldspace1, Worldspace2 });

        Assert.Same(rc, filtered);
    }
}
