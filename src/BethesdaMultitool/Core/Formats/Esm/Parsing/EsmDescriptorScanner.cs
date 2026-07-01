using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

internal readonly record struct EsmRecordDescriptor(
    string Signature,
    uint DataSize,
    uint Flags,
    uint FormId,
    long Offset,
    bool IsBigEndian);

internal readonly record struct EsmSubrecordDescriptor(
    string Signature,
    int DataOffset,
    int DataLength);

internal sealed record EsmDescriptorScanResult(
    EsmRecordScanResult ScanResult,
    List<GrupHeaderInfo> GrupHeaders,
    Dictionary<uint, string> FormIdMap);

internal static class EsmDescriptorScanner
{
    /// <param name="onBytesScanned">
    ///     Optional progress callback invoked once per top-level record/GRUP with the byte offset consumed
    ///     so far. Fires at top-level granularity only (NOT inside <see cref="ParseGroupRecursive" />, which
    ///     runs millions of times) — coarse enough to be cheap, fine enough to drive a smooth progress bar
    ///     on large ESMs. Callers should throttle their own reporting.
    /// </param>
    internal static EsmDescriptorScanResult Scan(
        ReadOnlySpan<byte> data, Action<long>? onBytesScanned = null)
    {
        var scanResult = new EsmRecordScanResult();
        var grupHeaders = new List<GrupHeaderInfo>();
        var formIdMap = new Dictionary<uint, string>();
        var refrStates = new Dictionary<long, RefrState>();

        var bigEndian = EsmParser.IsBigEndian(data);
        var format = PluginFormat.Detect(data);

        // TES3 (Morrowind) is a flat record stream with a different framing — no GRUPs, no FormIDs,
        // 16-byte record headers, and 4-byte subrecord sizes. Walk it separately.
        if (GameProfiles.For(format.Game).IsTes3)
        {
            return ScanTes3(data, scanResult, grupHeaders, formIdMap);
        }

        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian, format);
        if (tes4Header == null || tes4Header.Signature != "TES4")
        {
            return new EsmDescriptorScanResult(scanResult, grupHeaders, formIdMap);
        }

        var offset = (long)format.RecordHeaderSize + tes4Header.DataSize;
        while (offset + format.RecordHeaderSize <= data.Length)
        {
            onBytesScanned?.Invoke(offset);
            var sig = ReadSignature(data[(int)offset..], bigEndian);

            if (sig == "GRUP")
            {
                var actualEnd = ParseGroupRecursive(data, offset, bigEndian, format, scanResult, grupHeaders,
                    formIdMap, refrStates);
                var groupHeader = EsmParser.ParseGroupHeader(data[(int)offset..], bigEndian, format);
                if (groupHeader == null || groupHeader.GroupSize < format.GroupHeaderSize)
                {
                    break;
                }

                var declaredEnd = offset + groupHeader.GroupSize;
                offset = Math.Max(declaredEnd, actualEnd);
            }
            else if (sig == "TOFT")
            {
                var toftHeader = EsmParser.ParseRecordHeader(data[(int)offset..], bigEndian, format);
                if (toftHeader == null)
                {
                    break;
                }

                if (toftHeader.DataSize > 0)
                {
                    ScanToftBlock(data, offset + format.RecordHeaderSize,
                        offset + format.RecordHeaderSize + toftHeader.DataSize,
                        bigEndian, format, scanResult, formIdMap, refrStates);
                    offset += format.RecordHeaderSize + toftHeader.DataSize;
                }
                else
                {
                    offset += format.RecordHeaderSize;
                }
            }
            else
            {
                var recordHeader = EsmParser.ParseRecordHeader(data[(int)offset..], bigEndian, format);
                if (recordHeader == null)
                {
                    break;
                }

                ProcessRecord(data, offset, recordHeader, bigEndian, format, scanResult, formIdMap, refrStates);
                offset += format.RecordHeaderSize + recordHeader.DataSize;
            }
        }

