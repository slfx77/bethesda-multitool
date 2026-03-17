using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Extracts structured data (positions, conditions, REFR records, etc.) from parsed ESM records.
///     Separated from <see cref="EsmFileAnalyzer" /> to keep the analysis orchestrator focused on
///     high-level workflow while this class handles subrecord-level data extraction.
/// </summary>
internal static class EsmDataExtractor
{
    /// <summary>
    ///     Converts parsed records to EsmRecordScanResult for compatibility with existing UI.
    /// </summary>
    internal static EsmRecordScanResult ConvertToScanResult(
        List<ParsedMainRecord> records,
        bool bigEndian,
        Dictionary<uint, uint>? cellToWorldspaceMap = null,
        Dictionary<uint, uint>? landToWorldspaceMap = null,
        Dictionary<uint, List<uint>>? cellToRefrMap = null,
        Dictionary<uint, List<uint>>? topicToInfoMap = null)
    {
        var mainRecords = new List<DetectedMainRecord>();
        var editorIds = new List<EdidRecord>();
        var fullNames = new List<TextSubrecord>();
        var descriptions = new List<TextSubrecord>();
        var modelPaths = new List<TextSubrecord>();
        var iconPaths = new List<TextSubrecord>();
        var nameReferences = new List<NameSubrecord>();
        var positions = new List<PositionSubrecord>();
        var conditions = new List<ConditionSubrecord>();
        var cellGrids = new List<CellGridSubrecord>();

        foreach (var record in records)
        {
            // Convert header to DetectedMainRecord
            mainRecords.Add(new DetectedMainRecord(
                record.Header.Signature,
                record.Header.DataSize,
                record.Header.Flags,
                record.Header.FormId,
                record.Offset,
                bigEndian));

            // Extract subrecord data
            foreach (var sub in record.Subrecords)
            {
                switch (sub.Signature)
                {
                    case "EDID":
                        editorIds.Add(new EdidRecord(sub.DataAsString ?? "", record.Offset));
                        break;

                    case "FULL":
                        fullNames.Add(new TextSubrecord("FULL", sub.DataAsString ?? "", record.Offset));
                        break;

                    case "DESC":
                        descriptions.Add(new TextSubrecord("DESC", sub.DataAsString ?? "", record.Offset));
                        break;

                    case "MODL":
                        modelPaths.Add(new TextSubrecord("MODL", sub.DataAsString ?? "", record.Offset));
                        break;

                    case "ICON":
                    case "MICO":
                        iconPaths.Add(new TextSubrecord(sub.Signature, sub.DataAsString ?? "", record.Offset));
                        break;

                    case "NAME" when sub.Data.Length >= 4:
                        var refFormId = bigEndian
                            ? (uint)((sub.Data[0] << 24) | (sub.Data[1] << 16) | (sub.Data[2] << 8) | sub.Data[3])
                            : (uint)(sub.Data[0] | (sub.Data[1] << 8) | (sub.Data[2] << 16) | (sub.Data[3] << 24));
                        nameReferences.Add(new NameSubrecord(refFormId, record.Offset, bigEndian));
                        break;

                    case "DATA" when sub.Data.Length >= 24 && IsPositionRecord(record.Header.Signature):
                        // Position data: X, Y, Z, rX, rY, rZ (6 floats)
                        positions.Add(ExtractPosition(sub.Data, record.Offset, bigEndian));
                        break;

                    case "CTDA" when sub.Data.Length >= 24:
                        conditions.Add(ExtractCondition(sub.Data, record.Offset, bigEndian));
                        break;

                    case "XCLC" when sub.Data.Length >= 8 && record.Header.Signature == "CELL":
                    {
                        var gridX = bigEndian
                            ? BinaryPrimitives.ReadInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadInt32LittleEndian(sub.Data);
                        var gridY = bigEndian
                            ? BinaryPrimitives.ReadInt32BigEndian(sub.Data.AsSpan(4))
                            : BinaryPrimitives.ReadInt32LittleEndian(sub.Data.AsSpan(4));
                        cellGrids.Add(new CellGridSubrecord { GridX = gridX, GridY = gridY, Offset = record.Offset });
                    }
                        break;
                }
            }
        }

        return new EsmRecordScanResult
        {
            MainRecords = mainRecords,
            EditorIds = editorIds,
            FullNames = fullNames,
            Descriptions = descriptions,
            ModelPaths = modelPaths,
            IconPaths = iconPaths,
            NameReferences = nameReferences,
            Positions = positions,
            Conditions = conditions,
            CellGrids = cellGrids,
            CellToWorldspaceMap = cellToWorldspaceMap ?? [],
            LandToWorldspaceMap = landToWorldspaceMap ?? [],
            CellToRefrMap = cellToRefrMap ?? [],
            TopicToInfoMap = topicToInfoMap ?? []
        };
    }

