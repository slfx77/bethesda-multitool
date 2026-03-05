using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Scans memory dumps for ESM record headers and subrecords.
///     Supports both PC (little-endian) and Xbox 360 (big-endian) formats.
///     Constants and dispatch tables live in <see cref="RecordScannerDispatch" />;
///     validation/detection helpers live in <see cref="RecordValidator" />.
/// </summary>
internal static class EsmRecordScanner
{
    #region Forwarding Helpers

    /// <summary>
    ///     Forwards to <see cref="RecordValidator.IsRecordTypeMarker" />.
    ///     Kept for backward compatibility with callers referencing <c>EsmRecordScanner.IsRecordTypeMarker</c>.
    /// </summary>
    internal static bool IsRecordTypeMarker(byte[] data, int offset)
    {
        return RecordValidator.IsRecordTypeMarker(data, offset);
    }

    #endregion

    #region Record Scanning

    public static EsmRecordScanResult ScanForRecords(byte[] data)
    {
        var result = new EsmRecordScanResult();
        var seenEdids = new HashSet<string>();
        var seenFormIds = new HashSet<uint>();
        var seenMainRecordOffsets = new HashSet<long>();

        for (var i = 0; i <= data.Length - 24; i++)
        {
            // Check for main record headers first (24 bytes minimum)
            TryAddMainRecordHeader(data, i, data.Length, result.MainRecords, seenMainRecordOffsets);

            // Then check for subrecords
            if (RecordValidator.MatchesSignature(data, i, "EDID"u8))
            {
                EsmMiscDetector.TryAddEdidRecord(data, i, data.Length, result.EditorIds, seenEdids);
            }
            else if (RecordValidator.MatchesSignature(data, i, "GMST"u8))
            {
                EsmMiscDetector.TryAddGmstRecord(data, i, data.Length, result.GameSettings);
            }
            else if (RecordValidator.MatchesSignature(data, i, "SCTX"u8))
            {
                EsmMiscDetector.TryAddSctxRecord(data, i, data.Length, result.ScriptSources);
            }
            else if (RecordValidator.MatchesSignature(data, i, "SCRO"u8))
            {
                EsmMiscDetector.TryAddScroRecord(data, i, data.Length, result.FormIdReferences, seenFormIds);
            }
            else if (RecordValidator.MatchesSignature(data, i, "NAME"u8))
            {
                EsmMiscDetector.TryAddNameSubrecord(data, i, data.Length, result.NameReferences);
            }
            else if (RecordValidator.MatchesSignature(data, i, "DATA"u8))
            {
                EsmWorldExtractor.TryAddPositionSubrecord(data, i, data.Length, result.Positions);
            }
            else if (RecordValidator.MatchesSignature(data, i, "ACBS"u8))
            {
                EsmActorDetector.TryAddActorBaseSubrecord(data, i, data.Length, result.ActorBases);
            }
            else if (RecordValidator.MatchesSignature(data, i, "NAM1"u8))
            {
                EsmDialogueDetector.TryAddResponseTextSubrecord(data, i, data.Length, result.ResponseTexts);
            }
            else if (RecordValidator.MatchesSignature(data, i, "TRDT"u8))
            {
                EsmDialogueDetector.TryAddResponseDataSubrecord(data, i, data.Length, result.ResponseData);
            }
            // Text-containing subrecords
            else if (RecordValidator.MatchesSignature(data, i, "FULL"u8))
            {
                EsmMiscDetector.TryAddTextSubrecord(data, i, data.Length, "FULL", result.FullNames);
            }
            else if (RecordValidator.MatchesSignature(data, i, "DESC"u8))
            {
                EsmMiscDetector.TryAddTextSubrecord(data, i, data.Length, "DESC", result.Descriptions);
            }
            else if (RecordValidator.MatchesSignature(data, i, "MODL"u8))
            {
                EsmMiscDetector.TryAddPathSubrecord(data, i, data.Length, "MODL", result.ModelPaths);
            }
            else if (RecordValidator.MatchesSignature(data, i, "ICON"u8))
            {
                EsmMiscDetector.TryAddPathSubrecord(data, i, data.Length, "ICON", result.IconPaths);
            }
            else if (RecordValidator.MatchesSignature(data, i, "MICO"u8))
            {
                EsmMiscDetector.TryAddPathSubrecord(data, i, data.Length, "MICO", result.IconPaths);
            }
            // Texture set paths (TX00-TX07)
            else if (RecordValidator.MatchesTextureSignature(data, i))
            {
                var sig = Encoding.ASCII.GetString(data, i, 4);
                EsmMiscDetector.TryAddPathSubrecord(data, i, data.Length, sig, result.TexturePaths);
            }
            // FormID reference subrecords
            else if (RecordValidator.MatchesSignature(data, i, "SCRI"u8))
            {
                EsmMiscDetector.TryAddFormIdSubrecord(data, i, data.Length, "SCRI", result.ScriptRefs);
            }
            else if (RecordValidator.MatchesSignature(data, i, "ENAM"u8))
            {
                EsmMiscDetector.TryAddFormIdSubrecord(data, i, data.Length, "ENAM", result.EffectRefs);
            }
            else if (RecordValidator.MatchesSignature(data, i, "SNAM"u8))
            {
                EsmMiscDetector.TryAddFormIdSubrecord(data, i, data.Length, "SNAM", result.SoundRefs);
            }
            else if (RecordValidator.MatchesSignature(data, i, "QNAM"u8))
            {
                EsmMiscDetector.TryAddFormIdSubrecord(data, i, data.Length, "QNAM", result.QuestRefs);
            }
            // Condition data
            else if (RecordValidator.MatchesSignature(data, i, "CTDA"u8))
            {
                EsmActorDetector.TryAddConditionSubrecord(data, i, data.Length, result.Conditions);
            }
        }

        return result;
    }

