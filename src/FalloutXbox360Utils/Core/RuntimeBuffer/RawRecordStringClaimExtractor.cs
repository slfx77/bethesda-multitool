using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

/// <summary>
///     Extracts string ownership claims from raw ESM record subrecords in the dump.
///     Walks uncompressed main records and claims exact string-start file offsets
///     for string-bearing subrecords (EDID, FULL, DESC, MODL, etc.).
/// </summary>
internal static class RawRecordStringClaimExtractor
{
    private const int MainRecordHeaderSize = 24;
    private const int MaxRecordDataSize = 1024 * 1024; // 1 MB safety cap

    internal static List<RuntimeStringOwnershipClaim> ExtractClaims(
        IReadOnlyList<DetectedMainRecord> mainRecords,
        MemoryMappedViewAccessor accessor,
        long fileSize)
    {
        var claims = new List<RuntimeStringOwnershipClaim>();

        foreach (var record in mainRecords)
        {
            if (record.IsCompressed || record.IsDeleted)
            {
                continue;
            }

            if (record.DataSize == 0 || record.DataSize > MaxRecordDataSize)
            {
                continue;
            }

            var dataStart = record.Offset + MainRecordHeaderSize;
            var dataSize = (int)record.DataSize;

            if (dataStart + dataSize > fileSize)
            {
                continue;
            }

            var data = new byte[dataSize];
            accessor.ReadArray(dataStart, data, 0, dataSize);

            ExtractSubrecordClaims(data, dataSize, dataStart, record, claims);
        }

        return claims;
    }

    private static void ExtractSubrecordClaims(
        byte[] data,
        int dataSize,
        long dataFileOffset,
        DetectedMainRecord record,
        List<RuntimeStringOwnershipClaim> claims)
    {
        var offset = 0;

        while (offset + EsmSubrecordUtils.SubrecordHeaderSize <= dataSize)
        {
            // Read subrecord signature (4 bytes)
            string sig;
            ushort subSize;

            if (record.IsBigEndian)
            {
                sig = new string([
                    (char)data[offset + 3], (char)data[offset + 2],
                    (char)data[offset + 1], (char)data[offset]
                ]);
                subSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4));
            }
            else
            {
                sig = Encoding.ASCII.GetString(data, offset, 4);
                subSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
            }

            var subDataOffset = offset + EsmSubrecordUtils.SubrecordHeaderSize;

            if (subDataOffset + subSize > dataSize)
            {
                break;
            }

            if (subSize > 0
                && SubrecordSchemaRegistry.IsStringSubrecord(sig, record.RecordType)
                && IsLikelyString(data, subDataOffset, subSize))
            {
                var stringFileOffset = dataFileOffset + subDataOffset;
                claims.Add(new RuntimeStringOwnershipClaim(
                    stringFileOffset,
                    null,
                    "RawRecord",
                    $"{record.RecordType} [{record.FormId:X8}]",
                    record.FormId,
                    record.Offset,
                    ClaimSource.RawRecordSubrecord,
                    record.RecordType,
                    sig));
            }

            offset = subDataOffset + subSize;
        }
    }

    private static bool IsLikelyString(byte[] data, int offset, int length)
    {
        if (length == 0)
        {
            return false;
        }

        var printable = 0;
        var end = offset + length;

        // Exclude trailing null terminator from validation
        if (data[end - 1] == 0)
        {
            end--;
        }

        if (end <= offset)
        {
            return false;
        }

        for (var i = offset; i < end; i++)
        {
            var c = data[i];
            if ((c >= 32 && c <= 126) || c == '\n' || c == '\r' || c == '\t')
            {
                printable++;
            }
        }

        return printable >= (end - offset) * 0.8;
    }
}
