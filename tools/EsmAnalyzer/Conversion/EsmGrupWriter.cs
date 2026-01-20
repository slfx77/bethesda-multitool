using System.Buffers.Binary;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using static EsmAnalyzer.Conversion.EsmEndianHelpers;

namespace EsmAnalyzer.Conversion;

/// <summary>
///     Handles writing GRUP structures for world and cell hierarchies.
/// </summary>
public sealed class EsmGrupWriter
{
    private readonly byte[] _input;
    private readonly EsmRecordWriter _recordWriter;
    private readonly EsmConversionStats _stats;

    public EsmGrupWriter(byte[] input, EsmRecordWriter recordWriter, EsmConversionStats stats)
    {
        _input = input;
        _recordWriter = recordWriter;
        _stats = stats;
    }

    /// <summary>
    ///     Writes the contents of the WRLD top-level group with proper hierarchy.
    /// </summary>
    public void WriteWorldGroupContents(ConversionIndex index, BinaryWriter writer)
    {
        if (index.Worlds.Count == 0) return;

        foreach (var world in index.Worlds)
        {
            _recordWriter.WriteRecordToWriter(world.Offset, writer);
            WriteWorldChildrenGroup(world.FormId, index, writer);
        }
    }

    /// <summary>
    ///     Writes the contents of the CELL top-level group (interior cells) with proper hierarchy.
    /// </summary>
    public void WriteCellGroupContents(ConversionIndex index, BinaryWriter writer)
    {
        if (index.InteriorCells.Count == 0) return;

        foreach (var blockGroup in GetInteriorBlockGroups(index.InteriorCells))
            WriteInteriorBlockGroup(blockGroup.Key, blockGroup, index, writer);
    }

    #region Interior Cell Writing

    private static IEnumerable<IGrouping<int, CellEntry>> GetInteriorBlockGroups(IEnumerable<CellEntry> cells)
    {
        return cells
            .GroupBy(c => (int)((c.FormId & 0xFFF) % 10))
            .OrderBy(g => g.Key);
    }

    private void WriteInteriorBlockGroup(int blockId, IEnumerable<CellEntry> cells, ConversionIndex index,
        BinaryWriter writer)
    {
        WriteGrupWithContents(writer, 2, (uint)blockId, 0, 0, () =>
        {
            foreach (var subBlockGroup in GetInteriorSubBlockGroups(cells))
                WriteInteriorSubBlockGroup(subBlockGroup.Key, subBlockGroup, index, writer);
        });
    }

    private static IEnumerable<IGrouping<int, CellEntry>> GetInteriorSubBlockGroups(IEnumerable<CellEntry> cells)
    {
        return cells
            .GroupBy(c => (int)(c.FormId % 10))
            .OrderBy(g => g.Key);
    }

    private void WriteInteriorSubBlockGroup(int subBlockId, IEnumerable<CellEntry> cells, ConversionIndex index,
        BinaryWriter writer)
    {
        WriteGrupWithContents(writer, 3, (uint)subBlockId, 0, 0, () =>
        {
            foreach (var cell in cells.OrderBy(c => c.FormId))
            {
                _recordWriter.WriteRecordToWriter(cell.Offset, writer);
                WriteCellChildren(cell.FormId, index, writer);
            }
        });
    }

    #endregion

    #region Exterior Cell Writing

    private void WriteWorldChildrenGroup(uint worldFormId, ConversionIndex index, BinaryWriter writer)
    {
        if (!index.ExteriorCellsByWorld.TryGetValue(worldFormId, out var cells) || cells.Count == 0) return;

        WriteGrupWithContents(writer, 1, worldFormId, 0, 0, () =>
        {
            foreach (var blockGroup in GetExteriorBlockGroups(cells))
                WriteExteriorBlockGroup(blockGroup.Key.BlockX, blockGroup.Key.BlockY, blockGroup, index, writer);
        });
    }

    private static IEnumerable<IGrouping<(int BlockX, int BlockY), CellEntry>> GetExteriorBlockGroups(
        IEnumerable<CellEntry> cells)
    {
        return cells
            .Where(c => c.GridX.HasValue && c.GridY.HasValue)
            .GroupBy(c => (BlockX: FloorDiv(c.GridX!.Value, 32), BlockY: FloorDiv(c.GridY!.Value, 32)))
            .OrderBy(g => g.Key.BlockY)
            .ThenBy(g => g.Key.BlockX);
    }

