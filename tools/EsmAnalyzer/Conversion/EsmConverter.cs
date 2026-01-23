using System.Buffers.Binary;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using static EsmAnalyzer.Conversion.EsmEndianHelpers;

namespace EsmAnalyzer.Conversion;

/// <summary>
///     ESM file converter from Xbox 360 (big-endian) to PC (little-endian) format.
/// </summary>
public sealed class EsmConverter : IDisposable
{
    private readonly EsmGrupWriter _grupWriter;
    private readonly byte[] _input;
    private readonly MemoryStream _output;
    private readonly EsmRecordWriter _recordWriter;
    private readonly EsmConversionStats _stats = new();
    private readonly bool _verbose;
    private readonly BinaryWriter _writer;
    private bool _disposed;

    public EsmConverter(byte[] input, bool verbose)
    {
        _input = input;
        _verbose = verbose;
        _output = new MemoryStream(input.Length);
        _writer = new BinaryWriter(_output);
        _recordWriter = new EsmRecordWriter(input, _stats);
        _grupWriter = new EsmGrupWriter(input, _recordWriter, _stats);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _writer.Dispose();
            _output.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    ///     Converts the entire ESM file from big-endian to little-endian.
    ///     Uses an iterative approach with explicit stack to avoid stack overflow.
    /// </summary>
    public byte[] ConvertToLittleEndian()
    {
        const int grupHeaderSize = 24;

        var indexBuilder = new EsmConversionIndexBuilder(_input);
        var index = indexBuilder.Build();

        if (_verbose)
        {
            var exteriorCells = index.ExteriorCellsByWorld.Sum(kvp => kvp.Value.Count);
            var temporaryGroups = index.CellChildGroups.Count(kvp => kvp.Key.type == 9);
            var persistentGroups = index.CellChildGroups.Count(kvp => kvp.Key.type == 8);
            var totalChildRecords = index.CellChildGroups.Sum(kvp => kvp.Value.Sum(g => g.Size));
            AnsiConsole.MarkupLine(
                $"[grey]Index: worlds={index.Worlds.Count}, interiorCells={index.InteriorCells.Count}, exteriorCells={exteriorCells}, exteriorWorlds={index.ExteriorCellsByWorld.Count}[/]");
            foreach (var kvp in index.ExteriorCellsByWorld)
                Console.WriteLine($"  World 0x{kvp.Key:X8}: {kvp.Value.Count} exterior cells");
            AnsiConsole.MarkupLine(
                $"[grey]CellChildGroups: persistent={persistentGroups}, temporary={temporaryGroups}, totalSize={totalChildRecords:N0} bytes[/]");
        }

        // Stack to track GRUP boundaries: (outputHeaderPos, inputGrupEnd)
        var grupStack = new Stack<(long outputHeaderPos, int inputGrupEnd)>();

        var inputOffset = 0;

        // Convert TES4 header record first (it's never inside a GRUP)
        inputOffset = ConvertRecord(inputOffset);

        // Process remaining content iteratively
        while (inputOffset < _input.Length)
        {
            FinalizeCompletedOutputGroups(grupStack, inputOffset);

            if (inputOffset + 4 > _input.Length) break;

            var signature = ReadSignature(inputOffset);

            if (TrySkipToftRegion(signature, grupStack, ref inputOffset)) continue;

            if (grupStack.Count == 0 && !IsValidRecordSignature(signature))
            {
                if (TryResyncToNextGrup(ref inputOffset))
                    continue;

                if (_verbose)
                    Console.WriteLine(
                        $"  [0x{inputOffset:X8}] Invalid top-level signature '{signature}', stopping conversion.");

                break;
            }

            if (TrySkipTopLevelRecord(signature, grupStack, ref inputOffset)) continue;

            // If at top level and not a GRUP, try resyncing (likely orphaned data)
            if (grupStack.Count == 0 && signature != "GRUP")
            {
                if (TryResyncToNextGrup(ref inputOffset))
                    continue;

                if (_verbose)
                    Console.WriteLine(
                        $"  [0x{inputOffset:X8}] Cannot resync after '{signature}', stopping conversion.");

                break;
            }

            if (TryHandleGrup(signature, grupHeaderSize, index, grupStack, ref inputOffset)) continue;

            inputOffset = ConvertRecord(inputOffset);
        }

        // Finalize any remaining GRUPs
        while (grupStack.Count > 0)
        {
            var (headerPos, _) = grupStack.Pop();
            EsmGrupWriter.FinalizeGrup(_writer, headerPos);
        }

        var outputBytes = _output.ToArray();
        RebuildOfstTables(outputBytes, index);
        return outputBytes;
    }

    /// <summary>
    ///     Prints conversion statistics.
    /// </summary>
    public void PrintStats()
    {
        _stats.PrintStats(_verbose);
    }

    #region Record Conversion

    private int ConvertRecord(int offset)
    {
        var buffer = _recordWriter.ConvertRecordToBuffer(offset, out var recordEnd, out _);
        if (buffer != null) _writer.Write(buffer);

        if (_verbose && _stats.RecordsConverted % 10000 == 0)
            AnsiConsole.MarkupLine($"[grey]  Converted {_stats.RecordsConverted:N0} records...[/]");

        return recordEnd;
    }

    #endregion

    private void RebuildOfstTables(byte[] output, ConversionIndex index)
    {
        var outputHeader = EsmParser.ParseFileHeader(output);
        if (outputHeader == null || outputHeader.IsBigEndian)
            return;

        var outputRecords = EsmHelpers.ScanAllRecords(output, outputHeader.IsBigEndian);
        var outputWrlds = outputRecords
            .Where(r => r.Signature == "WRLD")
            .ToList();

        var cellRecordOffsets = outputRecords
            .Where(r => r.Signature == "CELL")
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Offset).First().Offset);

