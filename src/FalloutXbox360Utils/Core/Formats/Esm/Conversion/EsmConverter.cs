using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

/// <summary>
///     ESM file converter from Xbox 360 (big-endian) to PC (little-endian) format.
/// </summary>
public sealed class EsmConverter : IDisposable
{
    private readonly EsmConverterGrupHandler _grupHandler;
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
        _grupHandler = new EsmConverterGrupHandler(input, _grupWriter, _stats, verbose);
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
            EsmConverterGrupHandler.FinalizeCompletedOutputGroups(_writer, grupStack, inputOffset);

            if (inputOffset + 4 > _input.Length)
            {
                break;
            }

            var signature = _grupHandler.ReadSignature(inputOffset);

            if (_grupHandler.TrySkipToftRegion(signature, grupStack, ref inputOffset))
            {
                continue;
            }

            if (grupStack.Count == 0 && !EsmConverterGrupHandler.IsValidRecordSignature(signature))
            {
                if (_grupHandler.TryResyncToNextGrup(ref inputOffset))
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

            if (_grupHandler.TrySkipTopLevelRecord(signature, grupStack, ref inputOffset))
            {
                continue;
            }

            // If at top level and not a GRUP, try resyncing (likely orphaned data)
            if (grupStack.Count == 0 && signature != "GRUP")
            {
                if (_grupHandler.TryResyncToNextGrup(ref inputOffset))
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

            if (_grupHandler.TryHandleGrup(signature, grupHeaderSize, index, grupStack, _writer, ref inputOffset))
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
        EsmConverterOfstBuilder.RebuildOfstTables(outputBytes, index, _verbose);
        return outputBytes;
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

    #region TOFT Index Building

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

            var signature = _grupHandler.ReadSignature(offset);

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

            // TOFT region is top-level only -- collect all INFO records from it, then stop
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
    ///     TOFT records are container markers -- INFO records live inside their data blocks.
    /// </summary>
    private void CollectToftRegionRecords(int startOffset, Dictionary<uint, int> map)
    {
        const int recordHeaderSize = 24;
        var cur = startOffset;

        while (cur + 4 <= _input.Length)
        {
            var sig = _grupHandler.ReadSignature(cur);
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
            var sig = _grupHandler.ReadSignature(inner);
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

    #endregion
}
