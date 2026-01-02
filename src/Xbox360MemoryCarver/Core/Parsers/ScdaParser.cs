using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Bethesda compiled script bytecode (SCDA records).
///     Used in release builds where scripts are compiled rather than stored as source.
/// </summary>
public class ScdaParser : IFileParser
{
    private static readonly byte[] ScdaSignature = "SCDA"u8.ToArray();

    /// <summary>
    ///     Parsed SCDA record with bytecode and optional source text.
    /// </summary>
    public record ScdaRecord
    {
        public required long Offset { get; init; }
        public int BytecodeSize => Bytecode.Length;
        public int BytecodeLength => Bytecode.Length;
        public required byte[] Bytecode { get; init; }
        public string? SourceText { get; init; }
        public long SourceOffset { get; init; }
        public bool HasAssociatedSctx => !string.IsNullOrEmpty(SourceText);
        public List<uint> FormIdReferences { get; init; } = [];
    }

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 10)
        {
            return null;
        }

        var span = data[offset..];
        if (!span[..4].SequenceEqual(ScdaSignature))
        {
            return null;
        }

        // SCDA format: "SCDA" (4) + length (2) + bytecode data
        var length = BinaryUtils.ReadUInt16LE(span, 4);
        if (length == 0 || length > 65535 || offset + 6 + length > data.Length)
        {
            return null;
        }

        // Validate that this looks like bytecode (not random data)
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
                ["bytecodeSize"] = length
            }
        };
    }

    /// <summary>
    ///     Scan an entire memory dump for all SCDA records.
    /// </summary>
    public static List<ScdaRecord> ScanForRecords(byte[] data)
    {
        var records = new List<ScdaRecord>();
        int i = 0;

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

        return records;
    }

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

        var bytecode = new byte[length];
        Array.Copy(data, offset + 6, bytecode, 0, length);

        // Look for associated SCTX (source text) nearby
        var (sourceText, sourceOffset) = FindAssociatedSctx(data, offset + 6 + length);

        // Look for SCRO FormID references
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

    private static (string? Text, long Offset) FindAssociatedSctx(byte[] data, int searchStart)
    {
        // Look for SCTX within 200 bytes after SCDA
        var searchEnd = Math.Min(searchStart + 200, data.Length - 10);

        for (int i = searchStart; i < searchEnd; i++)
        {
            if (data[i] == 'S' && data[i + 1] == 'C' && data[i + 2] == 'T' && data[i + 3] == 'X')
            {
                var length = BinaryUtils.ReadUInt16LE(data, i + 4);
                if (length > 0 && length < 65535 && i + 6 + length <= data.Length)
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

        for (int i = searchStart; i < searchEnd; i++)
        {
            if (data[i] == 'S' && data[i + 1] == 'C' && data[i + 2] == 'R' && data[i + 3] == 'O')
            {
                var length = BinaryUtils.ReadUInt16LE(data, i + 4);
                if (length == 4 && i + 10 <= data.Length)
                {
                    var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
                    if (formId != 0 && formId != 0xFFFFFFFF && (formId >> 24) <= 0x0F)
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

        // Basic validation: check if first bytes look like bytecode
        // Valid bytecode typically starts with opcodes in certain ranges
        var firstByte = bytecode[0];

        // Common starting opcodes: 0x10-0x1F (control flow), or function opcodes
        if (firstByte is >= 0x10 and <= 0x1F)
        {
            return true;
        }

        // Could be a high opcode (FUNCTION_*)
        if (bytecode.Length >= 2)
        {
            var word = BinaryUtils.ReadUInt16LE(bytecode, 0);
            if (word is >= 0x100 and < 0x2000)
            {
                return true;
            }
        }

        return false;
    }
}
