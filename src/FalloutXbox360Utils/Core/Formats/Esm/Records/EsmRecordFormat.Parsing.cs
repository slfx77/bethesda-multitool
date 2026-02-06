using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;
using SubrecordEntry = FalloutXbox360Utils.Core.Utils.ParsedSubrecord;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class EsmRecordFormat
{
    #region Subrecord Iteration

    /// <summary>
    ///     Iterates through subrecords in a record's data section.
    ///     Uses the shared utility from EsmSubrecordUtils.
    /// </summary>
    private static IEnumerable<SubrecordEntry> IterateSubrecords(byte[] data, int dataSize, bool bigEndian)
    {
        return EsmSubrecordUtils.IterateSubrecords(data, dataSize, bigEndian);
    }

    #endregion

    #region Subrecord Length Helpers

    /// <summary>
    ///     Get subrecord length, trying both endianness.
    /// </summary>
    private static ushort GetSubrecordLength(byte[] data, int offset, int maxLen)
    {
        var lenLe = BinaryUtils.ReadUInt16LE(data, offset);
        var lenBe = BinaryUtils.ReadUInt16BE(data, offset);

        // Prefer LE if it's valid
        if (lenLe > 0 && lenLe <= maxLen)
        {
            return lenLe;
        }

        if (lenBe > 0 && lenBe <= maxLen)
        {
            return lenBe;
        }

        return 0;
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
            if (MatchesSignature(data, i, "EDID"u8))
            {
                TryAddEdidRecord(data, i, data.Length, result.EditorIds, seenEdids);
            }
            else if (MatchesSignature(data, i, "GMST"u8))
            {
                TryAddGmstRecord(data, i, data.Length, result.GameSettings);
            }
            else if (MatchesSignature(data, i, "SCTX"u8))
            {
                TryAddSctxRecord(data, i, data.Length, result.ScriptSources);
            }
            else if (MatchesSignature(data, i, "SCRO"u8))
            {
                TryAddScroRecord(data, i, data.Length, result.FormIdReferences, seenFormIds);
            }
            else if (MatchesSignature(data, i, "NAME"u8))
            {
                TryAddNameSubrecord(data, i, data.Length, result.NameReferences);
            }
            else if (MatchesSignature(data, i, "DATA"u8))
            {
                TryAddPositionSubrecord(data, i, data.Length, result.Positions);
            }
            else if (MatchesSignature(data, i, "ACBS"u8))
            {
                TryAddActorBaseSubrecord(data, i, data.Length, result.ActorBases);
            }
            else if (MatchesSignature(data, i, "NAM1"u8))
            {
                TryAddResponseTextSubrecord(data, i, data.Length, result.ResponseTexts);
            }
            else if (MatchesSignature(data, i, "TRDT"u8))
            {
                TryAddResponseDataSubrecord(data, i, data.Length, result.ResponseData);
            }
            // Text-containing subrecords
            else if (MatchesSignature(data, i, "FULL"u8))
            {
                TryAddTextSubrecord(data, i, data.Length, "FULL", result.FullNames);
            }
            else if (MatchesSignature(data, i, "DESC"u8))
            {
                TryAddTextSubrecord(data, i, data.Length, "DESC", result.Descriptions);
            }
            else if (MatchesSignature(data, i, "MODL"u8))
            {
                TryAddPathSubrecord(data, i, data.Length, "MODL", result.ModelPaths);
            }
            else if (MatchesSignature(data, i, "ICON"u8))
            {
                TryAddPathSubrecord(data, i, data.Length, "ICON", result.IconPaths);
            }
            else if (MatchesSignature(data, i, "MICO"u8))
            {
                TryAddPathSubrecord(data, i, data.Length, "MICO", result.IconPaths);
            }
            // Texture set paths (TX00-TX07)
            else if (MatchesTextureSignature(data, i))
            {
                var sig = Encoding.ASCII.GetString(data, i, 4);
                TryAddPathSubrecord(data, i, data.Length, sig, result.TexturePaths);
            }
            // FormID reference subrecords
            else if (MatchesSignature(data, i, "SCRI"u8))
            {
                TryAddFormIdSubrecord(data, i, data.Length, "SCRI", result.ScriptRefs);
            }
            else if (MatchesSignature(data, i, "ENAM"u8))
            {
                TryAddFormIdSubrecord(data, i, data.Length, "ENAM", result.EffectRefs);
            }
            else if (MatchesSignature(data, i, "SNAM"u8))
            {
                TryAddFormIdSubrecord(data, i, data.Length, "SNAM", result.SoundRefs);
            }
            else if (MatchesSignature(data, i, "QNAM"u8))
            {
                TryAddFormIdSubrecord(data, i, data.Length, "QNAM", result.QuestRefs);
            }
            // Condition data
            else if (MatchesSignature(data, i, "CTDA"u8))
            {
                TryAddConditionSubrecord(data, i, data.Length, result.Conditions);
            }
        }

        return result;
    }

    /// <summary>
    ///     Scan an entire memory dump for ESM records using memory-mapped access.
    ///     Processes in chunks to avoid loading the entire file into memory.
    /// </summary>
    /// <param name="accessor">Memory-mapped file accessor.</param>
    /// <param name="fileSize">Total file size in bytes.</param>
    /// <param name="excludeRanges">Optional list of (start, end) ranges to skip (e.g., module memory).</param>
    /// <param name="progress">Optional progress reporter.</param>
    public static EsmRecordScanResult ScanForRecordsMemoryMapped(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        List<(long start, long end)>? excludeRanges = null,
        IProgress<(long bytesProcessed, long totalBytes, int recordsFound)>? progress = null)
    {
        const int chunkSize = 16 * 1024 * 1024; // 16MB chunks
        const int overlapSize = 1024; // Overlap to handle records at chunk boundaries

        var result = new EsmRecordScanResult();
        var seenEdids = new HashSet<string>();
        var seenFormIds = new HashSet<uint>();
        var seenMainRecordOffsets = new HashSet<long>();
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + overlapSize);

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                var toRead = (int)Math.Min(chunkSize + overlapSize, fileSize - offset);

                // Report progress after each chunk
                progress?.Report((offset, fileSize, result.MainRecords.Count));
                accessor.ReadArray(offset, buffer, 0, toRead);

                // Determine the search limit for this chunk
                // Only search up to chunkSize unless this is the last chunk
                var searchLimit = offset + chunkSize >= fileSize ? toRead - 24 : chunkSize;

                // Scan this chunk - optimized with single magic read + switch + smart byte skipping
                var bufferSpan = buffer.AsSpan(0, toRead);
