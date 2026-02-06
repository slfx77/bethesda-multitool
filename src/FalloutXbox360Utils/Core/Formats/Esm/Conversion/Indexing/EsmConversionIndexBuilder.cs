using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

/// <summary>
///     Builds the conversion index from the input ESM file.
///     The index tracks WRLD, CELL, and child GRUP positions for reconstruction.
/// </summary>
internal sealed class EsmConversionIndexBuilder
{
    private readonly byte[] _input;

    public EsmConversionIndexBuilder(byte[] input)
    {
        _input = input;
    }

    /// <summary>
    ///     Builds the conversion index by scanning the input file.
    ///     Scans both nested GRUPs and the flat Cell Temporary groups after TOFT.
    /// </summary>
    public ConversionIndex Build()
    {
        var index = new ConversionIndex();
        if (!TryGetTes4StartOffset(out var offset))
        {
            return index;
        }

        var grupStack = new Stack<(int end, int type, uint label)>();

        // Phase 1: Scan nested structure
        // Note: TOFT records appear INSIDE Cell Children GRUPs on Xbox 360, so we can't stop at TOFT
        while (offset + EsmParser.MainRecordHeaderSize <= _input.Length)
        {
            PopCompletedGroups(grupStack, offset);

            if (TryHandleIndexGroup(index, grupStack, ref offset))
            {
                continue;
            }

            var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan(offset), true);
            if (recHeader == null)
            {
                break;
            }

            // Skip TOFT records (Size=0, appear inside Cell Children GRUPs)
            if (recHeader.Signature == "TOFT")
            {
                offset += EsmParser.MainRecordHeaderSize; // TOFT has DataSize=0
                continue;
            }

            if (!TryHandleIndexRecord(index, grupStack, ref offset))
            {
                break;
            }
        }

        // Fallback: if no worlds were indexed, locate WRLD records directly
        if (index.Worlds.Count == 0)
        {
            var worlds = EsmRecordParser.ScanForRecordType(_input, true, "WRLD")
                .OrderBy(r => r.Offset);

            foreach (var world in worlds)
            {
                index.Worlds.Add(new WorldEntry(world.FormId, (int)world.Offset));
            }
        }

        // If Phase 1 stopped early, ensure we start flat scanning at the real TOFT record
        var toftRecord = EsmRecordParser.ScanForRecordType(_input, true, "TOFT")
            .OrderBy(r => r.Offset)
            .FirstOrDefault();

        if (toftRecord != null && offset < toftRecord.Offset)
        {
            offset = (int)toftRecord.Offset;
        }

        // Phase 2: Scan flat Cell Temporary groups after TOFT region
        ScanFlatCellGroups(index, offset);

        // Phase 3: Comprehensive scan for ALL Cell Temporary/Persistent groups throughout the file
        // This catches Cell Temporary groups that contain LAND/NAVM records
        ScanAllCellChildGroups(index);

