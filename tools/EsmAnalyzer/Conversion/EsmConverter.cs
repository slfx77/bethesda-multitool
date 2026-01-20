using System.Buffers.Binary;
using System.Linq;
using Spectre.Console;
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
                    Console.WriteLine($"  [0x{inputOffset:X8}] Invalid top-level signature '{signature}', stopping conversion.");

                break;
            }

            if (TrySkipTopLevelRecord(signature, grupStack, ref inputOffset)) continue;

            if (TryHandleGrup(signature, grupHeaderSize, index, grupStack, ref inputOffset)) continue;

            inputOffset = ConvertRecord(inputOffset);
        }

        // Finalize any remaining GRUPs
        while (grupStack.Count > 0)
        {
            var (headerPos, _) = grupStack.Pop();
            _grupWriter.FinalizeGrup(_writer, headerPos);
        }

        return _output.ToArray();
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
        {
            if (ch is < 'A' or > 'Z' && ch is < '0' or > '9')
                return false;
        }

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
            _grupWriter.FinalizeGrup(_writer, headerPos);
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
        var recordEnd = inputOffset + recordHeaderSize + (int)dataSize;
        if (recordEnd > _input.Length) recordEnd = _input.Length;

        _stats.IncrementSkippedRecordType(signature);
        _stats.TopLevelRecordsSkipped++;

        if (_verbose)
            Console.WriteLine($"  [0x{inputOffset:X8}] Skipping top-level record {signature} (size={dataSize:N0})");

        inputOffset = recordEnd;
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
        _grupWriter.FinalizeGrup(_writer, grupHeaderPosition);
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