    /// <summary>
    ///     Scan an entire memory dump for ESM records using memory-mapped access.
    ///     Processes in chunks to avoid loading the entire file into memory.
    /// </summary>
    public static EsmRecordScanResult ScanForRecordsMemoryMapped(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        List<(long start, long end)>? excludeRanges = null,
        IProgress<(long bytesProcessed, long totalBytes, int recordsFound)>? progress = null)
    {
        const int chunkSize = 16 * 1024 * 1024; // 16MB chunks
        const int overlapSize = 1024; // Overlap to handle records at chunk boundaries

        var result = new EsmRecordScanResult();
        var dedup = new RecordScannerDispatch.ScanDedup(
            new HashSet<string>(), new HashSet<uint>(), new HashSet<long>());
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + overlapSize);

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                var toRead = (int)Math.Min(chunkSize + overlapSize, fileSize - offset);
                progress?.Report((offset, fileSize, result.MainRecords.Count));
                accessor.ReadArray(offset, buffer, 0, toRead);

                // Only search up to chunkSize unless this is the last chunk
                var searchLimit = offset + chunkSize >= fileSize ? toRead - 24 : chunkSize;

                ScanChunkForSubrecords(buffer, toRead, searchLimit, offset,
                    result, dedup, excludeRanges);

                offset += chunkSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    private static void ScanChunkForSubrecords(
        byte[] buffer, int toRead, int searchLimit, long offset,
        EsmRecordScanResult result, RecordScannerDispatch.ScanDedup dedup,
        List<(long start, long end)>? excludeRanges)
    {
        var bufferSpan = buffer.AsSpan(0, toRead);

#pragma warning disable S127 // Loop counter modified in body - intentional skip-ahead in binary parsing
        for (var i = 0; i <= searchLimit; i++)
        {
            // Fast-reject: valid ESM signatures (LE and BE) start with A-Z, 0-9, or '_'.
            // The underscore and digit cases cover big-endian variants: NPC_ -> _CPN (0x5F),
            // TES4 -> 4SET (0x34). Rejects ~86% of byte positions.
            var b = buffer[i];
            if (b is < (byte)'0' or > (byte)'9' and < (byte)'A' or > (byte)'Z' and not (byte)'_')
            {
                continue;
            }

            if (i + 4 > toRead) continue;
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(bufferSpan.Slice(i, 4));

            // Single unified dispatch: one FrozenDictionary lookup for all pattern types
            if (!RecordScannerDispatch.UnifiedDispatch.TryGetValue(magic, out var action))
            {
                continue;
            }

            if (RecordValidator.IsInExcludedRange(offset + i, excludeRanges))
            {
                continue;
            }

            // Priority 1: Main record or GRUP header (triggers skip-ahead)
            if ((action & (RecordScannerDispatch.ActionMainRecordLE
                           | RecordScannerDispatch.ActionMainRecordBE
                           | RecordScannerDispatch.ActionGrup)) != 0)
            {
                var recordSize = TryAddMainRecordHeaderWithOffset(buffer, i, toRead, offset,
                    result.MainRecords, dedup.SeenMainRecordOffsets, action);

                if (recordSize > 24)
                {
                    var skipAmount = Math.Min(recordSize - 1, searchLimit - i);
                    if (skipAmount > 0)
                    {
                        i += skipAmount;
                    }

                    continue;
                }

                if (recordSize > 0)
                {
                    continue;
                }
            }

            // Priority 2: Subrecord handler
            var handlerIndex = action & 0xFF;
            if (handlerIndex != RecordScannerDispatch.NoHandler)
            {
                RecordScannerDispatch.SubrecordHandlers[handlerIndex](buffer, i, toRead, offset, result,
                    dedup.SeenEdids, dedup.SeenFormIds);
            }
            // Priority 3: Texture path
            else if ((action & RecordScannerDispatch.ActionTexture) != 0)
            {
                var sig = Encoding.ASCII.GetString(buffer, i, 4);
                EsmMiscDetector.TryAddPathSubrecordWithOffset(buffer, i, toRead, offset, sig,
                    result.TexturePaths);
            }
        }
#pragma warning restore S127
    }