        // Fallback: if very few CELLs were indexed, scan for all CELL records directly
        if (index.CellsById.Count < 1000)
        {
            var defaultWorldId = index.Worlds.FirstOrDefault()?.FormId;
            var cells = EsmRecordParser.ScanForRecordType(_input, true, "CELL")
                .OrderBy(r => r.Offset);

            foreach (var cell in cells)
            {
                if (index.CellsById.ContainsKey(cell.FormId))
                {
                    continue;
                }

                var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan((int)cell.Offset), true);
                if (recHeader == null)
                {
                    continue;
                }

                var recordEnd = (int)cell.Offset + EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
                if (recordEnd > _input.Length)
                {
                    continue;
                }

                var cellEntry = BuildCellEntryFromRecord(recHeader, (int)cell.Offset, recordEnd, defaultWorldId);
                index.CellsById[recHeader.FormId] = cellEntry;

                if (cellEntry.IsExterior && cellEntry.WorldId.HasValue)
                {
                    if (!index.ExteriorCellsByWorld.TryGetValue(cellEntry.WorldId.Value, out var list))
                    {
                        list = [];
                        index.ExteriorCellsByWorld[cellEntry.WorldId.Value] = list;
                    }

                    list.Add(cellEntry);
                }
                else if (!cellEntry.IsExterior)
                {
                    index.InteriorCells.Add(cellEntry);
                }
            }
        }

        return index;
    }

    /// <summary>
    ///     Scans flat groups that appear after TOFT:
    ///     - Cell Temporary (type 9) groups containing REFR/ACHR/ACRE records
    ///     - World Children (type 1) groups containing Exterior Cell Blocks with actual CELL records
    /// </summary>
    private void ScanFlatCellGroups(ConversionIndex index, int startOffset)
    {
        var offset = startOffset;

        // Skip past TOFT records and any non-GRUP streaming cache records
        while (offset + EsmParser.MainRecordHeaderSize <= _input.Length)
        {
            var sigBytes = _input.AsSpan(offset, 4);
            var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";

            if (signature is "GRUP" or "PURG")
            {
                break; // Found start of flat GRUPs (PURG is big-endian GRUP)
            }

            // Skip non-GRUP records (TOFT and streamed cache entries)
            var header = RecordHeaderProcessor.ReadRecordHeader(_input.AsSpan(offset));
            offset += EsmParser.MainRecordHeaderSize + (int)header.DataSize;
        }

        // Now scan flat GRUPs - Xbox 360 may have orphaned data between GRUPs
        while (offset + EsmParser.MainRecordHeaderSize <= _input.Length)
        {
            var sigBytes = _input.AsSpan(offset, 4);
            var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";

            if (signature is not "GRUP" and not "PURG")
            {
                // Not a GRUP - try to skip forward and find the next GRUP
                var found = false;
                for (var scan = offset + 1; scan <= _input.Length - 4 && scan < offset + 1024; scan++)
                {
                    if (_input[scan] == 0x50 && _input[scan + 1] == 0x55 &&
                        _input[scan + 2] == 0x52 && _input[scan + 3] == 0x47) // "PURG"
                    {
                        offset = scan;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    break; // End of flat GRUPs
                }

                continue;
            }

            var grupHeader = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(offset));

            // Index Cell Temporary (9) groups - these contain REFR/ACHR/ACRE
            if (grupHeader.GroupType == 9)
            {
                var key = (grupHeader.LabelAsUInt, grupHeader.GroupType);
                if (!index.CellChildGroups.TryGetValue(key, out var list))
                {
                    list = [];
                    index.CellChildGroups[key] = list;
                }

                list.Add(new GrupEntry(grupHeader.GroupType, grupHeader.LabelAsUInt, offset, (int)grupHeader.GroupSize));
            }

            // Scan World Children (type 1) groups for exterior cells
            if (grupHeader.GroupType == 1)
            {
                var worldId = grupHeader.LabelAsUInt;
                ScanFlatWorldChildrenGroup(index, offset, (int)grupHeader.GroupSize, worldId);
            }

            offset += (int)grupHeader.GroupSize;
        }
    }

    /// <summary>
    ///     Comprehensive scan for ALL Cell Child GRUPs (type 8, 9, 10) throughout the entire file.
    ///     Xbox 360 ESMs have Cell Temporary groups scattered throughout containing LAND/NAVM records.
    /// </summary>
    private void ScanAllCellChildGroups(ConversionIndex index)
    {
        // Scan byte-by-byte for "PURG" (big-endian GRUP) throughout the file
        for (var offset = 0; offset <= _input.Length - 24; offset++)
        {
            // Quick check for PURG signature
            if (_input[offset] != 0x50 || _input[offset + 1] != 0x55 ||
                _input[offset + 2] != 0x52 || _input[offset + 3] != 0x47)
            {
                continue;
            }

            // Read GRUP header
            var grupHeader = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(offset));

            // Validate GRUP size (must be at least header size, not larger than remaining file)
            if (grupHeader.GroupSize < 24 || grupHeader.GroupSize > (uint)(_input.Length - offset))
            {
                continue;
            }

            // Index Cell Child groups (8=Persistent, 9=Temporary, 10=VWD)
            if (grupHeader.GroupType is 8 or 9 or 10)
            {
                var key = (grupHeader.LabelAsUInt, grupHeader.GroupType);
                if (!index.CellChildGroups.TryGetValue(key, out var list))
                {
                    list = [];
                    index.CellChildGroups[key] = list;
                }

                // Only add if not already indexed at this offset (avoid re-adding the same group)
                if (!list.Any(g => g.Offset == offset))
                {
                    list.Add(new GrupEntry(grupHeader.GroupType, grupHeader.LabelAsUInt, offset, (int)grupHeader.GroupSize));
                }
            }
        }
    }

    /// <summary>
    ///     Scans a flat World Children GRUP for exterior cells.
    ///     Xbox 360 stores exterior cells in a World Children GRUP at top level, not nested under WRLD.
    /// </summary>
    private void ScanFlatWorldChildrenGroup(ConversionIndex index, int grupOffset, int grupSize, uint worldId)
    {
        var offset = grupOffset + EsmParser.MainRecordHeaderSize;
        var grupEnd = grupOffset + grupSize;
        var grupStack = new Stack<(int end, int type, uint label)>();

        while (offset < grupEnd && offset + 4 <= _input.Length)
        {
            // Pop completed groups
            while (grupStack.Count > 0 && offset >= grupStack.Peek().end)
            {
                _ = grupStack.Pop();
            }

            var sigBytes = _input.AsSpan(offset, 4);
            var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";

            if (signature == "GRUP")
            {
                var childGrupHeader = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(offset));

                // Track cell child groups (Persistent/Temporary/VWD)
                if (childGrupHeader.GroupType is 8 or 9 or 10)
                {
                    var key = (childGrupHeader.LabelAsUInt, childGrupHeader.GroupType);
                    if (!index.CellChildGroups.TryGetValue(key, out var list))
                    {
                        list = [];
                        index.CellChildGroups[key] = list;
                    }

                    list.Add(new GrupEntry(childGrupHeader.GroupType, childGrupHeader.LabelAsUInt, offset,
                        (int)childGrupHeader.GroupSize));
                }

                grupStack.Push((offset + (int)childGrupHeader.GroupSize, childGrupHeader.GroupType, childGrupHeader.LabelAsUInt));
                offset += EsmParser.MainRecordHeaderSize;
            }
            else if (signature == "CELL")
            {
                var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan(offset), true);
                if (recHeader == null)
                {
                    break;
                }

                var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
                if (recordEnd > _input.Length)
                {
                    break;
                }

                // Build cell entry with the worldId from the World Children label
                var cellEntry = BuildCellEntryWithWorld(recHeader, offset, recordEnd, worldId);
                index.CellsById[recHeader.FormId] = cellEntry;

                if (cellEntry.IsExterior)
                {
                    if (!index.ExteriorCellsByWorld.TryGetValue(worldId, out var list))
                    {
                        list = [];
                        index.ExteriorCellsByWorld[worldId] = list;
                    }

                    list.Add(cellEntry);
                }

                offset = recordEnd;
            }
            else
            {
                // Skip other records (REFR, ACHR, etc.)
                var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan(offset), true);
                if (recHeader == null)
                {
                    break;
                }

                offset += EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
            }
        }
    }

    /// <summary>
    ///     Builds a CellEntry with an explicit worldId (for flat World Children groups).
    /// </summary>
    private CellEntry BuildCellEntryWithWorld(MainRecordHeader recHeader, int offset, int recordEnd, uint worldId)
    {
        var recInfo = new AnalyzerRecordInfo
        {
            Signature = recHeader.Signature,
            FormId = recHeader.FormId,
            Flags = recHeader.Flags,
            DataSize = recHeader.DataSize,
            Offset = (uint)offset,
            TotalSize = (uint)(recordEnd - offset)
        };

        var recordData = EsmHelpers.GetRecordData(_input, recInfo, true);
        var subrecords = EsmRecordParser.ParseSubrecords(recordData, true);
        var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");

        int? gridX = null;
        int? gridY = null;
        var isExterior = false;

        if (xclc != null && xclc.Data.Length >= 8)
        {
            isExterior = true;
            gridX = (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan());
            gridY = (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan(), 4);

            if (gridX > 0x7FFFFFFF)
            {
                gridX = (int)(gridX - 0x100000000);
            }

            if (gridY > 0x7FFFFFFF)
            {
                gridY = (int)(gridY - 0x100000000);
            }
        }

        return new CellEntry(recHeader.FormId, offset, recHeader.Flags, recHeader.DataSize, isExterior, gridX, gridY,
            worldId);
    }

    private CellEntry BuildCellEntryFromRecord(MainRecordHeader recHeader, int offset, int recordEnd,
        uint? defaultWorldId)
    {
        var recInfo = new AnalyzerRecordInfo
        {
            Signature = recHeader.Signature,
            FormId = recHeader.FormId,
            Flags = recHeader.Flags,
            DataSize = recHeader.DataSize,
            Offset = (uint)offset,
            TotalSize = (uint)(recordEnd - offset)
        };

        var recordData = EsmHelpers.GetRecordData(_input, recInfo, true);
        var subrecords = EsmRecordParser.ParseSubrecords(recordData, true);
        var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");

        int? gridX = null;
        int? gridY = null;
        var isExterior = false;
        uint? worldId = null;

        if (xclc != null && xclc.Data.Length >= 8)
        {
            isExterior = true;
            worldId = defaultWorldId;
            gridX = (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan());
            gridY = (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan(), 4);

            if (gridX > 0x7FFFFFFF)
            {
                gridX = (int)(gridX - 0x100000000);
            }

            if (gridY > 0x7FFFFFFF)
            {
                gridY = (int)(gridY - 0x100000000);
            }
        }

        return new CellEntry(recHeader.FormId, offset, recHeader.Flags, recHeader.DataSize, isExterior, gridX, gridY,
            worldId);
    }

    private bool TryGetTes4StartOffset(out int offset)
    {
        offset = 0;
        var header = EsmParser.ParseFileHeader(_input);
        if (header == null || !header.IsBigEndian)
        {
            return false;
        }

        var tes4Header = EsmParser.ParseRecordHeader(_input, true);
        if (tes4Header == null)
        {
            return false;
        }

        offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;
        return true;
    }

    private static void PopCompletedGroups(Stack<(int end, int type, uint label)> grupStack, int offset)
    {
        while (grupStack.Count > 0 && offset >= grupStack.Peek().end)
        {
            _ = grupStack.Pop();
        }
    }

    private bool TryHandleIndexGroup(ConversionIndex index, Stack<(int end, int type, uint label)> grupStack,
        ref int offset)
    {
        var sigBytes = _input.AsSpan(offset, 4);
        var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";

        if (signature != "GRUP")
        {
            return false;
        }

        var grupHeader = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(offset));
        var grupEnd = offset + (int)grupHeader.GroupSize;

        // Track cell child groups (Persistent/Temporary/VWD)
        if (grupHeader.GroupType is 8 or 9 or 10)
        {
            var key = (grupHeader.LabelAsUInt, grupHeader.GroupType);
            if (!index.CellChildGroups.TryGetValue(key, out var list))
            {
                list = [];
                index.CellChildGroups[key] = list;
            }

            list.Add(new GrupEntry(grupHeader.GroupType, grupHeader.LabelAsUInt, offset, (int)grupHeader.GroupSize));
        }

        grupStack.Push((grupEnd, grupHeader.GroupType, grupHeader.LabelAsUInt));
        offset += EsmParser.MainRecordHeaderSize;
        return true;
    }

    private bool TryHandleIndexRecord(ConversionIndex index, Stack<(int end, int type, uint label)> grupStack,
        ref int offset)
    {
        var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan(offset), true);
        if (recHeader == null)
        {
            return false;
        }

        var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
        if (recordEnd > _input.Length)
        {
            return false;
        }

        if (recHeader.Signature == "WRLD")
        {
            index.Worlds.Add(new WorldEntry(recHeader.FormId, offset));
        }

        if (recHeader.Signature == "CELL")
        {
            var cellEntry = BuildCellEntry(recHeader, offset, recordEnd, grupStack);
            index.CellsById[recHeader.FormId] = cellEntry;

            // Worldspace persistent cell: a CELL that lives directly under a WRLD's World Children GRUP,
            // not nested under exterior block/sub-block groups.
            if (cellEntry.WorldId.HasValue && !IsInsideExteriorBlockOrSubBlock(grupStack))
            {
                var worldId = cellEntry.WorldId.Value;
                if (!index.WorldPersistentCellsByWorld.ContainsKey(worldId))
                {
                    index.WorldPersistentCellsByWorld[worldId] = cellEntry;
                }
            }

            if (cellEntry.IsExterior && cellEntry.WorldId.HasValue && IsInsideExteriorBlockOrSubBlock(grupStack))
            {
                if (!index.ExteriorCellsByWorld.TryGetValue(cellEntry.WorldId.Value, out var list))
                {
                    list = [];
                    index.ExteriorCellsByWorld[cellEntry.WorldId.Value] = list;
                }

                list.Add(cellEntry);
            }
            else if (!cellEntry.IsExterior && !cellEntry.WorldId.HasValue)
            {
                // Interior cell (no XCLC, not under a WRLD group)
                index.InteriorCells.Add(cellEntry);
            }
        }

        offset = recordEnd;
        return true;
    }

    private static bool IsInsideExteriorBlockOrSubBlock(Stack<(int end, int type, uint label)> grupStack)
    {
        foreach (var (_, type, _) in grupStack)
        {
            if (type is 4 or 5)
            {
                return true;
            }
        }

        return false;
    }

    private CellEntry BuildCellEntry(MainRecordHeader recHeader, int offset, int recordEnd,
        Stack<(int end, int type, uint label)> grupStack)
    {
        var worldId = GetWorldIdFromStack(grupStack);
        var recInfo = new AnalyzerRecordInfo
        {
            Signature = recHeader.Signature,
            FormId = recHeader.FormId,
            Flags = recHeader.Flags,
            DataSize = recHeader.DataSize,
            Offset = (uint)offset,
            TotalSize = (uint)(recordEnd - offset)
        };

        var recordData = EsmHelpers.GetRecordData(_input, recInfo, true);
        var subrecords = EsmRecordParser.ParseSubrecords(recordData, true);
        var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");

        int? gridX = null;
        int? gridY = null;
        var isExterior = false;

        if (xclc != null && xclc.Data.Length >= 8)
        {
            isExterior = true;
            gridX = (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan());
            gridY = (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan(), 4);

            if (gridX > 0x7FFFFFFF)
            {
                gridX = (int)(gridX - 0x100000000);
            }

            if (gridY > 0x7FFFFFFF)
            {
                gridY = (int)(gridY - 0x100000000);
            }
        }

        return new CellEntry(recHeader.FormId, offset, recHeader.Flags, recHeader.DataSize, isExterior, gridX, gridY,
            worldId);
    }

    private static uint? GetWorldIdFromStack(Stack<(int end, int type, uint label)> grupStack)
    {
        foreach (var (_, type, label) in grupStack)
        {
            if (type == 1)
            {
                return label;
            }
        }

        return null;
    }
}
