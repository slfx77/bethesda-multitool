using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Scda;

/// <summary>
///     Bethesda compiled script bytecode (SCDA) format module.
/// </summary>
public sealed class ScdaFormat : FileFormatBase, IDumpScanner
{
    public override string FormatId => "scda";
    public override string DisplayName => "SCDA";
    public override string Extension => ".scda";
    public override FileCategory Category => FileCategory.Script;
    public override string OutputFolder => "scripts";
    public override int MinSize => 10;
    public override int MaxSize => 64 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "scda",
            MagicBytes = "SCDA"u8.ToArray(),
            Description = "Compiled Script Bytecode (SCDA)"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 10)
        {
            return null;
        }

        var span = data[offset..];
        if (!span[..4].SequenceEqual("SCDA"u8))
        {
            return null;
        }

        var length = BinaryUtils.ReadUInt16LE(span, 4);
        if (length == 0 || offset + 6 + length > data.Length)
        {
            return null;
        }

        var bytecode = span.Slice(6, length);
        if (!ValidateBytecode(bytecode))
        {
            return null;
        }

        return new ParseResult
        {
            Format = "SCDA",
            EstimatedSize = 6 + length,
            Metadata = new Dictionary<string, object>
            {
                ["bytecodeSize"] = (int)length
            }
        };
    }

    #region IDumpScanner

    /// <summary>
    ///     Scan an entire memory dump for all SCDA records using memory-mapped access.
    /// </summary>
    public object ScanDump(MemoryMappedViewAccessor accessor, long fileSize)
    {
        return ScanForRecordsMemoryMapped(accessor, fileSize);
    }

    /// <summary>
    ///     Scan an entire memory dump for all SCDA records (legacy byte array version).
    /// </summary>
    public static ScdaScanResult ScanForRecords(byte[] data)
    {
        var records = new List<ScdaRecord>();
        var i = 0;

        while (i <= data.Length - 10)
        {
            if (MatchesScdaSignature(data, i))
            {
                var record = TryParseScdaRecord(data, i);
                if (record != null)
                {
                    records.Add(record);
                    i += 6 + record.BytecodeSize;
                    continue;
                }
            }

            i++;
        }

        return new ScdaScanResult { Records = records };
    }

    /// <summary>
    ///     Scan an entire memory dump for all SCDA records using memory-mapped access.
    ///     Processes in chunks to avoid loading the entire file into memory.
    /// </summary>
    public static ScdaScanResult ScanForRecordsMemoryMapped(MemoryMappedViewAccessor accessor, long fileSize)
    {
        const int chunkSize = 16 * 1024 * 1024; // 16MB chunks
        const int overlapSize = 512; // Overlap to handle records at chunk boundaries

        var records = new List<ScdaRecord>();
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + overlapSize);

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                var toRead = (int)Math.Min(chunkSize + overlapSize, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                // Scan this chunk
                var chunkRecords = ScanChunkForRecords(buffer, toRead, offset);

                foreach (var record in chunkRecords)
                    // Only add records that start within the main chunk area (not in overlap)
                    // unless this is the last chunk
                {
                    if (record.Offset - offset < chunkSize || offset + chunkSize >= fileSize)
                    {
                        records.Add(record);
                    }
                }

                offset += chunkSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new ScdaScanResult { Records = records };
    }

    /// <summary>
    ///     Scan a chunk of data for SCDA records.
    /// </summary>
    private static List<ScdaRecord> ScanChunkForRecords(byte[] data, int dataLength, long baseOffset)
    {
        var records = new List<ScdaRecord>();
        var i = 0;
        var searchLimit = dataLength - 10;

        while (i <= searchLimit)
        {
            if (MatchesScdaSignature(data, i))
            {
                var record = TryParseScdaRecordFromChunk(data, i, dataLength, baseOffset);
                if (record != null)
                {
                    records.Add(record);
                    i += 6 + record.BytecodeSize;
                    continue;
                }
            }

            i++;
        }

        return records;
    }

    #endregion

    #region Private Implementation

    private static bool MatchesScdaSignature(byte[] data, int offset)
    {
        return data[offset] == 'S' &&
               data[offset + 1] == 'C' &&
               data[offset + 2] == 'D' &&
               data[offset + 3] == 'A';
    }

    private static ScdaRecord? TryParseScdaRecord(byte[] data, int offset)
    {
        var length = BinaryUtils.ReadUInt16LE(data, offset + 4);
        if (length == 0 || length >= 65535 || offset + 6 + length > data.Length)
        {
            return null;
        }

        var bytecodeSpan = data.AsSpan(offset + 6, length);
        if (!ValidateBytecode(bytecodeSpan))
        {
            return null;
        }

        var bytecode = new byte[length];
        Array.Copy(data, offset + 6, bytecode, 0, length);

        var (sourceText, sourceOffset) = FindAssociatedSctx(data, offset + 6 + length);
        var formIds = FindAssociatedScro(data, offset + 6 + length);

        return new ScdaRecord
        {
            Offset = offset,
            Bytecode = bytecode,
            SourceText = sourceText,
            SourceOffset = sourceOffset,
            FormIdReferences = formIds
        };
    }

    private static ScdaRecord? TryParseScdaRecordFromChunk(byte[] data, int localOffset, int dataLength,
        long baseOffset)
    {
        if (localOffset + 6 > dataLength)
        {
            return null;
        }

        var length = BinaryUtils.ReadUInt16LE(data, localOffset + 4);
        if (length == 0 || length >= 65535 || localOffset + 6 + length > dataLength)
        {
            return null;
        }

        var bytecodeSpan = data.AsSpan(localOffset + 6, length);
        if (!ValidateBytecode(bytecodeSpan))
        {
            return null;
        }

        var bytecode = new byte[length];
        Array.Copy(data, localOffset + 6, bytecode, 0, length);

        var searchStart = localOffset + 6 + length;
        var (sourceText, sourceLocalOffset) = FindAssociatedSctxInChunk(data, searchStart, dataLength);
        var formIds = FindAssociatedScroInChunk(data, searchStart, dataLength);

        return new ScdaRecord
        {
            Offset = baseOffset + localOffset,
            Bytecode = bytecode,
            SourceText = sourceText,
            SourceOffset = sourceLocalOffset > 0 ? baseOffset + sourceLocalOffset : 0,
            FormIdReferences = formIds
        };
    }

    private static (string? Text, long Offset) FindAssociatedSctx(byte[] data, int searchStart)
    {
        var searchEnd = Math.Min(searchStart + 200, data.Length - 10);

        for (var i = searchStart; i < searchEnd; i++)
        {
            if (data[i] == 'S' && data[i + 1] == 'C' && data[i + 2] == 'T' && data[i + 3] == 'X')
            {
                var length = BinaryUtils.ReadUInt16LE(data, i + 4);
                if (length is > 0 and < 65535 && i + 6 + length <= data.Length)
                {
                    var text = Encoding.ASCII.GetString(data, i + 6, length).TrimEnd('\0');
                    return (text, i);
                }
            }
        }

        return (null, 0);
    }

    private static (string? Text, int LocalOffset) FindAssociatedSctxInChunk(byte[] data, int searchStart,
        int dataLength)
    {
        var searchEnd = Math.Min(searchStart + 200, dataLength - 10);

        for (var i = searchStart; i < searchEnd; i++)
        {
            if (data[i] == 'S' && data[i + 1] == 'C' && data[i + 2] == 'T' && data[i + 3] == 'X')
            {
                if (i + 6 > dataLength)
                {
                    break;
                }

                var length = BinaryUtils.ReadUInt16LE(data, i + 4);
                if (length is > 0 and < 65535 && i + 6 + length <= dataLength)
                {
                    var text = Encoding.ASCII.GetString(data, i + 6, length).TrimEnd('\0');
                    return (text, i);
                }
            }
        }

        return (null, 0);
    }

    private static List<uint> FindAssociatedScro(byte[] data, int searchStart)
    {
        var formIds = new List<uint>();
        var searchEnd = Math.Min(searchStart + 500, data.Length - 10);

        for (var i = searchStart; i < searchEnd; i++)
        {
            if (data[i] == 'S' && data[i + 1] == 'C' && data[i + 2] == 'R' && data[i + 3] == 'O')
            {
                var length = BinaryUtils.ReadUInt16LE(data, i + 4);
                if (length == 4 && i + 10 <= data.Length)
                {
                    var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
                    if (formId != 0 && formId != 0xFFFFFFFF && formId >> 24 <= 0x0F)
                    {
                        formIds.Add(formId);
                    }
                }
            }
        }

        return formIds;
    }

    private static List<uint> FindAssociatedScroInChunk(byte[] data, int searchStart, int dataLength)
    {
        var formIds = new List<uint>();
        var searchEnd = Math.Min(searchStart + 500, dataLength - 10);

        for (var i = searchStart; i < searchEnd; i++)
        {
            if (data[i] == 'S' && data[i + 1] == 'C' && data[i + 2] == 'R' && data[i + 3] == 'O')
            {
                if (i + 10 > dataLength)
                {
                    break;
                }

                var length = BinaryUtils.ReadUInt16LE(data, i + 4);
                if (length == 4)
                {
                    var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
                    if (formId != 0 && formId != 0xFFFFFFFF && formId >> 24 <= 0x0F)
                    {
                        formIds.Add(formId);
                    }
                }
            }
        }

        return formIds;
    }

    private static bool ValidateBytecode(ReadOnlySpan<byte> bytecode)
    {
        if (bytecode.Length < 4)
        {
            return false;
        }

        var firstOpcode = BinaryUtils.ReadUInt16LE(bytecode);

        // Opcode 0x0000 is not valid (padding/empty data)
        if (firstOpcode == 0)
        {
            return false;
        }

        // Valid opcodes: 0x01-0x1F (core) and 0x100-0x12FF (FUNCTION_*)
        if (firstOpcode > 0x20 && (firstOpcode < 0x100 || firstOpcode > 0x2000))
        {
            return false;
        }

        return true;
    }

    #endregion
}