    #endregion

    #region Main Record Header Parsing

    private static DetectedMainRecord? TryParseMainRecordHeader(byte[] data, int i, int dataLength, bool isBigEndian)
    {
        if (i + 24 > dataLength)
        {
            return null;
        }

        // Read the 4-char signature
        var sigBytes = new byte[4];
        if (isBigEndian)
        {
            // Xbox 360: bytes are reversed, so reverse them back
            sigBytes[0] = data[i + 3];
            sigBytes[1] = data[i + 2];
            sigBytes[2] = data[i + 1];
            sigBytes[3] = data[i];
        }
        else
        {
            sigBytes[0] = data[i];
            sigBytes[1] = data[i + 1];
            sigBytes[2] = data[i + 2];
            sigBytes[3] = data[i + 3];
        }

        var recordType = Encoding.ASCII.GetString(sigBytes);

        // Read header fields with appropriate endianness
        uint dataSize, flags, formId;
        if (isBigEndian)
        {
            dataSize = BinaryUtils.ReadUInt32BE(data, i + 4);
            flags = BinaryUtils.ReadUInt32BE(data, i + 8);
            formId = BinaryUtils.ReadUInt32BE(data, i + 12);
        }
        else
        {
            dataSize = BinaryUtils.ReadUInt32LE(data, i + 4);
            flags = BinaryUtils.ReadUInt32LE(data, i + 8);
            formId = BinaryUtils.ReadUInt32LE(data, i + 12);
        }

        // Validate the header
        if (!RecordValidator.IsValidMainRecordHeader(recordType, dataSize, flags, formId))
        {
            return null;
        }

        return new DetectedMainRecord(recordType, dataSize, flags, formId, i, isBigEndian);
    }

    private static DetectedMainRecord? TryParseMainRecordHeaderWithOffset(byte[] data, int i, int dataLength,
        long baseOffset, bool isBigEndian)
    {
        var header = TryParseMainRecordHeader(data, i, dataLength, isBigEndian);
        if (header == null)
        {
            return null;
        }

        return header with { Offset = baseOffset + i };
    }

    /// <summary>
    ///     Try to parse a GRUP header at position i. GRUP has a different layout than main records:
    ///     offset 4 = total group size (including header), offset 8 = label, offset 12 = group type (0-10).
    ///     Returns a DetectedMainRecord with DataSize=0 (only the 24-byte header is highlighted).
    /// </summary>
    private static DetectedMainRecord? TryParseGrupHeader(byte[] data, int i, int dataLength, bool isBigEndian)
    {
        if (i + 24 > dataLength)
        {
            return null;
        }

        // Read group type at offset 12 (where FormID would be for main records)
        var groupType = isBigEndian
            ? BinaryUtils.ReadUInt32BE(data, i + 12)
            : BinaryUtils.ReadUInt32LE(data, i + 12);

        // Group type must be 0-10 (defined by the ESM format)
        if (groupType > 10)
        {
            return null;
        }

        // Read total group size at offset 4 -- must be at least 24 (header-only group)
        var groupSize = isBigEndian
            ? BinaryUtils.ReadUInt32BE(data, i + 4)
            : BinaryUtils.ReadUInt32LE(data, i + 4);

        if (groupSize < 24)
        {
            return null;
        }

        // Read label at offset 8 (FormID, record type, or block coords depending on group type)
        var label = isBigEndian
            ? BinaryUtils.ReadUInt32BE(data, i + 8)
            : BinaryUtils.ReadUInt32LE(data, i + 8);

        // DataSize = 0: only the 24-byte GRUP header gets highlighted,
        // not the group contents (which contain separately-detected records)
        return new DetectedMainRecord("GRUP", 0, groupType, label, i, isBigEndian);
    }

