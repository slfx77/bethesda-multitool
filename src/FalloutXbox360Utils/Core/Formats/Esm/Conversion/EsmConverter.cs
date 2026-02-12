using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using static FalloutXbox360Utils.Core.Formats.Esm.Conversion.EsmEndianHelpers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

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

    private void Log(string message)
    {
        if (_verbose)
        {
            Console.WriteLine(message);
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

        var toftInfoOffsetsByFormId = BuildToftInfoIndex();
        if (toftInfoOffsetsByFormId.Count > 0)
        {
            _recordWriter.SetToftInfoIndex(toftInfoOffsetsByFormId);
            if (_verbose)
            {
                Log($"TOFT: indexed {toftInfoOffsetsByFormId.Count:N0} INFO records from streaming cache");
            }
        }

        if (_verbose)
        {
            var exteriorCells = index.ExteriorCellsByWorld.Sum(kvp => kvp.Value.Count);
            var temporaryGroups = index.CellChildGroups.Count(kvp => kvp.Key.type == 9);
            var persistentGroups = index.CellChildGroups.Count(kvp => kvp.Key.type == 8);
            var totalChildRecords = index.CellChildGroups.Sum(kvp => kvp.Value.Sum(g => g.Size));
            Log(
                $"Index: worlds={index.Worlds.Count}, interiorCells={index.InteriorCells.Count}, exteriorCells={exteriorCells}, exteriorWorlds={index.ExteriorCellsByWorld.Count}");
            foreach (var kvp in index.ExteriorCellsByWorld)
            {
                Console.WriteLine($"  World 0x{kvp.Key:X8}: {kvp.Value.Count} exterior cells");
            }

            Log(
                $"CellChildGroups: persistent={persistentGroups}, temporary={temporaryGroups}, totalSize={totalChildRecords:N0} bytes");
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

            if (inputOffset + 4 > _input.Length)
            {
                break;
            }

            var signature = ReadSignature(inputOffset);

            if (TrySkipToftRegion(signature, grupStack, ref inputOffset))
            {
                continue;
            }

            if (grupStack.Count == 0 && !IsValidRecordSignature(signature))
            {
                if (TryResyncToNextGrup(ref inputOffset))
                {
                    continue;
                }

                if (_verbose)
                {
                    Console.WriteLine(
                        $"  [0x{inputOffset:X8}] Non-record data '{signature}' encountered; stopping conversion.");
                }

                break;
            }

            if (TrySkipTopLevelRecord(signature, grupStack, ref inputOffset))
            {
                continue;
            }

            // If at top level and not a GRUP, try resyncing (likely orphaned data)
            if (grupStack.Count == 0 && signature != "GRUP")
            {
                if (TryResyncToNextGrup(ref inputOffset))
                {
                    continue;
                }

                if (_verbose)
                {
                    Console.WriteLine(
                        $"  [0x{inputOffset:X8}] Cannot resync after '{signature}', stopping conversion.");
                }

                break;
            }

            if (TryHandleGrup(signature, grupHeaderSize, index, grupStack, ref inputOffset))
            {
                continue;
            }

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

    private Dictionary<uint, int> BuildToftInfoIndex()
    {
        const int recordHeaderSize = 24;
        const int grupHeaderSize = 24;

        var map = new Dictionary<uint, int>();
        var grupEndStack = new Stack<int>();

        if (_input.Length < recordHeaderSize)
        {
            return map;
        }

        // Skip TES4 header record
        var tes4Header = RecordHeaderProcessor.ReadRecordHeader(_input.AsSpan(0));
        var offset = recordHeaderSize + (int)tes4Header.DataSize;
        if (offset < 0 || offset > _input.Length)
        {
            return map;
        }

        while (offset + 4 <= _input.Length)
        {
            while (grupEndStack.Count > 0 && offset >= grupEndStack.Peek())
            {
                grupEndStack.Pop();
            }

            var signature = ReadSignature(offset);

            if (signature == "GRUP")
            {
                if (offset + grupHeaderSize > _input.Length)
                {
                    break;
                }

                var header = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(offset));
                var end = offset + (int)header.GroupSize;
                if (end <= offset || end > _input.Length)
                {
                    break;
                }

                grupEndStack.Push(end);
                offset += grupHeaderSize;
                continue;
            }

            // TOFT region is top-level only — collect all INFO records from it, then stop
            if (grupEndStack.Count == 0 && signature == "TOFT")
            {
                CollectToftRegionRecords(offset, map);
                break;
            }

            if (offset + recordHeaderSize > _input.Length)
            {
                break;
            }

            var rec = RecordHeaderProcessor.ReadRecordHeader(_input.AsSpan(offset));
            var recordEnd = offset + recordHeaderSize + (int)rec.DataSize;
            if (recordEnd <= offset || recordEnd > _input.Length)
            {
                break;
            }

            offset = recordEnd;
        }

        return map;
    }

    /// <summary>
    ///     Scans the TOFT streaming cache region for INFO records.
    ///     TOFT records are container markers — INFO records live inside their data blocks.
    /// </summary>
    private void CollectToftRegionRecords(int startOffset, Dictionary<uint, int> map)
    {
        const int recordHeaderSize = 24;
        var cur = startOffset;

        while (cur + 4 <= _input.Length)
        {
            var sig = ReadSignature(cur);
            if (sig == "GRUP")
            {
                break;
            }

            if (cur + recordHeaderSize > _input.Length)
            {
                break;
            }

            var header = RecordHeaderProcessor.ReadRecordHeader(_input.AsSpan(cur));

            if (sig == "TOFT")
            {
                var toftDataStart = cur + recordHeaderSize;
                var toftDataEnd = toftDataStart + (int)header.DataSize;

                if (toftDataEnd > _input.Length || toftDataEnd <= toftDataStart)
                {
                    cur += recordHeaderSize;
                    continue;
                }

                ScanInfoRecordsInsideToftBlock(toftDataStart, toftDataEnd, map);
                cur = toftDataEnd;
                continue;
            }

            // Non-TOFT record (INFO, etc.) directly in the TOFT region
            if (sig == "INFO")
            {
                _ = map.TryAdd(header.FormId, cur);
            }

            var next = cur + recordHeaderSize + (int)header.DataSize;
            if (next <= cur || next > _input.Length)
            {
                break;
            }

            cur = next;
        }
    }

    /// <summary>
    ///     Scans INFO records nested inside a single TOFT block's data area.
    /// </summary>
    private void ScanInfoRecordsInsideToftBlock(int dataStart, int dataEnd, Dictionary<uint, int> map)
    {
        const int recordHeaderSize = 24;
        var inner = dataStart;

        while (inner + recordHeaderSize <= dataEnd)
        {
            var sig = ReadSignature(inner);
            if (sig == "GRUP")
            {
                break;
            }

            var header = RecordHeaderProcessor.ReadRecordHeader(_input.AsSpan(inner));
            if (sig == "INFO")
            {
                _ = map.TryAdd(header.FormId, inner);
            }

            var next = inner + recordHeaderSize + (int)header.DataSize;
            if (next <= inner || next > dataEnd)
            {
                break;
            }

            inner = next;
        }
    }

    /// <summary>
    ///     Returns conversion statistics summary.
    /// </summary>
    public string GetStatsSummary()
    {
        return _stats.GetStatsSummary(_verbose);
    }

    #region Record Conversion

    private int ConvertRecord(int offset)
    {
        var buffer = _recordWriter.ConvertRecordToBuffer(offset, out var recordEnd, out _);
        if (buffer != null)
        {
            _writer.Write(buffer);
        }

        if (_verbose && _stats.RecordsConverted % 10000 == 0)
        {
            Log($"  Converted {_stats.RecordsConverted:N0} records...");
        }

        return recordEnd;
    }

    #endregion

    private void RebuildOfstTables(byte[] output, ConversionIndex index)
    {
        var outputHeader = EsmParser.ParseFileHeader(output);
        if (outputHeader == null || outputHeader.IsBigEndian)
        {
            return;
        }

        var outputRecords = EsmRecordParser.ScanAllRecords(output, outputHeader.IsBigEndian);
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
            Log($"OFST Rebuild: {outputWrlds.Count} WRLD records, {cellRecordOffsets.Count} CELL records");
            Log($"  outputExteriorCellsByWorld entries: {outputExteriorCellsByWorld.Count}");
            foreach (var kvp in outputExteriorCellsByWorld.Take(3))
            {
                Log($"    World 0x{kvp.Key:X8}: {kvp.Value.Count} cells");
            }

            Log($"  fallbackExteriorCells: {fallbackExteriorCells.Count}");
        }

        foreach (var wrld in outputWrlds)
        {
            RebuildOfstForWorld(output, wrld, outputHeader.IsBigEndian,
                indexExteriorCellsByWorld, outputExteriorCellsByWorld,
                fallbackExteriorCells, cellRecordOffsets);
        }
    }


    /// <summary>
    ///     Rebuilds the OFST (cell offset table) for a single WRLD record.
    /// </summary>
    private void RebuildOfstForWorld(
        byte[] output,
        AnalyzerRecordInfo wrld,
        bool bigEndian,
        Dictionary<uint, List<CellGrid>> indexExteriorCellsByWorld,
        Dictionary<uint, List<CellGrid>> outputExteriorCellsByWorld,
        List<CellGrid> fallbackExteriorCells,
        Dictionary<uint, uint> cellRecordOffsets)
    {
        var exteriorCells = ResolveExteriorCellsForWorld(
            wrld.FormId, indexExteriorCellsByWorld, outputExteriorCellsByWorld, fallbackExteriorCells);

        if (exteriorCells.Count == 0 || (wrld.Flags & 0x00040000) != 0)
        {
            return;
        }

        var wrldData = EsmHelpers.GetRecordData(output, wrld, bigEndian);
        var subs = EsmRecordParser.ParseSubrecords(wrldData, bigEndian);
        var ofst = subs.FirstOrDefault(s => s.Signature == "OFST");
        if (ofst == null || ofst.Data.Length == 0)
        {
            return;
        }

        if (!TryGetWorldBounds(subs, bigEndian, out var minX, out var maxX, out var minY, out var maxY))
        {
            if (_verbose)
            {
                Log($"  WRLD 0x{wrld.FormId:X8}: failed to get bounds");
            }

            return;
        }

        if (!TryCalculateGridDimensions(ofst.Data.Length, maxX - minX + 1, maxY - minY + 1,
                out var columns, out var rows, out var count))
        {
            return;
        }

        var offsets = BuildOfstOffsetArray(exteriorCells, cellRecordOffsets, wrld.Offset,
            minX, minY, columns, rows, count);

        var ofstDataOffset = (long)wrld.Offset + EsmParser.MainRecordHeaderSize + ofst.Offset + 6;
        if (ofstDataOffset < 0 || ofstDataOffset + (long)count * 4 > output.Length)
        {
            return;
        }

        for (var i = 0; i < offsets.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan((int)ofstDataOffset + i * 4, 4),
                offsets[i]);
        }
    }

    /// <summary>
    ///     Resolves the exterior cells for a world by merging index, output, and fallback sources.
    /// </summary>
    private static List<CellGrid> ResolveExteriorCellsForWorld(
        uint worldFormId,
        Dictionary<uint, List<CellGrid>> indexExteriorCellsByWorld,
        Dictionary<uint, List<CellGrid>> outputExteriorCellsByWorld,
        List<CellGrid> fallbackExteriorCells)
    {
        if (!indexExteriorCellsByWorld.TryGetValue(worldFormId, out var exteriorCells)
            && !outputExteriorCellsByWorld.TryGetValue(worldFormId, out exteriorCells))
        {
            exteriorCells = [];
        }

        if (fallbackExteriorCells.Count == 0 && exteriorCells.Count == 0)
        {
            return [];
        }

        if (fallbackExteriorCells.Count > 0)
        {
            var merged = new Dictionary<uint, CellGrid>();
            foreach (var cell in exteriorCells)
            {
                merged[cell.FormId] = cell;
            }

            foreach (var cell in fallbackExteriorCells)
            {
                _ = merged.TryAdd(cell.FormId, cell);
            }

            return merged.Values.ToList();
        }

        return exteriorCells;
    }

    /// <summary>
    ///     Calculates grid dimensions from OFST data, adjusting if the expected count doesn't match.
    /// </summary>
    private static bool TryCalculateGridDimensions(
        int ofstDataLength, int initialColumns, int initialRows,
        out int columns, out int rows, out int count)
    {
        columns = initialColumns;
        rows = initialRows;
        count = ofstDataLength / 4;

        if (columns <= 0 || rows <= 0 || count <= 0)
        {
            return false;
        }

        var expected = columns * rows;
        if (expected == count)
        {
            return true;
        }

        if (count % columns == 0)
        {
            rows = count / columns;
        }
        else if (count % rows == 0)
        {
            columns = count / rows;
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Builds the OFST offset array mapping grid positions to cell record offsets relative to the WRLD record.
    /// </summary>
    private static uint[] BuildOfstOffsetArray(
        List<CellGrid> exteriorCells,
        Dictionary<uint, uint> cellRecordOffsets,
        uint wrldOffset,
        int minX, int minY,
        int columns, int rows, int count)
    {
        var offsets = new uint[count];
        var bestByIndex = new Dictionary<int, uint>();

        foreach (var cell in exteriorCells)
        {
            var col = cell.GridX - minX;
            var row = cell.GridY - minY;
            if (col < 0 || col >= columns || row < 0 || row >= rows)
            {
                continue;
            }

            var ofstIndex = row * columns + col;
            if (ofstIndex < 0 || ofstIndex >= count)
            {
                continue;
            }

            if (!cellRecordOffsets.TryGetValue(cell.FormId, out var cellOffset))
            {
                continue;
            }

            var rel = cellOffset - (long)wrldOffset;
            if (rel is <= 0 or > uint.MaxValue)
            {
                continue;
            }

            var relValue = (uint)rel;
            if (!bestByIndex.TryGetValue(ofstIndex, out var existing) || relValue < existing)
            {
                bestByIndex[ofstIndex] = relValue;
                offsets[ofstIndex] = relValue;
            }
        }

        return offsets;
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
        {
            return false;
        }

        var minXf = ReadFloat(nam0.Data, 0, bigEndian);
        var minYf = ReadFloat(nam0.Data, 4, bigEndian);
        var maxXf = ReadFloat(nam9.Data, 0, bigEndian);
        var maxYf = ReadFloat(nam9.Data, 4, bigEndian);

        if (IsUnsetFloat(minXf))
        {
            minXf = 0;
        }

        if (IsUnsetFloat(minYf))
        {
            minYf = 0;
        }

        if (IsUnsetFloat(maxXf))
        {
            maxXf = 0;
        }

        if (IsUnsetFloat(maxYf))
        {
            maxYf = 0;
        }

        const float cellScale = 4096f;
        minX = (int)Math.Round(minXf / cellScale);
        minY = (int)Math.Round(minYf / cellScale);
        maxX = (int)Math.Round(maxXf / cellScale);
        maxY = (int)Math.Round(maxYf / cellScale);

        return true;
    }

    private static float ReadFloat(byte[] data, int offset, bool bigEndian)
    {
        if (offset + 4 > data.Length)
        {
            return 0;
        }

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
        if (header == null)
        {
            return map;
        }

        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null)
        {
            return map;
        }

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;
        var stack = new Stack<(int end, int type, uint label)>();

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            while (stack.Count > 0 && offset >= stack.Peek().end)
            {
                _ = stack.Pop();
            }

            var groupHeader = EsmParser.ParseGroupHeader(data.AsSpan(offset), bigEndian);
            if (groupHeader != null)
            {
                var groupEnd = offset + (int)groupHeader.GroupSize;
                if (groupEnd <= offset || groupEnd > data.Length)
                {
                    break;
                }

                var labelValue = BinaryPrimitives.ReadUInt32LittleEndian(groupHeader.Label);
                stack.Push((groupEnd, groupHeader.GroupType, labelValue));
                offset += EsmParser.MainRecordHeaderSize;
                continue;
            }

            var recordHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (recordHeader == null)
            {
                break;
            }

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
                    var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
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
            {
                continue;
            }

            var recordData = EsmHelpers.GetRecordData(data, record, bigEndian);
            var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
            var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");
            if (xclc == null || xclc.Data.Length < 8)
            {
                continue;
            }

            var gridX = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(0, 4));
            var gridY = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(4, 4));

            list.Add(new CellGrid(record.FormId, gridX, gridY));
        }

        return list;
    }

    private static uint? GetCurrentWorldId(Stack<(int end, int type, uint label)> stack)
    {
        foreach (var (_, type, label) in stack.Reverse())
        {
            if (type == 1)
            {
                return label;
            }
        }

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
        if (signature.Length != 4)
        {
            return false;
        }

        return !signature.Any(ch => ch is (< 'A' or > 'Z') and (< '0' or > '9'));
    }

    private bool TryResyncToNextGrup(ref int inputOffset)
    {
        const int headerSize = 24;

        for (var i = inputOffset + 1; i <= _input.Length - headerSize; i++)
        {
            if (_input[i] != 0x50 || _input[i + 1] != 0x55 || _input[i + 2] != 0x52 || _input[i + 3] != 0x47)
            {
                continue;
            }

            if (_verbose)
            {
                Console.WriteLine($"  [0x{inputOffset:X8}] Resyncing to GRUP at 0x{i:X8}");
            }

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
        if (signature != "TOFT" || grupStack.Count != 0)
        {
            return false;
        }

        var toftStartOffset = inputOffset;

        while (inputOffset + 4 <= _input.Length)
        {
            var checkSignature = ReadSignature(inputOffset);

            if (checkSignature == "GRUP")
            {
                break;
            }

            if (inputOffset + 24 > _input.Length)
            {
                break;
            }

            var header = RecordHeaderProcessor.ReadRecordHeader(_input.AsSpan(inputOffset));
            _stats.IncrementSkippedRecordType(checkSignature);

            inputOffset += 24 + (int)header.DataSize;
        }

        _stats.ToftTrailingBytesSkipped = inputOffset - toftStartOffset;

        if (_verbose)
        {
            Console.WriteLine(
                $"  [0x{toftStartOffset:X8}] Consuming Xbox TOFT region (streaming cache) for INFO merge");
            Console.WriteLine(
                $"  Skipped writing {_stats.ToftTrailingBytesSkipped:N0} bytes, resuming at 0x{inputOffset:X8}");
            foreach (var (type, cnt) in _stats.SkippedRecordTypeCounts.OrderByDescending(x => x.Value).Take(5))
            {
                Console.WriteLine($"    Skipped writing {cnt:N0} {type} records");
            }
        }

        return true;
    }

    private bool TrySkipTopLevelRecord(string signature, Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack,
        ref int inputOffset)
    {
        if (grupStack.Count != 0 || signature == "GRUP")
        {
            return false;
        }

        if (!IsValidRecordSignature(signature))
        {
            return false;
        }

        const int recordHeaderSize = 24;
        if (inputOffset + recordHeaderSize > _input.Length)
        {
            return true;
        }

        var header = RecordHeaderProcessor.ReadRecordHeader(_input.AsSpan(inputOffset));

        // Use long to avoid overflow when dataSize > int.MaxValue
        var recordEnd = (long)inputOffset + recordHeaderSize + header.DataSize;

        // Sanity check: if record extends past file end, it's orphan data - try to resync
        if (recordEnd > _input.Length)
        {
            if (_verbose)
            {
                Console.WriteLine(
                    $"  [0x{inputOffset:X8}] Record {signature} size {header.DataSize:N0} exceeds file, resyncing...");
            }

            return TryResyncToNextGrup(ref inputOffset);
        }

        _stats.IncrementSkippedRecordType(signature);
        _stats.TopLevelRecordsSkipped++;

        if (_verbose)
        {
            Console.WriteLine(
                $"  [0x{inputOffset:X8}] Skipping top-level record {signature} (size={header.DataSize:N0})");
        }

        inputOffset = (int)recordEnd;
        return true;
    }

    private bool TryHandleGrup(string signature, int grupHeaderSize, ConversionIndex index,
        Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack, ref int inputOffset)
    {
        if (signature != "GRUP")
        {
            return false;
        }

        if (inputOffset + grupHeaderSize > _input.Length)
        {
            return true;
        }

        var header = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(inputOffset));
        var labelBytesForWrite = _input.AsSpan(inputOffset + 8, 4).ToArray();
        var labelSignature =
            $"{(char)labelBytesForWrite[3]}{(char)labelBytesForWrite[2]}{(char)labelBytesForWrite[1]}{(char)labelBytesForWrite[0]}";

        // Handle WRLD top-level group with reconstruction
        if (header.GroupType == 0 && labelSignature == "WRLD")
        {
            WriteTopLevelGrupWithReconstruction(inputOffset, header.GroupSize, labelBytesForWrite, header.GroupType,
                () => _grupWriter.WriteWorldGroupContents(index, _writer));
            inputOffset += (int)header.GroupSize;
            return true;
        }

        // Handle CELL top-level group with reconstruction
        if (header.GroupType == 0 && labelSignature == "CELL")
        {
            WriteTopLevelGrupWithReconstruction(inputOffset, header.GroupSize, labelBytesForWrite, header.GroupType,
                () => _grupWriter.WriteCellGroupContents(index, _writer));
            inputOffset += (int)header.GroupSize;
            return true;
        }

        // Skip nested-only groups at top level
        if (grupStack.Count == 0 && IsNestedOnlyGrupType(header.GroupType))
        {
            var labelValue = BinaryPrimitives.ReadUInt32BigEndian(labelBytesForWrite);
            SkipTopLevelGrup(header.GroupType, labelValue, header.GroupSize, ref inputOffset);
            return true;
        }

        WriteGrupHeaderAndPush(grupStack, header.GroupType, header.GroupSize, labelBytesForWrite, inputOffset);
        inputOffset += grupHeaderSize;
        return true;
    }

    private void WriteTopLevelGrupWithReconstruction(int inputOffset, uint grupSize, byte[] labelBytesForWrite,
        int grupType, Action writeContents)
    {
        var header = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(inputOffset));
        var grupHeaderPosition = _writer.BaseStream.Position;

        // The GRUP label field is a 4-byte value stored big-endian in Xbox data.
        // We write it little-endian for PC output, which naturally flips the byte order.
        var labelValue = BinaryPrimitives.ReadUInt32BigEndian(labelBytesForWrite);
        var outputHeader = new GroupHeader
        {
            GroupSize = grupSize,
            Label = BitConverter.GetBytes(labelValue),
            GroupType = grupType,
            Stamp = header.Stamp,
            Unknown = header.Unknown
        };
        _ = RecordHeaderProcessor.WriteGrupHeader(_writer.BaseStream, outputHeader);
        _stats.GrupsConverted++;

        writeContents();
        EsmGrupWriter.FinalizeGrup(_writer, grupHeaderPosition);
    }

    private void SkipTopLevelGrup(int grupType, uint labelValue, uint grupSize, ref int inputOffset)
    {
        var skipGrupEnd = inputOffset + (int)grupSize;

        if (_verbose)
        {
            Console.WriteLine(
                $"  [0x{inputOffset:X8}] Skipping top-level {GetGrupTypeName(grupType)} GRUP (label=0x{labelValue:X8}, size={grupSize:N0})");
        }

        _stats.IncrementSkippedGrupType(grupType);
        _stats.TopLevelGrupsSkipped++;

        inputOffset = skipGrupEnd;
    }

    private void WriteGrupHeaderAndPush(Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack, int grupType,
        uint grupSize, byte[] labelBytesForWrite, int inputOffset)
    {
        var header = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(inputOffset));

        var grupHeaderPosition =
            _grupWriter.WriteGrupHeader(_writer, grupType, labelBytesForWrite, header.Stamp, header.Unknown);

        var grupEnd = inputOffset + (int)grupSize;
        grupStack.Push((grupHeaderPosition, grupEnd));
    }

    #endregion
}
