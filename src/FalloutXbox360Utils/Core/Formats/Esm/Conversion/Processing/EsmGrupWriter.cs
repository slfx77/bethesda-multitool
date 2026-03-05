using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

/// <summary>
///     Handles writing GRUP structures for world and cell hierarchies.
///     Exterior cell writing is delegated to <see cref="ExteriorCellWriter" />.
///     Spiral ordering is provided by <see cref="CellSpiralOrderGenerator" />.
/// </summary>
public sealed class EsmGrupWriter(byte[] input, EsmRecordWriter recordWriter, EsmConversionStats stats)
{
    private readonly byte[] _input = input;
    private readonly EsmRecordWriter _recordWriter = recordWriter;
    private readonly EsmConversionStats _stats = stats;
    private ExteriorCellWriter? _exteriorCellWriter;

    private ExteriorCellWriter ExteriorWriter =>
        _exteriorCellWriter ??= new ExteriorCellWriter(this, _recordWriter);

    /// <summary>
    ///     Writes the contents of the WRLD top-level group with proper hierarchy.
    /// </summary>
    public void WriteWorldGroupContents(ConversionIndex index, BinaryWriter writer)
    {
        if (index.Worlds.Count == 0)
        {
            return;
        }

        foreach (var world in index.Worlds)
        {
            _recordWriter.WriteRecordToWriter(world.Offset, writer);
            ExteriorWriter.WriteWorldChildrenGroup(world.FormId, index, writer);
        }
    }

    /// <summary>
    ///     Writes the contents of the CELL top-level group (interior cells) with proper hierarchy.
    /// </summary>
    public void WriteCellGroupContents(ConversionIndex index, BinaryWriter writer)
    {
        if (index.InteriorCells.Count == 0)
        {
            return;
        }

        foreach (var blockGroup in GetInteriorBlockGroups(index.InteriorCells))
        {
            WriteInteriorBlockGroup(blockGroup.Key, blockGroup, index, writer);
        }
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
            {
                WriteInteriorSubBlockGroup(subBlockGroup.Key, subBlockGroup, index, writer);
            }
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

    #region Cell Children Writing

    internal void WriteCellChildren(uint cellFormId, ConversionIndex index, BinaryWriter writer)
    {
        var hasPersistent = index.CellChildGroups.TryGetValue((cellFormId, 8), out var persistentGroups);
        var hasTemporary = index.CellChildGroups.TryGetValue((cellFormId, 9), out var temporaryGroups);
        var hasVwd = index.CellChildGroups.TryGetValue((cellFormId, 10), out var vwdGroups);

        if (!hasPersistent && !hasTemporary && !hasVwd)
        {
            return;
        }

        WriteGrupWithContents(writer, 6, cellFormId, 0, 0, () =>
        {
            if (hasPersistent && persistentGroups != null)
            {
                WriteMergedChildGroup(cellFormId, 8, persistentGroups, writer);
            }

            if (hasTemporary && temporaryGroups != null)
            {
                WriteMergedChildGroup(cellFormId, 9, temporaryGroups, writer);
            }

            if (hasVwd && vwdGroups != null)
            {
                WriteMergedChildGroup(cellFormId, 10, vwdGroups, writer);
            }
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
                if (start >= end || end > _input.Length)
                {
                    continue;
                }

                var buffer = ConvertRangeToBuffer(start, end);
                writer.Write(buffer);
            }
        });
    }

    #endregion

    #region GRUP Helpers

    internal void WriteGrupWithContents(BinaryWriter writer, int grupType, uint labelValue, uint stamp, uint unknown,
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
        RecordHeaderProcessor.FinalizeGrupSize(writer.BaseStream, headerPosition);
    }

    /// <summary>
    ///     Writes a GRUP header and returns the header position for later size finalization.
    /// </summary>
    public long WriteGrupHeader(BinaryWriter writer, int grupType, byte[] labelBytes, uint stamp, uint unknown)
    {
        var headerPos = writer.BaseStream.Position;

        // GRUP labels are stored big-endian in Xbox data.
        // Writing the uint32 little-endian for PC output will flip the byte order automatically.
        var labelValue = BinaryPrimitives.ReadUInt32BigEndian(labelBytes);
        var header = new GroupHeader
        {
            GroupSize = 0u,
            Label = BitConverter.GetBytes(labelValue),
            GroupType = grupType,
            Stamp = stamp,
            Unknown = unknown
        };
        _ = RecordHeaderProcessor.WriteGrupHeader(writer.BaseStream, header);
        _stats.GrupsConverted++;

        return headerPos;
    }

    /// <summary>
    ///     Finalizes a GRUP header by writing the actual size.
    /// </summary>
    public static void FinalizeGrup(BinaryWriter writer, long headerPosition)
    {
        FinalizeGrupHeader(writer, headerPosition);
    }

    #endregion

    #region Range Conversion

    private byte[] ConvertRangeToBuffer(int startOffset, int endOffset)
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
            {
                break;
            }
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
        if (offset + 4 > endOffset || offset + 4 > _input.Length)
        {
            return false;
        }

        var sigBytes = _input.AsSpan(offset, 4);
        var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";
        if (signature != "GRUP")
        {
            return false;
        }

        if (offset + grupHeaderSize > endOffset || offset + grupHeaderSize > _input.Length)
        {
            return false;
        }

        var header = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(offset));
        var grupEnd = offset + (int)header.GroupSize;

        var headerPos = WriteGrupHeader(writer, header.GroupType, _input.AsSpan(offset + 8, 4).ToArray(), header.Stamp,
            header.Unknown);
        grupStack.Push((headerPos, grupEnd));
        offset += grupHeaderSize;
        return true;
    }

    private bool TryWriteRecordInRange(BinaryWriter writer, ref int offset, int endOffset)
    {
        var buffer = _recordWriter.ConvertRecordToBuffer(offset, out var recordEnd, out _);
        if (buffer != null)
        {
            writer.Write(buffer);
        }

        if (recordEnd <= offset)
        {
            return false;
        }

        offset = Math.Min(recordEnd, endOffset);
        return true;
    }

    #endregion
}