        var outputExteriorCellsByWorld = BuildExteriorCellsByWorldFromOutput(output, outputHeader.IsBigEndian);
        var fallbackExteriorCells = BuildExteriorCellsFromAllCells(outputRecords, output, outputHeader.IsBigEndian);
        var indexExteriorCellsByWorld = index.ExteriorCellsByWorld
            .Where(kvp => kvp.Value.Count > 0)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
                    .Where(c => c.GridX.HasValue && c.GridY.HasValue)
                    .Select(c => new CellGrid(c.FormId, c.GridX!.Value, c.GridY!.Value))
                    .ToList());

        if (_verbose)
        {
            AnsiConsole.MarkupLine(
                $"[grey]OFST Rebuild: {outputWrlds.Count} WRLD records, {cellRecordOffsets.Count} CELL records[/]");
            AnsiConsole.MarkupLine(
                $"[grey]  outputExteriorCellsByWorld entries: {outputExteriorCellsByWorld.Count}[/]");
            foreach (var kvp in outputExteriorCellsByWorld.Take(3))
                AnsiConsole.MarkupLine($"[grey]    World 0x{kvp.Key:X8}: {kvp.Value.Count} cells[/]");
            AnsiConsole.MarkupLine($"[grey]  fallbackExteriorCells: {fallbackExteriorCells.Count}[/]");
        }

        foreach (var wrld in outputWrlds)
        {
            if (!indexExteriorCellsByWorld.TryGetValue(wrld.FormId, out var exteriorCells))
                if (!outputExteriorCellsByWorld.TryGetValue(wrld.FormId, out exteriorCells))
                    exteriorCells = [];

            if (fallbackExteriorCells.Count == 0 && exteriorCells.Count == 0)
                continue;

            if (fallbackExteriorCells.Count > 0)
            {
                var merged = new Dictionary<uint, CellGrid>();
                foreach (var cell in exteriorCells)
                    merged[cell.FormId] = cell;
                foreach (var cell in fallbackExteriorCells)
                    merged.TryAdd(cell.FormId, cell);
                exteriorCells = merged.Values.ToList();
            }

            if ((wrld.Flags & 0x00040000) != 0)
                continue; // Skip compressed WRLD records

            var wrldData = EsmHelpers.GetRecordData(output, wrld, outputHeader.IsBigEndian);
            var subs = EsmHelpers.ParseSubrecords(wrldData, outputHeader.IsBigEndian);
            var ofst = subs.FirstOrDefault(s => s.Signature == "OFST");
            if (ofst == null || ofst.Data.Length == 0)
                continue;

            if (!TryGetWorldBounds(subs, outputHeader.IsBigEndian, out var minX, out var maxX, out var minY,
                    out var maxY))
            {
                if (_verbose)
                    AnsiConsole.MarkupLine($"[yellow]  WRLD 0x{wrld.FormId:X8}: failed to get bounds[/]");
                continue;
            }

            var columns = maxX - minX + 1;
            var rows = maxY - minY + 1;

            if (columns <= 0 || rows <= 0)
                continue;

            var count = ofst.Data.Length / 4;
            if (count <= 0)
                continue;

            var expected = columns * rows;
            if (expected != count)
            {
                if (columns > 0 && count % columns == 0)
                    rows = count / columns;
                else if (rows > 0 && count % rows == 0)
                    columns = count / rows;
                else
                    continue;
            }

            var offsets = new uint[count];
            var bestByIndex = new Dictionary<int, uint>();
            var cellsMatched = 0;
            var cellsOutOfBounds = 0;
            var cellsNotFound = 0;
            var cellsNegativeRel = 0;

            foreach (var cell in exteriorCells)
            {
                var col = cell.GridX - minX;
                var row = cell.GridY - minY;
                if (col < 0 || col >= columns || row < 0 || row >= rows)
                {
                    cellsOutOfBounds++;
                    continue;
                }

                var ofstIndex = row * columns + col;
                if (ofstIndex < 0 || ofstIndex >= count)
                    continue;

                if (!cellRecordOffsets.TryGetValue(cell.FormId, out var cellOffset))
                {
                    cellsNotFound++;
                    continue;
                }

                var rel = cellOffset - (long)wrld.Offset;
                if (rel <= 0 || rel > uint.MaxValue)
                {
                    cellsNegativeRel++;
                    continue;
                }

                var relValue = (uint)rel;
                if (bestByIndex.TryGetValue(ofstIndex, out var existing))
                {
                    if (relValue < existing)
                    {
                        bestByIndex[ofstIndex] = relValue;
                        offsets[ofstIndex] = relValue;
                    }
                }
                else
                {
                    bestByIndex[ofstIndex] = relValue;
                    offsets[ofstIndex] = relValue;
                }

                cellsMatched++;
            }

            var ofstDataOffsetLocal = (long)wrld.Offset + EsmParser.MainRecordHeaderSize + ofst.Offset + 6;
            if (ofstDataOffsetLocal < 0 || ofstDataOffsetLocal + (long)count * 4 > output.Length)
                continue;

            for (var i = 0; i < offsets.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(
                    output.AsSpan((int)ofstDataOffsetLocal + i * 4, 4),
                    offsets[i]);
        }
    }


    private static bool TryGetWorldBounds(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian,
        out int minX, out int maxX, out int minY, out int maxY)
    {
        minX = 0;
        maxX = 0;
        minY = 0;
        maxY = 0;

        var nam0 = subrecords.FirstOrDefault(s => s.Signature == "NAM0");
        var nam9 = subrecords.FirstOrDefault(s => s.Signature == "NAM9");
        if (nam0 == null || nam9 == null || nam0.Data.Length < 8 || nam9.Data.Length < 8)
            return false;

        var minXf = ReadFloat(nam0.Data, 0, bigEndian);
        var minYf = ReadFloat(nam0.Data, 4, bigEndian);
        var maxXf = ReadFloat(nam9.Data, 0, bigEndian);
        var maxYf = ReadFloat(nam9.Data, 4, bigEndian);

        if (IsUnsetFloat(minXf)) minXf = 0;
        if (IsUnsetFloat(minYf)) minYf = 0;
        if (IsUnsetFloat(maxXf)) maxXf = 0;
        if (IsUnsetFloat(maxYf)) maxYf = 0;

        const float cellScale = 4096f;
        minX = (int)Math.Round(minXf / cellScale);
        minY = (int)Math.Round(minYf / cellScale);
        maxX = (int)Math.Round(maxXf / cellScale);
        maxY = (int)Math.Round(maxYf / cellScale);

        return true;
    }

    private static float ReadFloat(byte[] data, int offset, bool bigEndian)
    {
        if (offset + 4 > data.Length) return 0;
        var value = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        return BitConverter.Int32BitsToSingle((int)value);
    }

    private static bool IsUnsetFloat(float value)
    {
        const float unsetFloatThreshold = 1e20f;
        return float.IsNaN(value) || value <= -unsetFloatThreshold || value >= unsetFloatThreshold;
    }

    private static Dictionary<uint, List<CellGrid>> BuildExteriorCellsByWorldFromOutput(byte[] data, bool bigEndian)
    {
        var map = new Dictionary<uint, List<CellGrid>>();
        var header = EsmParser.ParseFileHeader(data);
        if (header == null) return map;

        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null) return map;

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;
        var stack = new Stack<(int end, int type, uint label)>();

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            while (stack.Count > 0 && offset >= stack.Peek().end)
                stack.Pop();

            var groupHeader = EsmParser.ParseGroupHeader(data.AsSpan(offset), bigEndian);
            if (groupHeader != null)
            {
                var groupEnd = offset + (int)groupHeader.GroupSize;
                if (groupEnd <= offset || groupEnd > data.Length)
                    break;

                var labelValue = BinaryPrimitives.ReadUInt32LittleEndian(groupHeader.Label);
                stack.Push((groupEnd, groupHeader.GroupType, labelValue));
                offset += EsmParser.MainRecordHeaderSize;
                continue;
            }

            var recordHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (recordHeader == null)
                break;

            if (recordHeader.Signature == "CELL")
            {
                var worldId = GetCurrentWorldId(stack);
                if (worldId.HasValue)
                {
                    var recInfo = new AnalyzerRecordInfo
                    {
                        Signature = recordHeader.Signature,
                        FormId = recordHeader.FormId,
                        Flags = recordHeader.Flags,
                        DataSize = recordHeader.DataSize,
                        Offset = (uint)offset,
                        TotalSize = EsmParser.MainRecordHeaderSize + recordHeader.DataSize
                    };

                    var recordData = EsmHelpers.GetRecordData(data, recInfo, bigEndian);
                    var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);
                    var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");

                    if (xclc != null && xclc.Data.Length >= 8)
                    {
                        var gridX = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(0, 4));
                        var gridY = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(4, 4));

                        if (!map.TryGetValue(worldId.Value, out var list))
                        {
                            list = [];
                            map[worldId.Value] = list;
                        }

                        list.Add(new CellGrid(recordHeader.FormId, gridX, gridY));
                    }
                }
            }

            offset += EsmParser.MainRecordHeaderSize + (int)recordHeader.DataSize;
        }

        return map;
    }

    private static List<CellGrid> BuildExteriorCellsFromAllCells(
        List<AnalyzerRecordInfo> records,
        byte[] data,
        bool bigEndian)
    {
        var list = new List<CellGrid>();

        foreach (var record in records)
        {
            if (record.Signature != "CELL")
                continue;

            var recordData = EsmHelpers.GetRecordData(data, record, bigEndian);
            var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);
            var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");
            if (xclc == null || xclc.Data.Length < 8)
                continue;

            var gridX = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(0, 4));
            var gridY = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(4, 4));

            list.Add(new CellGrid(record.FormId, gridX, gridY));
        }

        return list;
    }

    private static uint? GetCurrentWorldId(Stack<(int end, int type, uint label)> stack)
    {
        foreach (var entry in stack.Reverse())
            if (entry.type == 1)
                return entry.label;

        return null;
    }

    private sealed record CellGrid(uint FormId, int GridX, int GridY);

    #region Helpers

    private string ReadSignature(int offset)
    {
        var sigBytes = _input.AsSpan(offset, 4);
        return $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";
    }

    private static bool IsValidRecordSignature(string signature)
    {
        if (signature.Length != 4) return false;

        foreach (var ch in signature)
            if (ch is < 'A' or > 'Z' && ch is < '0' or > '9')
                return false;

        return true;
    }

    private bool TryResyncToNextGrup(ref int inputOffset)
    {
        const int headerSize = 24;

        for (var i = inputOffset + 1; i <= _input.Length - headerSize; i++)
        {
            if (_input[i] != 0x50 || _input[i + 1] != 0x55 || _input[i + 2] != 0x52 || _input[i + 3] != 0x47)
                continue;

            if (_verbose)
                Console.WriteLine($"  [0x{inputOffset:X8}] Resyncing to GRUP at 0x{i:X8}");

            inputOffset = i;
            return true;
        }

        return false;
    }

    #endregion

    #region Main Conversion Loop Helpers

    private void FinalizeCompletedOutputGroups(Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack,
        int inputOffset)
    {
        while (grupStack.Count > 0 && inputOffset >= grupStack.Peek().inputGrupEnd)
        {
            var (headerPos, _) = grupStack.Pop();
            EsmGrupWriter.FinalizeGrup(_writer, headerPos);
        }
    }

    private bool TrySkipToftRegion(string signature, Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack,
        ref int inputOffset)
    {
        if (signature != "TOFT" || grupStack.Count != 0) return false;

        var toftStartOffset = inputOffset;

        while (inputOffset + 4 <= _input.Length)
        {
            var checkSignature = ReadSignature(inputOffset);

            if (checkSignature == "GRUP") break;

            if (inputOffset + 24 > _input.Length) break;

            var skipSize = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(inputOffset + 4));
            _stats.IncrementSkippedRecordType(checkSignature);

            inputOffset += 24 + (int)skipSize;
        }

        _stats.ToftTrailingBytesSkipped = inputOffset - toftStartOffset;

        if (_verbose)
        {
            Console.WriteLine($"  [0x{toftStartOffset:X8}] Skipping Xbox TOFT region (streaming cache)");
            Console.WriteLine($"  Skipped {_stats.ToftTrailingBytesSkipped:N0} bytes, resuming at 0x{inputOffset:X8}");
            foreach (var (type, cnt) in _stats.SkippedRecordTypeCounts.OrderByDescending(x => x.Value).Take(5))
                Console.WriteLine($"    Skipped {cnt:N0} {type} records");
        }

        return true;
    }

    private bool TrySkipTopLevelRecord(string signature, Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack,
        ref int inputOffset)
    {
        if (grupStack.Count != 0 || signature == "GRUP") return false;
        if (!IsValidRecordSignature(signature)) return false;

        const int recordHeaderSize = 24;
        if (inputOffset + recordHeaderSize > _input.Length) return true;

        var dataSize = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(inputOffset + 4));

        // Use long to avoid overflow when dataSize > int.MaxValue
        var recordEnd = (long)inputOffset + recordHeaderSize + dataSize;

        // Sanity check: if record extends past file end, it's orphan data - try to resync
        if (recordEnd > _input.Length)
        {
            if (_verbose)
                Console.WriteLine(
                    $"  [0x{inputOffset:X8}] Record {signature} size {dataSize:N0} exceeds file, resyncing...");

            return TryResyncToNextGrup(ref inputOffset);
        }

        _stats.IncrementSkippedRecordType(signature);
        _stats.TopLevelRecordsSkipped++;

        if (_verbose)
            Console.WriteLine($"  [0x{inputOffset:X8}] Skipping top-level record {signature} (size={dataSize:N0})");

        inputOffset = (int)recordEnd;
        return true;
    }

    private bool TryHandleGrup(string signature, int grupHeaderSize, ConversionIndex index,
        Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack, ref int inputOffset)
    {
        if (signature != "GRUP") return false;

        if (inputOffset + grupHeaderSize > _input.Length) return true;

        var grupSize = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(inputOffset + 4));
        var grupType = (int)BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(inputOffset + 12));
        var labelBytesForWrite = _input.AsSpan(inputOffset + 8, 4).ToArray();
        var labelSignature =
            $"{(char)labelBytesForWrite[3]}{(char)labelBytesForWrite[2]}{(char)labelBytesForWrite[1]}{(char)labelBytesForWrite[0]}";

        // Handle WRLD top-level group with reconstruction
        if (grupType == 0 && labelSignature == "WRLD")
        {
            WriteTopLevelGrupWithReconstruction(inputOffset, grupSize, labelBytesForWrite, grupType,
                () => _grupWriter.WriteWorldGroupContents(index, _writer));
            inputOffset += (int)grupSize;
            return true;
        }

        // Handle CELL top-level group with reconstruction
        if (grupType == 0 && labelSignature == "CELL")
        {
            WriteTopLevelGrupWithReconstruction(inputOffset, grupSize, labelBytesForWrite, grupType,
                () => _grupWriter.WriteCellGroupContents(index, _writer));
            inputOffset += (int)grupSize;
            return true;
        }

        // Skip nested-only groups at top level
        if (grupStack.Count == 0 && IsNestedOnlyGrupType(grupType))
        {
            var labelValue = BinaryPrimitives.ReadUInt32BigEndian(labelBytesForWrite);
            SkipTopLevelGrup(grupType, labelValue, grupSize, ref inputOffset);
            return true;
        }

        WriteGrupHeaderAndPush(grupStack, grupType, grupSize, labelBytesForWrite, inputOffset);
        inputOffset += grupHeaderSize;
        return true;
    }

    private void WriteTopLevelGrupWithReconstruction(int inputOffset, uint grupSize, byte[] labelBytesForWrite,
        int grupType, Action writeContents)
    {
        var stampValue = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(inputOffset + 16));
        var unknownValue = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(inputOffset + 20));
        var grupHeaderPosition = _writer.BaseStream.Position;

        _writer.Write("GRUP"u8);
        _writer.Write(grupSize);
        _writer.Write(labelBytesForWrite[3]);
        _writer.Write(labelBytesForWrite[2]);
        _writer.Write(labelBytesForWrite[1]);
        _writer.Write(labelBytesForWrite[0]);
        _writer.Write((uint)grupType);
        _writer.Write(stampValue);
        _writer.Write(unknownValue);
        _stats.GrupsConverted++;

        writeContents();
        EsmGrupWriter.FinalizeGrup(_writer, grupHeaderPosition);
    }

    private void SkipTopLevelGrup(int grupType, uint labelValue, uint grupSize, ref int inputOffset)
    {
        var skipGrupEnd = inputOffset + (int)grupSize;

        if (_verbose)
            Console.WriteLine(
                $"  [0x{inputOffset:X8}] Skipping top-level {GetGrupTypeName(grupType)} GRUP (label=0x{labelValue:X8}, size={grupSize:N0})");

        _stats.IncrementSkippedGrupType(grupType);
        _stats.TopLevelGrupsSkipped++;

        inputOffset = skipGrupEnd;
    }

    private void WriteGrupHeaderAndPush(Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack, int grupType,
        uint grupSize, byte[] labelBytesForWrite, int inputOffset)
    {
        var stamp = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(inputOffset + 16));
        var unknown = BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(inputOffset + 20));

        var grupHeaderPosition = _grupWriter.WriteGrupHeader(_writer, grupType, labelBytesForWrite, stamp, unknown);

        var grupEnd = inputOffset + (int)grupSize;
        grupStack.Push((grupHeaderPosition, grupEnd));
    }

    #endregion
}