    /// <summary>
    ///     Build ExtractedRefrRecord entries from parsed REFR/ACHR/ACRE records.
    ///     Mirrors the logic in EsmRecordFormat.ExtractRefrFromBuffer but reads from
    ///     already-parsed subrecords instead of raw byte buffers.
    /// </summary>
    internal static void ExtractRefrRecordsFromParsed(
        EsmRecordScanResult scanResult,
        List<ParsedMainRecord> records,
        bool bigEndian)
    {
        foreach (var record in records)
        {
            if (record.Header.Signature is not ("REFR" or "ACHR" or "ACRE"))
            {
                continue;
            }

            uint baseFormId = 0;
            PositionSubrecord? position = null;
            var scale = 1.0f;
            float? radius = null;
            uint? ownerFormId = null;
            uint? encounterZoneFormId = null;
            byte? lockLevel = null;
            uint? lockKeyFormId = null;
            byte? lockFlags = null;
            uint? lockNumTries = null;
            uint? lockTimesUnlocked = null;
            uint? destinationDoorFormId = null;
            uint? enableParentFormId = null;
            byte? enableParentFlags = null;
            uint? linkedRefKeywordFormId = null;
            uint? linkedRefFormId = null;
            var isMapMarker = false;
            ushort? markerType = null;
            string? markerName = null;

            foreach (var sub in record.Subrecords)
            {
                switch (sub.Signature)
                {
                    case "NAME" when sub.Data.Length >= 4:
                        baseFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                        break;
                    case "DATA" when sub.Data.Length >= 24:
                        position = ExtractPosition(sub.Data, record.Offset, bigEndian);
                        break;
                    case "XSCL" when sub.Data.Length == 4:
                        scale = bigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(sub.Data)
                            : BinaryPrimitives.ReadSingleLittleEndian(sub.Data);
                        break;
                    case "XRDS" when sub.Data.Length == 4:
                    {
                        var parsedRadius = bigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(sub.Data)
                            : BinaryPrimitives.ReadSingleLittleEndian(sub.Data);
                        if (float.IsFinite(parsedRadius) && parsedRadius > 0)
                        {
                            radius = parsedRadius;
                        }

                        break;
                    }
                    case "XOWN" when sub.Data.Length == 4:
                        ownerFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                        break;
                    case "XEZN" when sub.Data.Length == 4:
                        encounterZoneFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                        break;
                    case "XLOC" when sub.Data.Length >= 20:
                        lockLevel = sub.Data[0];
                        lockKeyFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data.AsSpan(4, 4))
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(4, 4));
                        lockFlags = sub.Data[8];
                        lockNumTries = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data.AsSpan(12, 4))
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(12, 4));
                        lockTimesUnlocked = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data.AsSpan(16, 4))
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(16, 4));
                        break;
                    case "XTEL" when sub.Data.Length >= 4:
                        destinationDoorFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                        break;
                    case "XESP" when sub.Data.Length >= 8:
                        enableParentFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                        enableParentFlags = sub.Data[4];
                        break;
                    case "XLKR" when sub.Data.Length >= 8:
                        linkedRefKeywordFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data.AsSpan(0, 4))
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(0, 4));
                        linkedRefFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data.AsSpan(4, 4))
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(4, 4));
                        break;
                    case "XLKR" when sub.Data.Length == 4:
                        linkedRefFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                        break;
                    case "XMRK":
                        isMapMarker = true;
                        break;
                    case "TNAM" when sub.Data.Length == 2:
                        markerType = bigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt16LittleEndian(sub.Data);
                        break;
                    case "FULL":
                        markerName = sub.DataAsString;
                        break;
                }
            }

            if (baseFormId == 0)
            {
                continue;
            }

            var header = new DetectedMainRecord(
                record.Header.Signature,
                record.Header.DataSize,
                record.Header.Flags,
                record.Header.FormId,
                record.Offset,
                bigEndian);

            scanResult.RefrRecords.Add(new ExtractedRefrRecord
            {
                Header = header,
                BaseFormId = baseFormId,
                Position = position,
                Scale = scale,
                Radius = radius,
                OwnerFormId = ownerFormId,
                EncounterZoneFormId = encounterZoneFormId,
                LockLevel = lockLevel,
                LockKeyFormId = lockKeyFormId,
                LockFlags = lockFlags,
                LockNumTries = lockNumTries,
                LockTimesUnlocked = lockTimesUnlocked,
                DestinationDoorFormId = destinationDoorFormId,
                EnableParentFormId = enableParentFormId,
                EnableParentFlags = enableParentFlags,
                IsMapMarker = isMapMarker,
                MarkerType = markerType,
                MarkerName = markerName,
                LinkedRefKeywordFormId = linkedRefKeywordFormId,
                LinkedRefFormId = linkedRefFormId
            });
        }
    }

    internal static bool IsPositionRecord(string signature)
    {
        return signature is "REFR" or "ACHR" or "ACRE" or "PGRE" or "PMIS";
    }

    internal static PositionSubrecord ExtractPosition(byte[] data, long offset, bool bigEndian)
    {
        float ReadFloat(int o)
        {
            return bigEndian
                ? BitConverter.UInt32BitsToSingle((uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) |
                                                         data[o + 3]))
                : BitConverter.ToSingle(data, o);
        }

        return new PositionSubrecord(
            ReadFloat(0), ReadFloat(4), ReadFloat(8), // X, Y, Z
            ReadFloat(12), ReadFloat(16), ReadFloat(20), // RotX, RotY, RotZ
            offset, bigEndian);
    }

    internal static ConditionSubrecord ExtractCondition(byte[] data, long offset, bool bigEndian)
    {
        uint ReadUInt32(int o)
        {
            return bigEndian
                ? (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3])
                : (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24));
        }

        ushort ReadUInt16(int o)
        {
            return bigEndian
                ? (ushort)((data[o] << 8) | data[o + 1])
                : (ushort)(data[o] | (data[o + 1] << 8));
        }

        float ReadFloat(int o)
        {
            return BitConverter.UInt32BitsToSingle(ReadUInt32(o));
        }

        // CTDA structure: Type(1) + unused(3) + CompValue(4) + FuncIdx(2) + unused(2) + Param1(4) + Param2(4) + RunOn(4)
        return new ConditionSubrecord(
            data[0], // Type
            (byte)((data[0] >> 5) & 0x7), // Operator (bits 5-7 of Type byte)
            ReadFloat(4), // ComparisonValue
            ReadUInt16(8), // FunctionIndex
            ReadUInt32(12), // Param1
            ReadUInt32(16), // Param2
            offset);
    }
}