    private void WriteExteriorBlockGroup(int blockX, int blockY, IEnumerable<CellEntry> cells, ConversionIndex index,
        BinaryWriter writer)
    {
        var blockLabel = ComposeGridLabel(blockX, blockY);
        WriteGrupWithContents(writer, 4, blockLabel, 0, 0, () =>
        {
            foreach (var subBlockGroup in GetExteriorSubBlockGroups(cells))
                WriteExteriorSubBlockGroup(subBlockGroup.Key.SubX, subBlockGroup.Key.SubY, subBlockGroup, index,
                    writer);
        });
    }

    private static IEnumerable<IGrouping<(int SubX, int SubY), CellEntry>> GetExteriorSubBlockGroups(
        IEnumerable<CellEntry> cells)
    {
        return cells
            .GroupBy(c => (SubX: FloorDiv(c.GridX!.Value, 8), SubY: FloorDiv(c.GridY!.Value, 8)))
            .OrderBy(g => g.Key.SubY)
            .ThenBy(g => g.Key.SubX);
    }

    private void WriteExteriorSubBlockGroup(int subX, int subY, IEnumerable<CellEntry> cells, ConversionIndex index,
        BinaryWriter writer)
    {
        var subBlockLabel = ComposeGridLabel(subX, subY);
        WriteGrupWithContents(writer, 5, subBlockLabel, 0, 0, () =>
        {
            foreach (var cell in cells.OrderBy(c => c.GridY).ThenBy(c => c.GridX).ThenBy(c => c.FormId))
            {
                _recordWriter.WriteRecordToWriter(cell.Offset, writer);
                WriteCellChildren(cell.FormId, index, writer);
            }
        });
    }

    #endregion

    #region Cell Children Writing

    private void WriteCellChildren(uint cellFormId, ConversionIndex index, BinaryWriter writer)
    {
        var hasPersistent = index.CellChildGroups.TryGetValue((cellFormId, 8), out var persistentGroups);
        var hasTemporary = index.CellChildGroups.TryGetValue((cellFormId, 9), out var temporaryGroups);
        var hasVwd = index.CellChildGroups.TryGetValue((cellFormId, 10), out var vwdGroups);

        if (!hasPersistent && !hasTemporary && !hasVwd) return;

        WriteGrupWithContents(writer, 6, cellFormId, 0, 0, () =>
        {
            if (hasPersistent && persistentGroups != null)
                WriteMergedChildGroup(cellFormId, 8, persistentGroups, writer);

            if (hasTemporary && temporaryGroups != null) WriteMergedChildGroup(cellFormId, 9, temporaryGroups, writer);

            if (hasVwd && vwdGroups != null) WriteMergedChildGroup(cellFormId, 10, vwdGroups, writer);
        });
    }

    private void WriteMergedChildGroup(uint cellFormId, int grupType, List<GrupEntry> groups, BinaryWriter writer)
    {
        WriteGrupWithContents(writer, grupType, cellFormId, 0, 0, () =>
        {
            foreach (var group in groups.OrderBy(g => g.Offset))
            {
                var start = group.Offset + EsmParser.MainRecordHeaderSize;
                var end = group.Offset + group.Size;
                if (start >= end || end > _input.Length) continue;

                var buffer = ConvertRangeToBuffer(start, end, writer);
                writer.Write(buffer);
            }
        });
    }

    #endregion

    #region GRUP Helpers

    private void WriteGrupWithContents(BinaryWriter writer, int grupType, uint labelValue, uint stamp, uint unknown,
        Action writeContents)
    {
        var headerPos = writer.BaseStream.Position;
        writer.Write("GRUP"u8);
        writer.Write(0u); // Placeholder for size
        writer.Write(labelValue);
        writer.Write((uint)grupType);
        writer.Write(stamp);
        writer.Write(unknown);
        _stats.GrupsConverted++;

        writeContents();

        FinalizeGrupHeader(writer, headerPos);
    }

