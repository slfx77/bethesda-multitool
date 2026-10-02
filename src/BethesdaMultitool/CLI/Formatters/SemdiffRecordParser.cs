using System.Globalization;
using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.CLI.Formatters;

/// <summary>
///     Record parsing and comparison logic for the semantic diff command.
/// </summary>
internal static class SemdiffRecordParser
{
    private const uint CompressedFlag = 0x00040000;
    private const int CompressedFlagBit = 18;

    internal static List<SemdiffTypes.ParsedRecord> ParseRecordsWithSubrecords(byte[] data, bool bigEndian,
        string? typeFilter, uint? formIdFilter)
    {
        return ParseRecordsWithSubrecords(data, bigEndian, typeFilter, formIdFilter, out _);
    }

    /// <summary>
    ///     Parses records and reports how many compressed records were skipped because their
    ///     payload failed zlib decompression — callers should surface a non-zero count so a
    ///     partially-diffed file is visible instead of silently thinner.
    /// </summary>
    internal static List<SemdiffTypes.ParsedRecord> ParseRecordsWithSubrecords(byte[] data, bool bigEndian,
        string? typeFilter, uint? formIdFilter, out int skippedCompressedRecords)
    {
        return ParseCore(data, bigEndian,
            (sig, formId) => MatchesType(sig, typeFilter) && (formIdFilter == null || formId == formIdFilter),
            null, out skippedCompressedRecords);
    }

    /// <summary>
    ///     Parses only the records whose FormID is in <paramref name="formIds" /> (and whose signature
    ///     matches <paramref name="typeFilter" /> when one is given). Used by <c>--map</c>, which names
    ///     a handful of FormIDs in a file of hundreds of thousands.
    /// </summary>
    internal static List<SemdiffTypes.ParsedRecord> ParseRecordsWithFormIds(byte[] data, bool bigEndian,
        string? typeFilter, IReadOnlySet<uint> formIds, out int skippedCompressedRecords)
    {
        return ParseCore(data, bigEndian,
            (sig, formId) => MatchesType(sig, typeFilter) && formIds.Contains(formId),
            null, out skippedCompressedRecords);
    }

    /// <summary>
    ///     Parses the <paramref name="signature" /> records whose EditorID equals
    ///     <paramref name="editorId" /> (case-insensitively, as the engine treats EditorIDs). Every
    ///     other record of that signature is decoded to read its EDID and then dropped, so memory stays
    ///     bounded by the matches rather than by the signature's population.
    /// </summary>
    internal static List<SemdiffTypes.ParsedRecord> ParseRecordsWithEditorId(byte[] data, bool bigEndian,
        string signature, string editorId)
    {
        return ParseCore(data, bigEndian,
            (sig, _) => string.Equals(sig, signature, StringComparison.Ordinal),
            record => string.Equals(record.EditorId, editorId, StringComparison.OrdinalIgnoreCase),
            out _);
    }

