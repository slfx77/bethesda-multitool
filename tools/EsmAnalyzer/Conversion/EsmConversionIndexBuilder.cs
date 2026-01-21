using System.Buffers.Binary;
using System.Linq;
using EsmAnalyzer.Helpers;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Conversion;

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
        if (!TryGetTes4StartOffset(out var offset)) return index;

        var grupStack = new Stack<(int end, int type, uint label)>();

        // Phase 1: Scan nested structure (before TOFT)
        while (offset + EsmParser.MainRecordHeaderSize <= _input.Length)
        {
            PopCompletedGroups(grupStack, offset);

            if (TryHandleIndexGroup(index, grupStack, ref offset)) continue;

            var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan(offset), true);
            if (recHeader == null) break;

            // Stop at TOFT record only when at a valid record boundary
            if (recHeader.Signature == "TOFT") break;

            if (!TryHandleIndexRecord(index, grupStack, ref offset)) break;
        }

        // Fallback: if no worlds were indexed, locate WRLD records directly
        if (index.Worlds.Count == 0)
        {
            var worlds = EsmHelpers.ScanForRecordType(_input, true, "WRLD")
                .OrderBy(r => r.Offset);

            foreach (var world in worlds)
                index.Worlds.Add(new WorldEntry(world.FormId, (int)world.Offset));
        }

        // If Phase 1 stopped early, ensure we start flat scanning at the real TOFT record
        var toftRecord = EsmHelpers.ScanForRecordType(_input, true, "TOFT")
            .OrderBy(r => r.Offset)
            .FirstOrDefault();

        if (toftRecord != null && offset < toftRecord.Offset)
            offset = (int)toftRecord.Offset;

        // Phase 2: Scan flat Cell Temporary groups after TOFT region
        ScanFlatCellGroups(index, offset);

        // Phase 3: Comprehensive scan for ALL Cell Temporary/Persistent groups throughout the file
        // This catches Cell Temporary groups that contain LAND/NAVM records
        ScanAllCellChildGroups(index);

        // Fallback: if very few CELLs were indexed, scan for all CELL records directly
        if (index.CellsById.Count < 1000)
        {
            var defaultWorldId = index.Worlds.FirstOrDefault()?.FormId;
            var cells = EsmHelpers.ScanForRecordType(_input, true, "CELL")
                .OrderBy(r => r.Offset);

            foreach (var cell in cells)
            {
                if (index.CellsById.ContainsKey(cell.FormId))
                    continue;

                var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan((int)cell.Offset), true);
                if (recHeader == null) continue;

                var recordEnd = (int)cell.Offset + EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
                if (recordEnd > _input.Length) continue;

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
        var worldChildrenFound = 0;

        // Skip past TOFT records and duplicate INFO records
        while (offset + EsmParser.MainRecordHeaderSize <= _input.Length)
        {
            var sigBytes = _input.AsSpan(offset, 4);
            var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";

            if (signature == "GRUP" || signature == "PURG")
                break; // Found start of flat GRUPs (PURG is big-endian GRUP)

            // Skip non-GRUP records (TOFT, duplicate INFO)
            var dataSize = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 4));
            offset += EsmParser.MainRecordHeaderSize + (int)dataSize;
        }

        // Now scan flat GRUPs - Xbox 360 may have orphaned data between GRUPs
        while (offset + EsmParser.MainRecordHeaderSize <= _input.Length)
        {
            var sigBytes = _input.AsSpan(offset, 4);
            var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";

            if (signature != "GRUP" && signature != "PURG")
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
                    break; // End of flat GRUPs
                continue;
            }

            var grupSize = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 4));
            var labelValue = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 8));
            var grupType = (int)BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 12));

            // Index Cell Temporary (9) groups - these contain REFR/ACHR/ACRE
            if (grupType == 9)
            {
                var key = (labelValue, grupType);
                if (!index.CellChildGroups.TryGetValue(key, out var list))
                {
                    list = [];
                    index.CellChildGroups[key] = list;
                }

                list.Add(new GrupEntry(grupType, labelValue, offset, (int)grupSize));
            }

            // Scan World Children (type 1) groups for exterior cells
            if (grupType == 1)
            {
                var worldId = labelValue;
                ScanFlatWorldChildrenGroup(index, offset, (int)grupSize, worldId);
                worldChildrenFound++;
            }

            offset += (int)grupSize;
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
                continue;

            // Read GRUP header
            var grupSize = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 4));

            // Validate GRUP size (must be at least header size, not larger than remaining file)
            if (grupSize < 24 || grupSize > (uint)(_input.Length - offset))
                continue;

            var labelValue = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 8));
            var grupType = (int)BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 12));

            // Index Cell Child groups (8=Persistent, 9=Temporary, 10=VWD)
            if (grupType is 8 or 9 or 10)
            {
                var key = (labelValue, grupType);
                if (!index.CellChildGroups.TryGetValue(key, out var list))
                {
                    list = [];
                    index.CellChildGroups[key] = list;
                }

                // Only add if not already indexed at this offset (avoid duplicates)
                if (!list.Any(g => g.Offset == offset))
                {
                    list.Add(new GrupEntry(grupType, labelValue, offset, (int)grupSize));
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
        var cellCount = 0;

        while (offset < grupEnd && offset + 4 <= _input.Length)
        {
            // Pop completed groups
            while (grupStack.Count > 0 && offset >= grupStack.Peek().end) grupStack.Pop();

            var sigBytes = _input.AsSpan(offset, 4);
            var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";

            if (signature == "GRUP")
            {
                var childGrupSize = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 4));
                var childLabelValue = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 8));
                var childGrupType = (int)BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 12));

                // Track cell child groups (Persistent/Temporary/VWD)
                if (childGrupType is 8 or 9 or 10)
                {
                    var key = (childLabelValue, childGrupType);
                    if (!index.CellChildGroups.TryGetValue(key, out var list))
                    {
                        list = [];
                        index.CellChildGroups[key] = list;
                    }

                    list.Add(new GrupEntry(childGrupType, childLabelValue, offset, (int)childGrupSize));
                }

                grupStack.Push((offset + (int)childGrupSize, childGrupType, childLabelValue));
                offset += EsmParser.MainRecordHeaderSize;
            }
            else if (signature == "CELL")
            {
                var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan(offset), true);
                if (recHeader == null) break;

                var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
                if (recordEnd > _input.Length) break;

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
                    cellCount++;
                }

                offset = recordEnd;
            }
            else
            {
                // Skip other records (REFR, ACHR, etc.)
                var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan(offset), true);
                if (recHeader == null) break;

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
        var subrecords = EsmHelpers.ParseSubrecords(recordData, true);
        var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");

        int? gridX = null;
        int? gridY = null;
        var isExterior = false;

        if (xclc != null && xclc.Data.Length >= 8)
        {
            isExterior = true;
            gridX = (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan());
            gridY = (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan(), 4);

            if (gridX > 0x7FFFFFFF) gridX = (int)(gridX - 0x100000000);
            if (gridY > 0x7FFFFFFF) gridY = (int)(gridY - 0x100000000);
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
        var subrecords = EsmHelpers.ParseSubrecords(recordData, true);
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

            if (gridX > 0x7FFFFFFF) gridX = (int)(gridX - 0x100000000);
            if (gridY > 0x7FFFFFFF) gridY = (int)(gridY - 0x100000000);
        }

        return new CellEntry(recHeader.FormId, offset, recHeader.Flags, recHeader.DataSize, isExterior, gridX, gridY,
            worldId);
    }

    private bool TryGetTes4StartOffset(out int offset)
    {
        offset = 0;
        var header = EsmParser.ParseFileHeader(_input);
        if (header == null || !header.IsBigEndian) return false;

        var tes4Header = EsmParser.ParseRecordHeader(_input, true);
        if (tes4Header == null) return false;

        offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;
        return true;
    }

    private static void PopCompletedGroups(Stack<(int end, int type, uint label)> grupStack, int offset)
    {
        while (grupStack.Count > 0 && offset >= grupStack.Peek().end) grupStack.Pop();
    }

    private bool TryHandleIndexGroup(ConversionIndex index, Stack<(int end, int type, uint label)> grupStack,
        ref int offset)
    {
        var sigBytes = _input.AsSpan(offset, 4);
        var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";

        if (signature != "GRUP") return false;

        var grupSize = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 4));
        var labelValue = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 8));
        var grupType = (int)BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(offset + 12));
        var grupEnd = offset + (int)grupSize;

        // Track cell child groups (Persistent/Temporary/VWD)
        if (grupType is 8 or 9 or 10)
        {
            var key = (labelValue, grupType);
            if (!index.CellChildGroups.TryGetValue(key, out var list))
            {
                list = [];
                index.CellChildGroups[key] = list;
            }

            list.Add(new GrupEntry(grupType, labelValue, offset, (int)grupSize));
        }

        grupStack.Push((grupEnd, grupType, labelValue));
        offset += EsmParser.MainRecordHeaderSize;
        return true;
    }

    private bool TryHandleIndexRecord(ConversionIndex index, Stack<(int end, int type, uint label)> grupStack,
        ref int offset)
    {
        var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan(offset), true);
        if (recHeader == null) return false;

        var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
        if (recordEnd > _input.Length) return false;

        if (recHeader.Signature == "WRLD") index.Worlds.Add(new WorldEntry(recHeader.FormId, offset));

        if (recHeader.Signature == "CELL")
        {
            var cellEntry = BuildCellEntry(recHeader, offset, recordEnd, grupStack);
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
            else if (!cellEntry.IsExterior && !cellEntry.WorldId.HasValue)
            {
                // Interior cell (no XCLC, not under a WRLD group)
                index.InteriorCells.Add(cellEntry);
            }
        }

        offset = recordEnd;
        return true;
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
        var subrecords = EsmHelpers.ParseSubrecords(recordData, true);
        var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");

        int? gridX = null;
        int? gridY = null;
        var isExterior = false;

        if (xclc != null && xclc.Data.Length >= 8)
        {
            isExterior = true;
            gridX = (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan());
            gridY = (int)BinaryUtils.ReadUInt32BE(xclc.Data.AsSpan(), 4);

            if (gridX > 0x7FFFFFFF) gridX = (int)(gridX - 0x100000000);
            if (gridY > 0x7FFFFFFF) gridY = (int)(gridY - 0x100000000);
        }

        return new CellEntry(recHeader.FormId, offset, recHeader.Flags, recHeader.DataSize, isExterior, gridX, gridY,
            worldId);
    }

    private static uint? GetWorldIdFromStack(Stack<(int end, int type, uint label)> grupStack)
    {
        foreach (var entry in grupStack)
            if (entry.type == 1)
                return entry.label;

        return null;
    }
}