    private static void TryAddMainRecordHeader(byte[] data, int i, int dataLength,
        List<DetectedMainRecord> records, HashSet<long> seenOffsets)
    {
        if (i + 24 > dataLength || seenOffsets.Contains(i))
        {
            return;
        }

        var magic = BinaryUtils.ReadUInt32LE(data, i);
        if (!RecordScannerDispatch.UnifiedDispatch.TryGetValue(magic, out var action))
        {
            return;
        }

        if ((action & (RecordScannerDispatch.ActionMainRecordLE
                       | RecordScannerDispatch.ActionMainRecordBE
                       | RecordScannerDispatch.ActionGrup)) == 0)
        {
            return;
        }

        // Reject known GPU debug patterns BEFORE parsing header
        if (RecordValidator.IsKnownFalsePositive(data, i))
        {
            return;
        }

        // Try little-endian (PC format)
        if ((action & RecordScannerDispatch.ActionMainRecordLE) != 0)
        {
            var header = TryParseMainRecordHeader(data, i, dataLength, false);
            if (header != null && seenOffsets.Add(i))
            {
                records.Add(header);
            }

            return;
        }

        // Try big-endian (Xbox 360 format)
        if ((action & RecordScannerDispatch.ActionMainRecordBE) != 0)
        {
            var header = TryParseMainRecordHeader(data, i, dataLength, true);
            if (header != null && seenOffsets.Add(i))
            {
                records.Add(header);
            }

            return;
        }

        // Try GRUP (structural container -- different header layout than main records)
        if ((action & RecordScannerDispatch.ActionGrup) != 0)
        {
            var isBigEndian = magic == RecordScannerDispatch.SigGrupBE;
            var grup = TryParseGrupHeader(data, i, dataLength, isBigEndian);
            if (grup != null && seenOffsets.Add(i))
            {
                records.Add(grup);
            }
        }
    }

    /// <summary>
    ///     Try to parse a main record header at position i using pre-computed action flags
    ///     from the unified dispatch table. Returns total record size for skip-ahead, or 0.
    /// </summary>
    private static int TryAddMainRecordHeaderWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<DetectedMainRecord> records, HashSet<long> seenOffsets, int action)
    {
        var globalOffset = baseOffset + i;
        if (i + 24 > dataLength || seenOffsets.Contains(globalOffset))
        {
            return 0;
        }

        // Reject known GPU debug patterns (e.g., VGT_DEBUG)
        if (RecordValidator.IsKnownFalsePositive(data, i)) return 0;

        // Main record LE or BE
        if ((action & RecordScannerDispatch.ActionMainRecordLE) != 0)
        {
            var header = TryParseMainRecordHeaderWithOffset(data, i, dataLength, baseOffset, false);
            if (header != null && seenOffsets.Add(globalOffset))
            {
                records.Add(header);
                return 24 + (int)header.DataSize;
            }
        }

        if ((action & RecordScannerDispatch.ActionMainRecordBE) != 0)
        {
            var header = TryParseMainRecordHeaderWithOffset(data, i, dataLength, baseOffset, true);
            if (header != null && seenOffsets.Add(globalOffset))
            {
                records.Add(header);
                return 24 + (int)header.DataSize;
            }
        }

        // GRUP
        if ((action & RecordScannerDispatch.ActionGrup) != 0)
        {
            var magic = BinaryUtils.ReadUInt32LE(data, i);
            var isBigEndian = magic == RecordScannerDispatch.SigGrupBE;
            var grup = TryParseGrupHeader(data, i, dataLength, isBigEndian);
            if (grup != null)
            {
                var grupWithOffset = grup with { Offset = globalOffset };
                if (seenOffsets.Add(globalOffset))
                {
                    records.Add(grupWithOffset);
                    return 24;
                }
            }
        }

        return 0;
    }

    #endregion
}