    private static void FinalizeGrupHeader(BinaryWriter writer, long headerPosition)
    {
        var currentPosition = writer.BaseStream.Position;
        var actualGrupSize = (uint)(currentPosition - headerPosition);

        writer.BaseStream.Position = headerPosition + 4;
        writer.Write(actualGrupSize);
        writer.BaseStream.Position = currentPosition;
    }

    /// <summary>
    ///     Writes a GRUP header and returns the header position for later size finalization.
    /// </summary>
    public long WriteGrupHeader(BinaryWriter writer, int grupType, byte[] labelBytes, uint stamp, uint unknown)
    {
        var headerPos = writer.BaseStream.Position;
        writer.Write("GRUP"u8);
        writer.Write(0u); // Placeholder for size

        if (grupType == 0)
        {
            // Top-level group: reverse signature bytes
            writer.Write(labelBytes[3]);
            writer.Write(labelBytes[2]);
            writer.Write(labelBytes[1]);
            writer.Write(labelBytes[0]);
        }
        else
        {
            // Other groups: label is a value (FormID or grid coords)
            var labelValue = BinaryPrimitives.ReadUInt32BigEndian(labelBytes);
            writer.Write(labelValue);
        }

        writer.Write((uint)grupType);
        writer.Write(stamp);
        writer.Write(unknown);
        _stats.GrupsConverted++;

        return headerPos;
    }

    /// <summary>
    ///     Finalizes a GRUP header by writing the actual size.
    /// </summary>
    public void FinalizeGrup(BinaryWriter writer, long headerPosition)
    {
        FinalizeGrupHeader(writer, headerPosition);
    }

    #endregion

    #region Range Conversion

    private byte[] ConvertRangeToBuffer(int startOffset, int endOffset, BinaryWriter targetWriter)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        ConvertRangeToWriter(startOffset, endOffset, writer);

        return stream.ToArray();
    }

    private void ConvertRangeToWriter(int startOffset, int endOffset, BinaryWriter writer)
    {
        const int grupHeaderSize = 24;
        var grupStack = new Stack<(long headerPos, int end)>();
        var offset = startOffset;

        while (offset < endOffset)
        {
            FinalizeCompletedRangeGroups(grupStack, writer, offset);

            if (!TryWriteGroupInRange(writer, grupStack, ref offset, endOffset, grupHeaderSize) &&
                !TryWriteRecordInRange(writer, ref offset, endOffset))
                break;
        }

        while (grupStack.Count > 0)
        {
            var (headerPos, _) = grupStack.Pop();
            FinalizeGrupHeader(writer, headerPos);
        }
    }

    private static void FinalizeCompletedRangeGroups(Stack<(long headerPos, int end)> grupStack, BinaryWriter writer,
        int offset)
    {
        while (grupStack.Count > 0 && offset >= grupStack.Peek().end)
        {
            var (headerPos, _) = grupStack.Pop();
            FinalizeGrupHeader(writer, headerPos);
        }
    }

    private bool TryWriteGroupInRange(BinaryWriter writer, Stack<(long headerPos, int end)> grupStack, ref int offset,
        int endOffset, int grupHeaderSize)
    {
        if (offset + 4 > endOffset || offset + 4 > _input.Length) return false;

        var sigBytes = _input.AsSpan(offset, 4);
        var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";
        if (signature != "GRUP") return false;

        if (offset + grupHeaderSize > endOffset || offset + grupHeaderSize > _input.Length) return false;

        var grupSize = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 4));
        var labelBytes = _input.AsSpan(offset + 8, 4).ToArray();
        var grupType = (int)BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 12));
        var stamp = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 16));
        var unknown = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 20));
        var grupEnd = offset + (int)grupSize;

        var headerPos = WriteGrupHeader(writer, grupType, labelBytes, stamp, unknown);
        grupStack.Push((headerPos, grupEnd));
        offset += grupHeaderSize;
        return true;
    }

    private bool TryWriteRecordInRange(BinaryWriter writer, ref int offset, int endOffset)
    {
        var buffer = _recordWriter.ConvertRecordToBuffer(offset, out var recordEnd, out _);
        if (buffer != null) writer.Write(buffer);

        if (recordEnd <= offset) return false;

        offset = Math.Min(recordEnd, endOffset);
        return true;
    }

    #endregion
}