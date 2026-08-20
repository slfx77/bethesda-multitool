using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Builds the file-hierarchy context for a DMP-only CELL. Master-backed cells keep
///     their parsed context; this class owns only contexts that must be reconstructed
///     from the semantic capture.
/// </summary>
public static class CellContextSynthesizer
{
    public static PcEsmCellContext Synthesize(CellCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var cell = entry.DmpModel
                   ?? throw new InvalidOperationException(
                       $"CellCatalogEntry 0x{entry.CellFormId:X8} has neither master context nor DMP model.");

        if (!cell.WorldspaceFormId.HasValue)
        {
            return Create(entry, true, 2, 3);
        }

        // Test this before grid coordinates. Runtime captures of the real persistent
        // container can expose an XCLC, but the file format still places that CELL
        // directly beneath World Children rather than beneath exterior grid GRUPs.
        if (cell.IsPersistentCell)
        {
            return Create(entry, false, 0, 0);
        }

        // Virtual/orphan buckets are removed by PersistentCellReparenting after any
        // rescuable children move out. Preserve their historical label-less context so
        // merely planning the bucket does not invent a real grid location for it.
        if (cell.IsVirtual || cell.IsUnresolvedBucket)
        {
            return Create(entry, false, 4, 5);
        }

        if (cell.GridX is not { } gridX || cell.GridY is not { } gridY)
        {
            throw new InvalidOperationException(
                $"DMP exterior CELL 0x{entry.CellFormId:X8} is not persistent/virtual and has no complete grid coordinates.");
        }

        var blockX = EsmEndianHelpers.FloorDiv(gridX, 32);
        var blockY = EsmEndianHelpers.FloorDiv(gridY, 32);
        var subblockX = EsmEndianHelpers.FloorDiv(gridX, 8);
        var subblockY = EsmEndianHelpers.FloorDiv(gridY, 8);

        return new PcEsmCellContext
        {
            CellFormId = entry.CellFormId,
            IsInterior = false,
            WorldspaceFormId = cell.WorldspaceFormId,
            BlockGroupType = 4,
            SubblockGroupType = 5,
            BlockLabel = EncodeGridLabel(blockX, blockY),
            SubblockLabel = EncodeGridLabel(subblockX, subblockY)
        };
    }

    private static PcEsmCellContext Create(
        CellCatalogEntry entry,
        bool isInterior,
        int blockType,
        int subblockType)
    {
        return new PcEsmCellContext
        {
            CellFormId = entry.CellFormId,
            IsInterior = isInterior,
            WorldspaceFormId = entry.DmpModel!.WorldspaceFormId,
            BlockGroupType = blockType,
            SubblockGroupType = subblockType,
            BlockLabel = null,
            SubblockLabel = null
        };
    }

    private static byte[] EncodeGridLabel(int x, int y)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, EsmEndianHelpers.ComposeGridLabel(x, y));
        return bytes;
    }
}
