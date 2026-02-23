using System.IO.Compression;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Record parsing and comparison logic for the semantic diff command.
/// </summary>
internal static class SemdiffRecordParser
{
    internal static List<SemdiffTypes.ParsedRecord> ParseRecordsWithSubrecords(byte[] data, bool bigEndian,
        string? typeFilter, uint? formIdFilter)
    {
        var records = new List<SemdiffTypes.ParsedRecord>();
        var offset = 0;

        while (offset + 24 <= data.Length)
        {
            var sig = bigEndian
                ? new string([
                    (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]
                ])
                : Encoding.ASCII.GetString(data, offset, 4);

            if (sig == "GRUP")
            {
                offset += 24;
                continue;
            }

            // Parse record header
            var dataSize = BinaryUtils.ReadUInt32(data, offset + 4, bigEndian);
            var flags = BinaryUtils.ReadUInt32(data, offset + 8, bigEndian);
            var formId = BinaryUtils.ReadUInt32(data, offset + 12, bigEndian);

            var headerSize = 24; // FNV uses 24-byte headers
            var recordEnd = offset + headerSize + (int)dataSize;

            // Filter checks
            var matchesType = string.IsNullOrEmpty(typeFilter) ||
                              sig.Equals(typeFilter, StringComparison.OrdinalIgnoreCase);
            var matchesFormId = formIdFilter == null || formId == formIdFilter;

            if (matchesType && matchesFormId)
            {
                // Parse subrecords
                var compressed = (flags & 0x00040000) != 0;
                byte[] recordData;
                int subOffset;

                if (compressed)
                {
                    // Decompress
                    var decompSize = BinaryUtils.ReadUInt32(data, offset + headerSize, bigEndian);
                    var compData = data.AsSpan(offset + headerSize + 4, (int)dataSize - 4);
                    recordData = DecompressZlib(compData.ToArray(), (int)decompSize);
                    subOffset = 0;
                }
                else
                {
                    recordData = data;
                    subOffset = offset + headerSize;
                }

                var subrecords = ParseSubrecords(recordData, subOffset, compressed ? recordData.Length : (int)dataSize,
                    bigEndian);
                records.Add(new SemdiffTypes.ParsedRecord(sig, formId, flags, offset, subrecords));
            }

            offset = recordEnd;
        }

        return records;
    }

    private static List<SemdiffTypes.ParsedSubrecord> ParseSubrecords(byte[] data, int startOffset, int length,
        bool bigEndian)
    {
        var subrecords = new List<SemdiffTypes.ParsedSubrecord>();
        var offset = startOffset;
        var endOffset = startOffset + length;

        while (offset + 6 <= endOffset)
        {
            var sig = bigEndian
                ? new string([
                    (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]
                ])
                : Encoding.ASCII.GetString(data, offset, 4);
            var size = BinaryUtils.ReadUInt16(data, offset + 4, bigEndian);

            if (offset + 6 + size > endOffset)
            {
                break;
            }

            var subData = new byte[size];
            Array.Copy(data, offset + 6, subData, 0, size);
            subrecords.Add(new SemdiffTypes.ParsedSubrecord(sig, subData, offset));

            offset += 6 + size;
        }

        return subrecords;
    }

    internal static List<SemdiffTypes.FieldDiff> CompareRecordFields(SemdiffTypes.ParsedRecord recA,
        SemdiffTypes.ParsedRecord recB, bool bigEndianA, bool bigEndianB)
    {
        var diffs = new List<SemdiffTypes.FieldDiff>();

        // Build subrecord lookup for both records
        var subsA = recA.Subrecords.GroupBy(s => s.Signature)
            .ToDictionary(g => g.Key, g => g.ToList());
        var subsB = recB.Subrecords.GroupBy(s => s.Signature)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allSigs = subsA.Keys.Union(subsB.Keys).OrderBy(x => x).ToList();

        foreach (var sig in allSigs)
        {
            var hasA = subsA.TryGetValue(sig, out var listA);
            var hasB = subsB.TryGetValue(sig, out var listB);

            if (!hasA && hasB)
            {
                foreach (var sub in listB!)
                {
                    diffs.Add(new SemdiffTypes.FieldDiff(sig, null, sub.Data, "Only in B", bigEndianA, bigEndianB,
                        recA.Type));
                }
            }
            else if (hasA && !hasB)
            {
                foreach (var sub in listA!)
                {
                    diffs.Add(new SemdiffTypes.FieldDiff(sig, sub.Data, null, "Only in A", bigEndianA, bigEndianB,
                        recA.Type));
                }
            }
            else
            {
                // Both have this subrecord - compare each instance
                var maxCount = Math.Max(listA!.Count, listB!.Count);
                for (var i = 0; i < maxCount; i++)
                {
                    var subA = i < listA.Count ? listA[i] : null;
                    var subB = i < listB.Count ? listB[i] : null;

                    if (subA == null)
                    {
                        diffs.Add(new SemdiffTypes.FieldDiff(sig, null, subB!.Data, $"Only in B (index {i})",
                            bigEndianA, bigEndianB, recA.Type));
                    }
                    else if (subB == null)
                    {
                        diffs.Add(new SemdiffTypes.FieldDiff(sig, subA.Data, null, $"Only in A (index {i})",
                            bigEndianA, bigEndianB, recA.Type));
                    }
                    else if (!subA.Data.SequenceEqual(subB.Data))
                    {
                        diffs.Add(new SemdiffTypes.FieldDiff(sig, subA.Data, subB.Data, null, bigEndianA, bigEndianB,
                            recA.Type));
                    }
                }
            }
        }

        return diffs;
    }

    private static byte[] DecompressZlib(byte[] compressed, int decompressedSize)
    {
        using var inputStream = new MemoryStream(compressed);
        using var zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress);
        var decompressed = new byte[decompressedSize];
        var totalRead = 0;
        while (totalRead < decompressedSize)
        {
            var read = zlibStream.Read(decompressed, totalRead, decompressedSize - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return decompressed;
    }
}
