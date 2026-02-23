using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using static FalloutXbox360Utils.Core.Formats.Esm.Conversion.EsmEndianHelpers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

/// <summary>
///     GRUP-level dispatch helpers for ESM conversion.
///     Handles skipping, resyncing, and writing GRUP structures during the main conversion loop.
/// </summary>
internal sealed class EsmConverterGrupHandler
{
    private readonly EsmGrupWriter _grupWriter;
    private readonly byte[] _input;
    private readonly EsmConversionStats _stats;
    private readonly bool _verbose;

    public EsmConverterGrupHandler(byte[] input, EsmGrupWriter grupWriter, EsmConversionStats stats, bool verbose)
    {
        _input = input;
        _grupWriter = grupWriter;
        _stats = stats;
        _verbose = verbose;
    }

    /// <summary>
    ///     Pops and finalizes any output GRUPs whose input boundaries have been reached.
    /// </summary>
    public static void FinalizeCompletedOutputGroups(
        BinaryWriter writer,
        Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack,
        int inputOffset)
    {
        while (grupStack.Count > 0 && inputOffset >= grupStack.Peek().inputGrupEnd)
        {
            var (headerPos, _) = grupStack.Pop();
            EsmGrupWriter.FinalizeGrup(writer, headerPos);
        }
    }

    /// <summary>
    ///     Skips the Xbox TOFT streaming cache region at the top level.
    ///     Returns true if a TOFT region was consumed.
    /// </summary>
    public bool TrySkipToftRegion(
        string signature,
        Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack,
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

    /// <summary>
    ///     Skips a valid record at the top level (outside any GRUP).
    ///     Returns true if the record was skipped.
    /// </summary>
    public bool TrySkipTopLevelRecord(
        string signature,
        Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack,
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

    /// <summary>
    ///     Handles a GRUP signature: dispatches to reconstruction, skip, or standard write.
    ///     Returns true if the GRUP was handled.
    /// </summary>
    public bool TryHandleGrup(
        string signature,
        int grupHeaderSize,
        ConversionIndex index,
        Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack,
        BinaryWriter writer,
        ref int inputOffset)
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
                writer, () => _grupWriter.WriteWorldGroupContents(index, writer));
            inputOffset += (int)header.GroupSize;
            return true;
        }

        // Handle CELL top-level group with reconstruction
        if (header.GroupType == 0 && labelSignature == "CELL")
        {
            WriteTopLevelGrupWithReconstruction(inputOffset, header.GroupSize, labelBytesForWrite, header.GroupType,
                writer, () => _grupWriter.WriteCellGroupContents(index, writer));
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

        WriteGrupHeaderAndPush(grupStack, header.GroupType, header.GroupSize, labelBytesForWrite, inputOffset, writer);
        inputOffset += grupHeaderSize;
        return true;
    }

    /// <summary>
    ///     Scans forward to find the next GRUP signature for resync after unexpected data.
    /// </summary>
    public bool TryResyncToNextGrup(ref int inputOffset)
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

    /// <summary>
    ///     Checks whether a 4-character signature represents a valid record type.
    /// </summary>
    public static bool IsValidRecordSignature(string signature)
    {
        if (signature.Length != 4)
        {
            return false;
        }

        return !signature.Any(ch => ch is (< 'A' or > 'Z') and (< '0' or > '9'));
    }

    /// <summary>
    ///     Reads a 4-byte big-endian signature from the input at the given offset.
    /// </summary>
    public string ReadSignature(int offset)
    {
        var sigBytes = _input.AsSpan(offset, 4);
        return $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";
    }

    private void WriteTopLevelGrupWithReconstruction(
        int inputOffset, uint grupSize, byte[] labelBytesForWrite,
        int grupType, BinaryWriter writer, Action writeContents)
    {
        var header = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(inputOffset));
        var grupHeaderPosition = writer.BaseStream.Position;

        // The GRUP label field is a 4-byte value stored big-endian in Xbox data.
        // Written as little-endian for PC output, naturally flipping the byte order.
        var labelValue = BinaryPrimitives.ReadUInt32BigEndian(labelBytesForWrite);
        var outputHeader = new GroupHeader
        {
            GroupSize = grupSize,
            Label = BitConverter.GetBytes(labelValue),
            GroupType = grupType,
            Stamp = header.Stamp,
            Unknown = header.Unknown
        };
        _ = RecordHeaderProcessor.WriteGrupHeader(writer.BaseStream, outputHeader);
        _stats.GrupsConverted++;

        writeContents();
        EsmGrupWriter.FinalizeGrup(writer, grupHeaderPosition);
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

    private void WriteGrupHeaderAndPush(
        Stack<(long outputHeaderPos, int inputGrupEnd)> grupStack,
        int grupType, uint grupSize, byte[] labelBytesForWrite,
        int inputOffset, BinaryWriter writer)
    {
        var header = RecordHeaderProcessor.ReadGrupHeader(_input.AsSpan(inputOffset));

        var grupHeaderPosition =
            _grupWriter.WriteGrupHeader(writer, grupType, labelBytesForWrite, header.Stamp, header.Unknown);

        var grupEnd = inputOffset + (int)grupSize;
        grupStack.Push((grupHeaderPosition, grupEnd));
    }
}