#pragma warning disable S127 // Loop counter modified in body - intentional skip-ahead in binary parsing
                for (var i = 0; i <= searchLimit; i++)
                {
                    // Skip offsets inside excluded ranges (e.g., module memory)
                    var globalOffset = offset + i;
                    if (IsInExcludedRange(globalOffset, excludeRanges))
                    {
                        continue;
                    }

                    // Check for main record headers first - returns record size for skip-ahead
                    var recordSize = TryAddMainRecordHeaderWithOffset(buffer, i, toRead, offset, result.MainRecords,
                        seenMainRecordOffsets);

                    // Smart byte skipping: if we found a valid main record, skip ahead past it
                    // This avoids re-scanning bytes that are part of a known record structure
                    if (recordSize > 24)
                    {
                        // Skip to end of record (minus 1 because loop will increment i)
                        // Cap the skip to stay within searchLimit and leave room for boundary overlap
                        var skipAmount = Math.Min(recordSize - 1, searchLimit - i);
                        if (skipAmount > 0)
                        {
                            i += skipAmount;
                        }

                        continue;
                    }

                    // Read magic once and use switch for subrecord detection
                    // This replaces 20+ MatchesSignature calls per byte with 1 read + 1 switch
                    if (i + 4 > toRead) continue;
                    var magic = BinaryPrimitives.ReadUInt32LittleEndian(bufferSpan.Slice(i, 4));

                    switch (magic)
                    {
                        case SigEdid:
                            TryAddEdidRecordWithOffset(buffer, i, toRead, offset, result.EditorIds, seenEdids);
                            break;
                        case SigGmst:
                            TryAddGmstRecordWithOffset(buffer, i, toRead, offset, result.GameSettings);
                            break;
                        case SigSctx:
                            TryAddSctxRecordWithOffset(buffer, i, toRead, offset, result.ScriptSources);
                            break;
                        case SigScro:
                            TryAddScroRecordWithOffset(buffer, i, toRead, offset, result.FormIdReferences, seenFormIds);
                            break;
                        case SigName:
                            TryAddNameSubrecordWithOffset(buffer, i, toRead, offset, result.NameReferences);
                            break;
                        case SigData:
                            TryAddPositionSubrecordWithOffset(buffer, i, toRead, offset, result.Positions);
                            break;
                        case SigAcbs:
                            TryAddActorBaseSubrecordWithOffset(buffer, i, toRead, offset, result.ActorBases);
                            break;
                        case SigNam1:
                            TryAddResponseTextSubrecordWithOffset(buffer, i, toRead, offset, result.ResponseTexts);
                            break;
                        case SigTrdt:
                            TryAddResponseDataSubrecordWithOffset(buffer, i, toRead, offset, result.ResponseData);
                            break;
                        case SigFull:
                            TryAddTextSubrecordWithOffset(buffer, i, toRead, offset, "FULL", result.FullNames);
                            break;
                        case SigDesc:
                            TryAddTextSubrecordWithOffset(buffer, i, toRead, offset, "DESC", result.Descriptions);
                            break;
                        case SigModl:
                            TryAddPathSubrecordWithOffset(buffer, i, toRead, offset, "MODL", result.ModelPaths);
                            break;
                        case SigIcon:
                            TryAddPathSubrecordWithOffset(buffer, i, toRead, offset, "ICON", result.IconPaths);
                            break;
                        case SigMico:
                            TryAddPathSubrecordWithOffset(buffer, i, toRead, offset, "MICO", result.IconPaths);
                            break;
                        case SigScri:
                            TryAddFormIdSubrecordWithOffset(buffer, i, toRead, offset, "SCRI", result.ScriptRefs);
                            break;
                        case SigEnam:
                            TryAddFormIdSubrecordWithOffset(buffer, i, toRead, offset, "ENAM", result.EffectRefs);
                            break;
                        case SigSnam:
                            TryAddFormIdSubrecordWithOffset(buffer, i, toRead, offset, "SNAM", result.SoundRefs);
                            break;
                        case SigQnam:
                            TryAddFormIdSubrecordWithOffset(buffer, i, toRead, offset, "QNAM", result.QuestRefs);
                            break;
                        case SigCtda:
                            TryAddConditionSubrecordWithOffset(buffer, i, toRead, offset, result.Conditions);
                            break;
                        case SigVhgt:
                            TryAddVhgtHeightmapWithOffset(buffer, i, toRead, offset, false, result.Heightmaps);
                            break;
                        case SigTghv: // BE reversed
                            TryAddVhgtHeightmapWithOffset(buffer, i, toRead, offset, true, result.Heightmaps);
                            break;
                        case SigXclc:
                            TryAddXclcSubrecordWithOffset(buffer, i, toRead, offset, false, result.CellGrids);
                            break;
                        case SigClcx: // BE reversed
                            TryAddXclcSubrecordWithOffset(buffer, i, toRead, offset, true, result.CellGrids);
                            break;
                        default:
                            // Check for texture signatures (TX00-TX07) and generic subrecords
                            if (MatchesTextureSignature(buffer, i))
                            {
                                var sig = Encoding.ASCII.GetString(buffer, i, 4);
                                TryAddPathSubrecordWithOffset(buffer, i, toRead, offset, sig, result.TexturePaths);
                            }
                            else
                            {
                                TryAddGenericSubrecordWithOffset(buffer, i, toRead, offset, result.GenericSubrecords);
                            }

                            break;
                    }
                }