        FinalizeRefrRecords(scanResult, refrStates);
        return new EsmDescriptorScanResult(scanResult, grupHeaders, formIdMap);
    }

    private static long ParseGroupRecursive(
        ReadOnlySpan<byte> data,
        long groupOffset,
        bool bigEndian,
        PluginFormat format,
        EsmRecordScanResult scanResult,
        List<GrupHeaderInfo> grupHeaders,
        Dictionary<uint, string> formIdMap,
        Dictionary<long, RefrState> refrStates)
    {
        var groupHeader = EsmParser.ParseGroupHeader(data[(int)groupOffset..], bigEndian, format);
        if (groupHeader == null || groupHeader.GroupSize < format.GroupHeaderSize)
        {
            return groupOffset;
        }

        grupHeaders.Add(new GrupHeaderInfo
        {
            Offset = groupOffset,
            GroupSize = groupHeader.GroupSize,
            Label = groupHeader.Label,
            GroupType = groupHeader.GroupType,
            Stamp = groupHeader.Stamp
        });

        var groupEnd = groupOffset + groupHeader.GroupSize;
        var offset = groupOffset + format.GroupHeaderSize;

        while (offset + format.RecordHeaderSize <= data.Length && offset < groupEnd)
        {
            var sig = ReadSignature(data[(int)offset..], bigEndian);

            if (sig == "GRUP")
            {
                var nestedEnd = ParseGroupRecursive(data, offset, bigEndian, format, scanResult, grupHeaders,
                    formIdMap, refrStates);
                var nestedHeader = EsmParser.ParseGroupHeader(data[(int)offset..], bigEndian, format);
                if (nestedHeader == null || nestedHeader.GroupSize < format.GroupHeaderSize)
                {
                    break;
                }

                var declaredEnd = offset + nestedHeader.GroupSize;
                offset = Math.Max(declaredEnd, nestedEnd);
            }
            else if (sig == "TOFT")
            {
                var toftHeader = EsmParser.ParseRecordHeader(data[(int)offset..], bigEndian, format);
                if (toftHeader == null)
                {
                    break;
                }

                if (toftHeader.DataSize > 0)
                {
                    ScanToftBlock(data, offset + format.RecordHeaderSize,
                        offset + format.RecordHeaderSize + toftHeader.DataSize,
                        bigEndian, format, scanResult, formIdMap, refrStates);
                    offset += format.RecordHeaderSize + toftHeader.DataSize;
                }
                else
                {
                    offset += format.RecordHeaderSize;
                }
            }
            else
            {
                var recordHeader = EsmParser.ParseRecordHeader(data[(int)offset..], bigEndian, format);
                if (recordHeader == null)
                {
                    break;
                }

                ProcessRecord(data, offset, recordHeader, bigEndian, format, scanResult, formIdMap, refrStates);
                offset += format.RecordHeaderSize + recordHeader.DataSize;
            }
        }

        return offset;
    }

    private static void ScanToftBlock(
        ReadOnlySpan<byte> data,
        long start,
        long end,
        bool bigEndian,
        PluginFormat format,
        EsmRecordScanResult scanResult,
        Dictionary<uint, string> formIdMap,
        Dictionary<long, RefrState> refrStates)
    {
        var offset = start;
        while (offset + format.RecordHeaderSize <= end)
        {
            var sig = ReadSignature(data[(int)offset..], bigEndian);
            if (sig == "GRUP")
            {
                break;
            }

            var header = EsmParser.ParseRecordHeader(data[(int)offset..], bigEndian, format);
            if (header == null)
            {
                break;
            }

            if (sig == "TOFT")
            {
                if (header.DataSize > 0)
                {
                    ScanToftBlock(data, offset + format.RecordHeaderSize,
                        offset + format.RecordHeaderSize + header.DataSize,
                        bigEndian, format, scanResult, formIdMap, refrStates);
                    offset += format.RecordHeaderSize + header.DataSize;
                }
                else
                {
                    offset += format.RecordHeaderSize;
                }

                continue;
            }

            var recordEnd = offset + format.RecordHeaderSize + header.DataSize;
            if (recordEnd <= offset || recordEnd > end)
            {
                break;
            }

            ProcessRecord(data, offset, header, bigEndian, format, scanResult, formIdMap, refrStates);
            offset = recordEnd;
        }
    }

    /// <summary>
    ///     Scan a Morrowind (TES3) plugin: a flat sequence of records with no GRUPs and no FormIDs.
    ///     Each record is a 16-byte header (Type + Data Size + Header1 + Flags) followed by its
    ///     subrecords, which use a 4-byte size field (vs 2-byte in TES4+). Sequential FormIDs are
    ///     synthesized so the FormID-keyed downstream pipeline can index the records. Subrecord-level
    ///     semantics differ entirely from TES4 — this enumerates record types and editor IDs so the
    ///     plugin loads and its structure is browsable; per-type field parsing is not modeled here.
    /// </summary>
    private static EsmDescriptorScanResult ScanTes3(
        ReadOnlySpan<byte> data,
        EsmRecordScanResult scanResult,
        List<GrupHeaderInfo> grupHeaders,
        Dictionary<uint, string> formIdMap)
    {
        const int tes3HeaderSize = 16;
        uint syntheticFormId = 1;
        long offset = 0;
        scanResult.IsTes3 = true;

        while (offset + tes3HeaderSize <= data.Length)
        {
            var sig = ReadSignature(data[(int)offset..], false);
            if (!IsValidSignature(sig))
            {
                break;
            }

            var dataSize = ReadUInt32(data, (int)offset + 4, false);
            var flags = ReadUInt32(data, (int)offset + 12, false);
            var dataStart = offset + tes3HeaderSize;
            if (dataStart + dataSize > data.Length)
            {
                break;
            }

            var formId = syntheticFormId++;
            var record = new DetectedMainRecord(sig, dataSize, flags, formId, offset, false)
            {
                HeaderSize = tes3HeaderSize
            };
            scanResult.MainRecords.Add(record);

            ProcessTes3Subrecords(
                data.Slice((int)dataStart, (int)dataSize), record, formId, scanResult, formIdMap);

            offset = dataStart + dataSize;
        }

        return new EsmDescriptorScanResult(scanResult, grupHeaders, formIdMap);
    }

    private static void ProcessTes3Subrecords(
        ReadOnlySpan<byte> recordData,
        DetectedMainRecord record,
        uint formId,
        EsmRecordScanResult scanResult,
        Dictionary<uint, string> formIdMap)
    {
        var offset = 0;
        while (offset + 8 <= recordData.Length)
        {
            var sig = ReadSignature(recordData[offset..], false);
            if (!IsValidSignature(sig))
            {
                break;
            }

            var length = checked((int)ReadUInt32(recordData, offset + 4, false));
            offset += 8;
            if (length < 0 || offset + length > recordData.Length)
            {
                break;
            }

            var subData = recordData.Slice(offset, length);
            switch (sig)
            {
                case "NAME": // Morrowind's per-record object/editor ID
                {
                    var editorId = EsmStringUtils.ReadNullTermString(subData);
                    scanResult.EditorIds.Add(new EdidRecord(editorId, record.Offset));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        formIdMap.TryAdd(formId, editorId);
                    }

                    break;
                }
                case "FNAM": // display / full name
                    scanResult.FullNames.Add(new TextSubrecord(
                        "FULL", EsmStringUtils.ReadNullTermString(subData), record.Offset));
                    break;
                case "MODL":
                    scanResult.ModelPaths.Add(new TextSubrecord(
                        "MODL", EsmStringUtils.ReadNullTermString(subData), record.Offset));
                    break;
            }

            offset += length;
        }
    }

    private static void ProcessRecord(
        ReadOnlySpan<byte> fileData,
        long offset,
        MainRecordHeader header,
        bool bigEndian,
        PluginFormat format,
        EsmRecordScanResult scanResult,
        Dictionary<uint, string> formIdMap,
        Dictionary<long, RefrState> refrStates)
    {
        var record = new DetectedMainRecord(
            header.Signature,
            header.DataSize,
            header.Flags,
            header.FormId,
            offset,
            bigEndian)
        {
            HeaderSize = format.RecordHeaderSize
        };
        scanResult.MainRecords.Add(record);

        var dataStart = offset + format.RecordHeaderSize;
        if (dataStart + header.DataSize > fileData.Length)
        {
            return;
        }

        var recordData = fileData.Slice((int)dataStart, (int)header.DataSize);
        if ((header.Flags & EsmParser.CompressedFlag) != 0 && recordData.Length > 4)
        {
            var decompressed = EsmParser.DecompressRecordData(recordData, bigEndian);
            if (decompressed == null)
            {
                return;
            }

            ProcessSubrecords(decompressed, record, scanResult, formIdMap, refrStates);
            return;
        }

        ProcessSubrecords(recordData, record, scanResult, formIdMap, refrStates);
    }

    private static void ProcessSubrecords(
        ReadOnlySpan<byte> recordData,
        DetectedMainRecord record,
        EsmRecordScanResult scanResult,
        Dictionary<uint, string> formIdMap,
        Dictionary<long, RefrState> refrStates)
    {
        var offset = 0;
        uint? extendedSize = null;

        while (offset + EsmParser.SubrecordHeaderSize <= recordData.Length)
        {
            var sig = ReadSignature(recordData[offset..], record.IsBigEndian);
            if (!IsValidSignature(sig))
            {
                break;
            }

            var dataLen = ReadUInt16(recordData, offset + 4, record.IsBigEndian);
            offset += EsmParser.SubrecordHeaderSize;

            if (sig == "XXXX" && dataLen == 4 && offset + 4 <= recordData.Length)
            {
                extendedSize = ReadUInt32(recordData, offset, record.IsBigEndian);
                offset += 4;
                continue;
            }

            var effectiveLength = checked((int)(extendedSize ?? dataLen));
            extendedSize = null;
            if (effectiveLength < 0 || offset + effectiveLength > recordData.Length)
            {
                break;
            }

            var subData = recordData.Slice(offset, effectiveLength);
            ProcessSubrecord(sig, subData, record, scanResult, formIdMap, refrStates);
            offset += effectiveLength;
        }
    }

    private static void ProcessSubrecord(
        string sig,
        ReadOnlySpan<byte> subData,
        DetectedMainRecord record,
        EsmRecordScanResult scanResult,
        Dictionary<uint, string> formIdMap,
        Dictionary<long, RefrState> refrStates)
    {
        switch (sig)
        {
            case "EDID":
            {
                var editorId = EsmStringUtils.ReadNullTermString(subData);
                scanResult.EditorIds.Add(new EdidRecord(editorId, record.Offset));
                if (record.FormId != 0 && !string.IsNullOrEmpty(editorId))
                {
                    formIdMap.TryAdd(record.FormId, editorId);
                }

                AddRefrField(sig, subData, record, refrStates);
                break;
            }
            case "FULL":
                scanResult.FullNames.Add(new TextSubrecord("FULL", EsmStringUtils.ReadNullTermString(subData),
                    record.Offset));
                AddRefrField(sig, subData, record, refrStates);
                break;
            case "DESC":
                scanResult.Descriptions.Add(new TextSubrecord("DESC", EsmStringUtils.ReadNullTermString(subData),
                    record.Offset));
                break;
            case "MODL":
                scanResult.ModelPaths.Add(new TextSubrecord("MODL", EsmStringUtils.ReadNullTermString(subData),
                    record.Offset));
                break;
            case "ICON":
            case "MICO":
                scanResult.IconPaths.Add(new TextSubrecord(sig, EsmStringUtils.ReadNullTermString(subData),
                    record.Offset));
                break;
            case "NAME" when subData.Length >= 4:
                scanResult.NameReferences.Add(new NameSubrecord(ReadUInt32(subData, 0, record.IsBigEndian),
                    record.Offset, record.IsBigEndian));
                AddRefrField(sig, subData, record, refrStates);
                break;
            case "DATA" when subData.Length >= 24 && EsmDataExtractor.IsPositionRecord(record.RecordType):
                scanResult.Positions.Add(EsmDataExtractor.ExtractPosition(subData[..24].ToArray(), record.Offset,
                    record.IsBigEndian));
                AddRefrField(sig, subData, record, refrStates);
                break;
            case "CTDA" when subData.Length >= 24:
                scanResult.Conditions.Add(EsmDataExtractor.ExtractCondition(subData.ToArray(), record.Offset,
                    record.IsBigEndian));
                break;
            case "XCLC" when subData.Length >= 8 && record.RecordType == "CELL":
                scanResult.CellGrids.Add(new CellGridSubrecord
                {
                    GridX = ReadInt32(subData, 0, record.IsBigEndian),
                    GridY = ReadInt32(subData, 4, record.IsBigEndian),
                    CellFormId = record.FormId,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
                break;
            default:
                AddRefrField(sig, subData, record, refrStates);
                break;
        }
    }

    private static void AddRefrField(
        string sig,
        ReadOnlySpan<byte> subData,
        DetectedMainRecord record,
        Dictionary<long, RefrState> refrStates)
    {
        if (record.RecordType is not ("REFR" or "ACHR" or "ACRE"))
        {
            return;
        }

        var state = GetOrCreateRefrState(refrStates, record);
        switch (sig)
        {
            case "EDID":
                state.EditorId = EsmStringUtils.ReadNullTermString(subData);
                break;
            case "NAME" when subData.Length >= 4:
                state.BaseFormId = ReadUInt32(subData, 0, record.IsBigEndian);
                break;
            case "DATA" when subData.Length >= 24:
                state.Position = EsmDataExtractor.ExtractPosition(subData[..24].ToArray(), record.Offset,
                    record.IsBigEndian);
                break;
            case "XSCL" when subData.Length == 4:
                state.Scale = ReadSingle(subData, 0, record.IsBigEndian);
                break;
            case "XRDS" when subData.Length == 4:
                var radius = ReadSingle(subData, 0, record.IsBigEndian);
                if (float.IsFinite(radius) && radius > 0)
                {
                    state.Radius = radius;
                }

                break;
            case "XOWN" when subData.Length == 4:
                state.OwnerFormId = ReadUInt32(subData, 0, record.IsBigEndian);
                break;
            case "XEZN" when subData.Length == 4:
                state.EncounterZoneFormId = ReadUInt32(subData, 0, record.IsBigEndian);
                break;
            case "XCNT" when subData.Length >= 4:
                state.Count = (short)ReadInt32(subData, 0, record.IsBigEndian);
                break;
            case "XLOC" when subData.Length >= 20:
                state.LockLevel = subData[0];
                state.LockKeyFormId = ReadUInt32(subData, 4, record.IsBigEndian);
                state.LockFlags = subData[8];
                state.LockNumTries = ReadUInt32(subData, 12, record.IsBigEndian);
                state.LockTimesUnlocked = ReadUInt32(subData, 16, record.IsBigEndian);
                break;
            case "XTEL" when subData.Length >= 4:
                state.DestinationDoorFormId = ReadUInt32(subData, 0, record.IsBigEndian);
                if (subData.Length >= 28)
                {
                    state.TeleportPosRot = new PositionSubrecord(
                        ReadSingle(subData, 4, record.IsBigEndian),
                        ReadSingle(subData, 8, record.IsBigEndian),
                        ReadSingle(subData, 12, record.IsBigEndian),
                        ReadSingle(subData, 16, record.IsBigEndian),
                        ReadSingle(subData, 20, record.IsBigEndian),
                        ReadSingle(subData, 24, record.IsBigEndian),
                        record.Offset,
                        record.IsBigEndian);
                }

                if (subData.Length >= 29)
                {
                    state.TeleportFlags = subData[28];
                }

                break;
            case "XESP" when subData.Length >= 8:
                state.EnableParentFormId = ReadUInt32(subData, 0, record.IsBigEndian);
                state.EnableParentFlags = subData[4];
                break;
            case "XLKR" when subData.Length >= 8:
                state.LinkedRefKeywordFormId = ReadUInt32(subData, 0, record.IsBigEndian);
                state.LinkedRefFormId = ReadUInt32(subData, 4, record.IsBigEndian);
                break;
            case "XLKR" when subData.Length == 4:
                state.LinkedRefFormId = ReadUInt32(subData, 0, record.IsBigEndian);
                break;
            case "XMRK":
                state.IsMapMarker = true;
                break;
            case "TNAM" when subData.Length == 2:
                state.MarkerType = ReadUInt16(subData, 0, record.IsBigEndian);
                break;
            case "FULL":
                // Keep the RAW FULL bytes: in a localized plugin (Skyrim/FO4) this is a 4-byte string ID, not
                // inline text — the .STRINGS table needed to resolve it isn't loaded yet at scan time, so the
                // eager null-term decode yields garbage. The consumer (WorldRecordHandler.ExtractMapMarkers)
                // re-resolves these bytes via RecordParserContext.ReadFullName once the table is available.
                // The eager decode stays as a fallback for paths without a context (and is correct for
                // non-localized FNV/Xbox FULLs, which are inline strings).
                state.MarkerNameRaw = subData.ToArray();
                state.MarkerName = EsmStringUtils.ReadNullTermString(subData);
                break;
        }
    }

    private static void FinalizeRefrRecords(EsmRecordScanResult scanResult, Dictionary<long, RefrState> refrStates)
    {
        if (refrStates.Count == 0)
        {
            return;
        }

        foreach (var state in refrStates.Values)
        {
            if (state.BaseFormId == 0)
            {
                continue;
            }

            scanResult.RefrRecords.Add(new ExtractedRefrRecord
            {
                Header = state.Header,
                BaseFormId = state.BaseFormId,
                Position = state.Position,
                Scale = state.Scale,
                Radius = state.Radius,
                Count = state.Count,
                OwnerFormId = state.OwnerFormId,
                EncounterZoneFormId = state.EncounterZoneFormId,
                LockLevel = state.LockLevel,
                LockKeyFormId = state.LockKeyFormId,
                LockFlags = state.LockFlags,
                LockNumTries = state.LockNumTries,
                LockTimesUnlocked = state.LockTimesUnlocked,
                DestinationDoorFormId = state.DestinationDoorFormId,
                TeleportPosRot = state.TeleportPosRot,
                TeleportFlags = state.TeleportFlags,
                EnableParentFormId = state.EnableParentFormId,
                EnableParentFlags = state.EnableParentFlags,
                IsMapMarker = state.IsMapMarker,
                MarkerType = state.MarkerType,
                MarkerName = state.MarkerName,
                MarkerNameRaw = state.MarkerNameRaw,
                EditorId = state.EditorId,
                LinkedRefKeywordFormId = state.LinkedRefKeywordFormId,
                LinkedRefFormId = state.LinkedRefFormId
            });
        }

        refrStates.Clear();
    }

    private static RefrState GetOrCreateRefrState(Dictionary<long, RefrState> refrStates, DetectedMainRecord record)
    {
        if (refrStates.TryGetValue(record.Offset, out var state))
        {
            return state;
        }

        state = new RefrState(record);
        refrStates[record.Offset] = state;
        return state;
    }

    private sealed class RefrState(DetectedMainRecord header)
    {
        public DetectedMainRecord Header { get; } = header;
        public uint BaseFormId { get; set; }
        public PositionSubrecord? Position { get; set; }
        public float Scale { get; set; } = 1.0f;
        public float? Radius { get; set; }
        public short? Count { get; set; }
        public uint? OwnerFormId { get; set; }
        public uint? EncounterZoneFormId { get; set; }
        public byte? LockLevel { get; set; }
        public uint? LockKeyFormId { get; set; }
        public byte? LockFlags { get; set; }
        public uint? LockNumTries { get; set; }
        public uint? LockTimesUnlocked { get; set; }
        public uint? DestinationDoorFormId { get; set; }
        public PositionSubrecord? TeleportPosRot { get; set; }
        public byte? TeleportFlags { get; set; }
        public uint? EnableParentFormId { get; set; }
        public byte? EnableParentFlags { get; set; }
        public uint? LinkedRefKeywordFormId { get; set; }
        public uint? LinkedRefFormId { get; set; }
        public bool IsMapMarker { get; set; }
        public ushort? MarkerType { get; set; }
        public string? MarkerName { get; set; }

        /// <summary>Raw FULL subrecord bytes (a 4-byte string ID in a localized plugin) for late resolution
        /// against the .STRINGS table, which isn't loaded at scan time. See the FULL case above.</summary>
        public byte[]? MarkerNameRaw { get; set; }
        public string? EditorId { get; set; }
    }

    private static string ReadSignature(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 4)
        {
            return string.Empty;
        }

        if (!bigEndian)
        {
            return Encoding.ASCII.GetString(data[..4]);
        }

        Span<byte> reversed = stackalloc byte[4];
        reversed[0] = data[3];
        reversed[1] = data[2];
        reversed[2] = data[1];
        reversed[3] = data[0];
        return Encoding.ASCII.GetString(reversed);
    }

    private static bool IsValidSignature(string signature)
    {
        if (signature.Length != 4)
        {
            return false;
        }

        foreach (var c in signature)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
}