    private static bool MatchesType(string sig, string? typeFilter)
    {
        return string.IsNullOrEmpty(typeFilter) || sig.Equals(typeFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static List<SemdiffTypes.ParsedRecord> ParseCore(byte[] data, bool bigEndian,
        Func<string, uint, bool> headerFilter, Func<SemdiffTypes.ParsedRecord, bool>? recordFilter,
        out int skippedCompressedRecords)
    {
        skippedCompressedRecords = 0;
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

            // Parse record header: signature, size, flags, FormID, then (24-byte framing) Version
            // Control Info 1 (u32 @16), form version (u16 @20) and Version Control Info 2 (u16 @22),
            // all in the file's byte order.
            var dataSize = BinaryUtils.ReadUInt32(data, offset + 4, bigEndian);
            var flags = BinaryUtils.ReadUInt32(data, offset + 8, bigEndian);
            var formId = BinaryUtils.ReadUInt32(data, offset + 12, bigEndian);
            var versionControl1 = BinaryUtils.ReadUInt32(data, offset + 16, bigEndian);
            var formVersion = BinaryUtils.ReadUInt16(data, offset + 20, bigEndian);
            var versionControl2 = BinaryUtils.ReadUInt16(data, offset + 22, bigEndian);

            var headerSize = 24; // FNV uses 24-byte headers
            var recordEnd = offset + headerSize + (int)dataSize;

            if (headerFilter(sig, formId))
            {
                // Parse subrecords
                var compressed = (flags & CompressedFlag) != 0;
                byte[] recordData;
                int subOffset;

                if (compressed)
                {
                    try
                    {
                        var decompSize = BinaryUtils.ReadUInt32(data, offset + headerSize, bigEndian);
                        var compData = data.AsSpan(offset + headerSize + 4, (int)dataSize - 4);
                        recordData = DecompressZlib(compData.ToArray(), (int)decompSize);
                        subOffset = 0;
                    }
                    catch (InvalidDataException)
                    {
                        // Some records have a compressed flag set but the payload isn't
                        // standard zlib (build artifacts, custom compression, header
                        // mismatch). Skip these rather than aborting the whole diff so
                        // the rest of the file can still be analyzed — but count them so
                        // the caller can warn that the diff is partial.
                        skippedCompressedRecords++;
                        offset = recordEnd;
                        continue;
                    }
                }
                else
                {
                    recordData = data;
                    subOffset = offset + headerSize;
                }

                var subrecords = ParseSubrecords(recordData, subOffset, compressed ? recordData.Length : (int)dataSize,
                    bigEndian);
                var record = new SemdiffTypes.ParsedRecord(sig, formId, flags, offset, subrecords)
                {
                    DataSize = dataSize,
                    VersionControl1 = versionControl1,
                    FormVersion = formVersion,
                    VersionControl2 = versionControl2,
                    HeaderSize = headerSize
                };

                if (recordFilter == null || recordFilter(record))
                {
                    records.Add(record);
                }
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

    /// <summary>
    ///     The first EDID subrecord read to its first NUL (an EDID is a NUL-terminated string, so
    ///     anything after the terminator is not part of it), or null when there is none or it is empty.
    /// </summary>
    internal static string? ReadEditorId(IReadOnlyList<SemdiffTypes.ParsedSubrecord> subrecords)
    {
        foreach (var sub in subrecords)
        {
            if (sub.Signature != "EDID")
            {
                continue;
            }

            var length = Array.IndexOf(sub.Data, (byte)0);
            if (length < 0)
            {
                length = sub.Data.Length;
            }

            return length == 0 ? null : Encoding.ASCII.GetString(sub.Data, 0, length);
        }

        return null;
    }

    /// <summary>
    ///     Compares the record headers of a same-signature pair. Every flag bit set on one side only
    ///     becomes a <see cref="SemdiffTypes.FlagBitDelta" /> named for <paramref name="a" />'s signature
    ///     in <paramref name="game" /> (bit 18, Compressed, is <see cref="SemdiffTypes.HeaderDeltaClass.Storage" />;
    ///     every other bit is <see cref="SemdiffTypes.HeaderDeltaClass.Semantic" />). The form version is a
    ///     <see cref="SemdiffTypes.HeaderDeltaClass.Format" /> delta and the two version-control words are
    ///     <see cref="SemdiffTypes.HeaderDeltaClass.Bookkeeping" />. The data size follows from the payload
    ///     and is never a delta of its own.
    /// </summary>
    internal static SemdiffTypes.HeaderComparison CompareRecordHeaders(SemdiffTypes.ParsedRecord a,
        SemdiffTypes.ParsedRecord b, BethesdaGame game)
    {
        var added = new List<SemdiffTypes.FlagBitDelta>();
        var removed = new List<SemdiffTypes.FlagBitDelta>();
        var changed = a.Flags ^ b.Flags;

        for (var bit = 0; bit < 32; bit++)
        {
            var mask = 1u << bit;
            if ((changed & mask) == 0)
            {
                continue;
            }

            var delta = new SemdiffTypes.FlagBitDelta(bit, mask, RecordHeaderFlagRegistry.GetName(game, a.Type, bit),
                bit == CompressedFlagBit
                    ? SemdiffTypes.HeaderDeltaClass.Storage
                    : SemdiffTypes.HeaderDeltaClass.Semantic);
            if ((b.Flags & mask) != 0)
            {
                added.Add(delta);
            }
            else
            {
                removed.Add(delta);
            }
        }

        var fields = new List<SemdiffTypes.HeaderFieldDelta>();
        if (a.FormVersion != b.FormVersion)
        {
            fields.Add(new SemdiffTypes.HeaderFieldDelta("Form Version",
                a.FormVersion.ToString(CultureInfo.InvariantCulture),
                b.FormVersion.ToString(CultureInfo.InvariantCulture),
                SemdiffTypes.HeaderDeltaClass.Format));
        }

        if (a.VersionControl1 != b.VersionControl1)
        {
            fields.Add(new SemdiffTypes.HeaderFieldDelta("Version Control Info 1",
                FormatVersionControl1(a.VersionControl1), FormatVersionControl1(b.VersionControl1),
                SemdiffTypes.HeaderDeltaClass.Bookkeeping));
        }

        if (a.VersionControl2 != b.VersionControl2)
        {
            fields.Add(new SemdiffTypes.HeaderFieldDelta("Version Control Info 2",
                a.VersionControl2.ToString(CultureInfo.InvariantCulture),
                b.VersionControl2.ToString(CultureInfo.InvariantCulture),
                SemdiffTypes.HeaderDeltaClass.Bookkeeping));
        }

        return new SemdiffTypes.HeaderComparison(a.Flags, b.Flags, added, removed, fields);
    }

    internal static string FormatVersionControl1(uint value)
    {
        return $"0x{value:X8}";
    }

    /// <summary>
    ///     Compares the subrecords of two records of the same signature, per signature (the i-th instance
    ///     of each signature against the i-th). See <see cref="CompareSubrecords" /> for how the two byte
    ///     orders are reconciled.
    /// </summary>
    /// <exception cref="ArgumentException">The two records have different signatures.</exception>
    internal static List<SemdiffTypes.FieldDiff> CompareRecordFields(SemdiffTypes.ParsedRecord recA,
        SemdiffTypes.ParsedRecord recB, bool bigEndianA, bool bigEndianB)
    {
        return CompareSubrecords(recA, recB, bigEndianA, bigEndianB).FieldDiffs;
    }

    /// <summary>
    ///     Compares the subrecords of two records of the same signature. Both sides are decoded with
    ///     one schema (the shared signature's), so a pair of different signatures is refused rather
    ///     than decoded as if B were an A.
    ///     <para>
    ///         Equality is exact. When both files share a byte order the raw payloads are compared. When
    ///         they do not, the big-endian side is first swapped to PC order with the converter's own rules
    ///         (<see cref="NormalizeBigEndianSubrecord" />), so an Xbox 360 record and a PC record holding the
    ///         same values compare equal; a subrecord no schema covers cannot be swapped and is reported as a
    ///         difference marked <see cref="SemdiffTypes.FieldDiff.ByteOrderUnresolved" />, never as equal.
    ///         Display strings (rounded floats, truncated byte arrays) never decide equality.
    ///     </para>
    ///     <para>
    ///         The per-signature comparison cannot see a subrecord that moved across signatures (XCAS and
    ///         XCLR swapped, or a condition moved from one quest stage to another), so when it finds nothing
    ///         the ordered sequences are compared too and the first diverging index is returned.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentException">The two records have different signatures.</exception>
    internal static SemdiffTypes.SubrecordComparison CompareSubrecords(SemdiffTypes.ParsedRecord recA,
        SemdiffTypes.ParsedRecord recB, bool bigEndianA, bool bigEndianB)
    {
        if (!string.Equals(recA.Type, recB.Type, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Cannot compare fields of a {recA.Type} record with a {recB.Type} record: " +
                "subrecords are decoded with one signature's schema.", nameof(recB));
        }

        var comparableA = ToComparable(recA, bigEndianA, bigEndianB);
        var comparableB = ToComparable(recB, bigEndianB, bigEndianA);
        var diffs = new List<SemdiffTypes.FieldDiff>();

        // Build subrecord lookup for both records
        var subsA = comparableA.GroupBy(s => s.Source.Signature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var subsB = comparableB.GroupBy(s => s.Source.Signature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var allSigs = subsA.Keys.Union(subsB.Keys).OrderBy(x => x).ToList();

        foreach (var sig in allSigs)
        {
            var hasA = subsA.TryGetValue(sig, out var listA);
            var hasB = subsB.TryGetValue(sig, out var listB);

            if (!hasA && hasB)
            {
                foreach (var sub in listB!)
                {
                    diffs.Add(new SemdiffTypes.FieldDiff(sig, null, sub.Source.Data, "Only in B", bigEndianA,
                        bigEndianB, recA.Type));
                }
            }
            else if (hasA && !hasB)
            {
                foreach (var sub in listA!)
                {
                    diffs.Add(new SemdiffTypes.FieldDiff(sig, sub.Source.Data, null, "Only in A", bigEndianA,
                        bigEndianB, recA.Type));
                }
            }
            else
            {
                // Both have this subrecord - compare each instance
                var maxCount = Math.Max(listA!.Count, listB!.Count);
                for (var i = 0; i < maxCount; i++)
                {
                    if (i >= listA.Count)
                    {
                        diffs.Add(new SemdiffTypes.FieldDiff(sig, null, listB[i].Source.Data,
                            $"Only in B (index {i})", bigEndianA, bigEndianB, recA.Type));
                    }
                    else if (i >= listB.Count)
                    {
                        diffs.Add(new SemdiffTypes.FieldDiff(sig, listA[i].Source.Data, null,
                            $"Only in A (index {i})", bigEndianA, bigEndianB, recA.Type));
                    }
                    else if (!SameContent(listA[i], listB[i]))
                    {
                        var dataA = listA[i].Data;
                        var dataB = listB[i].Data;
                        diffs.Add(new SemdiffTypes.FieldDiff(sig, listA[i].Source.Data, listB[i].Source.Data, null,
                            bigEndianA, bigEndianB, recA.Type)
                        {
                            ByteOrderUnresolved = dataA == null || dataB == null,
                            FirstDifferingOffset = dataA != null && dataB != null
                                ? dataA.AsSpan().CommonPrefixLength(dataB)
                                : null
                        });
                    }
                }
            }
        }

        return new SemdiffTypes.SubrecordComparison(diffs,
            diffs.Count == 0 ? FindOrderDivergence(comparableA, comparableB) : null);
    }

    /// <summary>
    ///     A big-endian (Xbox 360) subrecord payload in PC byte order, swapped exactly as the ESM converter
    ///     swaps it: <see cref="SubrecordSchemaProcessor.ConvertWithSchema" />, with the converter's one
    ///     record-chain rule (a PERK's top-level DATA(4) is four UInt8 fields and is copied as is; see
    ///     EsmRecordWriter's PERK entry tracking). Strings pass through unchanged. Null when no schema
    ///     covers the subrecord and it holds more than one byte (a zero- or one-byte payload has no byte
    ///     order to resolve).
    /// </summary>
    internal static byte[]? NormalizeBigEndianSubrecord(string signature, byte[] data, string recordType,
        bool topLevelPerkData)
    {
        if (topLevelPerkData && recordType == "PERK" && signature == "DATA" && data.Length == 4)
        {
            return data;
        }

        var converted = SubrecordSchemaProcessor.ConvertWithSchema(signature, data, recordType);
        if (converted != null)
        {
            return converted;
        }

        return data.Length <= 1 ? data : null;
    }

    /// <summary>
    ///     The record's subrecords as they compare against a file of <paramref name="otherBigEndian" /> byte
    ///     order: raw when the orders agree (or this side is already little-endian), else normalised to PC
    ///     order, with a null <see cref="ComparableSubrecord.Data" /> where no schema resolves the swap.
    /// </summary>
    private static List<ComparableSubrecord> ToComparable(SemdiffTypes.ParsedRecord record, bool bigEndian,
        bool otherBigEndian)
    {
        var result = new List<ComparableSubrecord>(record.Subrecords.Count);
        if (!bigEndian || otherBigEndian)
        {
            foreach (var sub in record.Subrecords)
            {
                result.Add(new ComparableSubrecord(sub, sub.Data));
            }

            return result;
        }

        // Mirrors the converter: a PERK's DATA is an entry payload between PRKE and PRKF, top-level otherwise.
        var insidePerkEntry = false;
        foreach (var sub in record.Subrecords)
        {
            result.Add(new ComparableSubrecord(sub,
                NormalizeBigEndianSubrecord(sub.Signature, sub.Data, record.Type, !insidePerkEntry)));

            if (record.Type == "PERK")
            {
                if (sub.Signature == "PRKE")
                {
                    insidePerkEntry = true;
                }
                else if (sub.Signature == "PRKF")
                {
                    insidePerkEntry = false;
                }
            }
        }

        return result;
    }

    private static bool SameContent(ComparableSubrecord a, ComparableSubrecord b)
    {
        return a.Data != null && b.Data != null &&
               string.Equals(a.Source.Signature, b.Source.Signature, StringComparison.Ordinal) &&
               a.Data.AsSpan().SequenceEqual(b.Data);
    }

    /// <summary>
    ///     The first index at which the ordered subrecord sequences disagree (signature or content), or null
    ///     when they are equal. Called only after the per-signature comparison found no difference, so both
    ///     sequences hold the same subrecords and any disagreement is one of order.
    /// </summary>
    private static SemdiffTypes.SubrecordOrderDivergence? FindOrderDivergence(
        List<ComparableSubrecord> sequenceA, List<ComparableSubrecord> sequenceB)
    {
        var count = Math.Max(sequenceA.Count, sequenceB.Count);
        for (var i = 0; i < count; i++)
        {
            var hasA = i < sequenceA.Count;
            var hasB = i < sequenceB.Count;
            if (!hasA || !hasB || !SameContent(sequenceA[i], sequenceB[i]))
            {
                return new SemdiffTypes.SubrecordOrderDivergence(i,
                    hasA ? sequenceA[i].Source.Signature : null,
                    hasB ? sequenceB[i].Source.Signature : null);
            }
        }

        return null;
    }

    /// <summary>
    ///     A parsed subrecord and the bytes it is compared by: the raw payload, or the payload in PC byte
    ///     order; null when the byte order could not be resolved.
    /// </summary>
    private readonly record struct ComparableSubrecord(SemdiffTypes.ParsedSubrecord Source, byte[]? Data);

    /// <summary>
    ///     One entry per subrecord signature, in first-appearance order, with each instance's size.
    ///     Used where typed decoding is refused: it shows what each side holds without claiming what it means.
    /// </summary>
    internal static IReadOnlyList<SemdiffTypes.SubrecordInventoryEntry> BuildSubrecordInventory(
        SemdiffTypes.ParsedRecord record)
    {
        return record.Subrecords
            .GroupBy(s => s.Signature, StringComparer.Ordinal)
            .Select(g => new SemdiffTypes.SubrecordInventoryEntry(g.Key, g.Count(),
                g.Select(s => s.Data.Length).ToList()))
            .ToList();
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