#pragma warning restore S127

                offset += chunkSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
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
        if (!IsValidMainRecordHeader(recordType, dataSize, flags, formId))
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

    private static void TryAddMainRecordHeader(byte[] data, int i, int dataLength,
        List<DetectedMainRecord> records, HashSet<long> seenOffsets)
    {
        if (i + 24 > dataLength || seenOffsets.Contains(i))
        {
            return;
        }

        // Reject known GPU debug patterns BEFORE parsing header
        if (IsKnownFalsePositive(data, i))
        {
            return;
        }

        var magic = BinaryUtils.ReadUInt32LE(data, i);

        // Try little-endian (PC format)
        if (RuntimeRecordMagicLE.Contains(magic))
        {
            var header = TryParseMainRecordHeader(data, i, dataLength, false);
            if (header != null && seenOffsets.Add(i))
            {
                records.Add(header);
            }

            return;
        }

        // Try big-endian (Xbox 360 format) - signature bytes are reversed
        if (RuntimeRecordMagicBE.Contains(magic))
        {
            var header = TryParseMainRecordHeader(data, i, dataLength, true);
            if (header != null && seenOffsets.Add(i))
            {
                records.Add(header);
            }
        }
    }

    /// <summary>
    ///     Try to parse a main record header at position i. If successful, adds to records
    ///     and returns the total record size (24-byte header + data size) for skip-ahead optimization.
    /// </summary>
    /// <returns>Total record size if a valid record was found; 0 otherwise.</returns>
    private static int TryAddMainRecordHeaderWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<DetectedMainRecord> records, HashSet<long> seenOffsets)
    {
        var globalOffset = baseOffset + i;
        if (i + 24 > dataLength || seenOffsets.Contains(globalOffset))
        {
            return 0;
        }

        // Reject known GPU debug patterns BEFORE parsing header
        // These ASCII patterns look like valid 4-char signatures but are GPU register names
        if (IsKnownFalsePositive(data, i))
        {
            return 0;
        }

        var magic = BinaryUtils.ReadUInt32LE(data, i);

        // Try little-endian (PC format)
        if (RuntimeRecordMagicLE.Contains(magic))
        {
            var header = TryParseMainRecordHeaderWithOffset(data, i, dataLength, baseOffset, false);
            if (header != null && seenOffsets.Add(globalOffset))
            {
                records.Add(header);
                // Return total record size: 24-byte header + data size
                return 24 + (int)header.DataSize;
            }

            return 0;
        }

        // Try big-endian (Xbox 360 format)
        if (RuntimeRecordMagicBE.Contains(magic))
        {
            var header = TryParseMainRecordHeaderWithOffset(data, i, dataLength, baseOffset, true);
            if (header != null && seenOffsets.Add(globalOffset))
            {
                records.Add(header);
                // Return total record size: 24-byte header + data size
                return 24 + (int)header.DataSize;
            }
        }

        return 0;
    }

    #endregion
}
