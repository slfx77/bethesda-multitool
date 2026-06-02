using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Subrecords;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Records;

/// <summary>
///     Extracts world-related ESM records (REFR, LAND) and subrecords (positions, heightmaps, cell grids)
///     from memory dumps and ESM files. Supports both PC (little-endian) and Xbox 360 (big-endian) formats.
/// </summary>
internal static class EsmWorldExtractor
{
    private static readonly HashSet<string> StructuralReferenceSubrecords = new(StringComparer.Ordinal)
    {
        "XOCP",
        "XORD",
        "XMBO",
        "XPRM",
        "XPOD",
        "XNDP"
    };

    #region REFR Record Extraction

    /// <summary>
    ///     Extract full REFR records with position and base object data.
    /// </summary>
    internal static void ExtractRefrRecords(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? editorIdMap = null)
    {
        // REFR records are usually small (<1 KB) but heavily-scripted refs or those carrying
        // large XPRD/XLOC payloads can exceed it. The previous fixed 1 KB rent + Math.Min
        // silently dropped any tail past 1024 bytes; grow on demand instead.
        const int InitialBufferSize = 4 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);

        try
        {
            var refrRecords = scanResult.MainRecords
                .Where(r => r.RecordType is "REFR" or "ACHR" or "ACRE")
                .ToList();

            foreach (var header in refrRecords)
            {
                var dataStart = header.Offset + 24;
                var dataSize = (int)header.DataSize;

                if (dataStart + dataSize > fileSize)
                {
                    continue;
                }

                if (dataSize > buffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(dataSize);
                }

                accessor.ReadArray(dataStart, buffer, 0, dataSize);

                var refr = ExtractRefrFromBuffer(buffer, dataSize, header, editorIdMap);
                if (refr != null)
                {
                    scanResult.RefrRecords.Add(refr);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ExtractedRefrRecord? ExtractRefrFromBuffer(
        byte[] data,
        int dataSize,
        DetectedMainRecord header,
        Dictionary<uint, string>? editorIdMap)
    {
        uint baseFormId = 0;
        PositionSubrecord? position = null;
        var scale = 1.0f;
        float? radius = null;
        short? count = null;
        uint? ownerFormId = null;
        uint? encounterZoneFormId = null;
        byte? lockLevel = null;
        uint? lockKeyFormId = null;
        byte? lockFlags = null;
        uint? lockNumTries = null;
        uint? lockTimesUnlocked = null;
        uint? destinationDoorFormId = null;
        PositionSubrecord? teleportPosRot = null;
        byte? teleportFlags = null;
        uint? enableParentFormId = null;
        byte? enableParentFlags = null;
        uint? linkedRefKeywordFormId = null;
        uint? linkedRefFormId = null;
        var isMapMarker = false;
        ushort? markerType = null;
        string? markerName = null;
        string? editorId = null;
        List<PlacedReferenceStructuralSubrecord>? structuralSubrecords = null;

        // Iterate through subrecords using the standard subrecord header format
        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, header.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID" when sub.DataLength > 0:
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;

                case "NAME" when sub.DataLength == 4:
                    baseFormId = SubrecordSchemaReader.ReadNameFormId(subData, header.IsBigEndian) ?? 0;
                    break;

                case "DATA" when sub.DataLength == 24:
                    var pos = SubrecordSchemaReader.ReadDataPosition(subData, header.IsBigEndian);
                    if (pos.HasValue)
                    {
                        position = new PositionSubrecord(
                            pos.Value.x, pos.Value.y, pos.Value.z,
                            pos.Value.rotX, pos.Value.rotY, pos.Value.rotZ,
                            header.Offset + 24 + sub.DataOffset, header.IsBigEndian);
                    }

                    break;

                case "XSCL" when sub.DataLength == 4:
                    scale = SubrecordSchemaReader.ReadXsclScale(subData, header.IsBigEndian) ?? 1.0f;
                    break;

                case "XRDS" when sub.DataLength == 4:
                {
                    var parsedRadius = header.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    if (float.IsFinite(parsedRadius) && parsedRadius > 0)
                    {
                        radius = parsedRadius;
                    }

                    break;
                }

                case "XCNT" when sub.DataLength >= 4:
                    count = (short)(header.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData));
                    break;

                case "XOWN" when sub.DataLength == 4:
                    ownerFormId = SubrecordSchemaReader.ReadNameFormId(subData, header.IsBigEndian);
                    break;

                case "XEZN" when sub.DataLength == 4:
                    encounterZoneFormId = SubrecordSchemaReader.ReadNameFormId(subData, header.IsBigEndian);
                    break;

                case "XLOC" when sub.DataLength >= 20:
                    lockLevel = subData[0];
                    lockKeyFormId = SubrecordSchemaReader.ReadNameFormId(subData[4..8], header.IsBigEndian);
                    lockFlags = subData[8];
                    lockNumTries = header.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[12..16])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[12..16]);
                    lockTimesUnlocked = header.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[16..20])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[16..20]);
                    break;

                case "XTEL" when sub.DataLength >= 4: // Door teleport destination
                    destinationDoorFormId = header.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    // 32-byte XTEL also carries 6-float PosRot @4 + uint8 Flags @28.
                    // Mirrors EsmDataExtractor.ExtractRefrRecordsFromParsed.
                    if (sub.DataLength >= 28)
                    {
                        teleportPosRot = new PositionSubrecord(
                            ReadXtelFloat(subData, 4, header.IsBigEndian),
                            ReadXtelFloat(subData, 8, header.IsBigEndian),
                            ReadXtelFloat(subData, 12, header.IsBigEndian),
                            ReadXtelFloat(subData, 16, header.IsBigEndian),
                            ReadXtelFloat(subData, 20, header.IsBigEndian),
                            ReadXtelFloat(subData, 24, header.IsBigEndian),
                            header.Offset + 24 + sub.DataOffset, header.IsBigEndian);
                    }

                    if (sub.DataLength >= 29)
                    {
                        teleportFlags = subData[28];
                    }

                    break;

                case "XESP" when sub.DataLength >= 8: // Enable Parent
                    enableParentFormId = header.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    enableParentFlags = subData[4];
                    break;

                case "XLKR" when sub.DataLength >= 8: // Linked Reference (keyword + reference)
                    linkedRefKeywordFormId = header.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    linkedRefFormId = header.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..8])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..8]);
                    break;

                case "XLKR" when sub.DataLength == 4: // Linked Reference (reference only)
                    linkedRefFormId = header.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    break;

                case "XMRK": // Map marker presence flag (0 bytes)
                    isMapMarker = true;
                    break;

                case "TNAM" when sub.DataLength == 2: // Marker type
                    markerType = header.IsBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData)
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData);
                    break;

                case "FULL" when sub.DataLength > 0: // Marker name (null-terminated string)
                    var nameLength = sub.DataLength;
                    while (nameLength > 0 && subData[nameLength - 1] == 0)
                    {
                        nameLength--;
                    }

                    if (nameLength > 0)
                    {
                        markerName = Encoding.UTF8.GetString(subData[..nameLength]);
                    }

                    break;
            }

            if (StructuralReferenceSubrecords.Contains(sub.Signature))
            {
                structuralSubrecords ??= [];
                structuralSubrecords.Add(new PlacedReferenceStructuralSubrecord(
                    sub.Signature,
                    NormalizeStructuralSubrecord(subData, header.IsBigEndian)));
            }
        }

        if (baseFormId == 0)
        {
            return null;
        }

        return new ExtractedRefrRecord
        {
            Header = header,
            BaseFormId = baseFormId,
            Position = position,
            Scale = scale,
            Radius = radius,
            Count = count,
            OwnerFormId = ownerFormId,
            EncounterZoneFormId = encounterZoneFormId,
            LockLevel = lockLevel,
            LockKeyFormId = lockKeyFormId,
            LockFlags = lockFlags,
            LockNumTries = lockNumTries,
            LockTimesUnlocked = lockTimesUnlocked,
            DestinationDoorFormId = destinationDoorFormId,
            TeleportPosRot = teleportPosRot,
            TeleportFlags = teleportFlags,
            EnableParentFormId = enableParentFormId,
            EnableParentFlags = enableParentFlags,
            BaseEditorId = editorIdMap?.GetValueOrDefault(baseFormId),
            IsMapMarker = isMapMarker,
            MarkerType = markerType,
            MarkerName = markerName,
            EditorId = editorId,
            LinkedRefKeywordFormId = linkedRefKeywordFormId,
            LinkedRefFormId = linkedRefFormId,
            StructuralData = structuralSubrecords is { Count: > 0 }
                ? new PlacedReferenceStructuralData { Subrecords = structuralSubrecords }
                : null
        };
    }

    private static float ReadXtelFloat(ReadOnlySpan<byte> subData, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(subData.Slice(offset, 4))
            : BinaryPrimitives.ReadSingleLittleEndian(subData.Slice(offset, 4));
    }

    private static byte[] NormalizeStructuralSubrecord(
        ReadOnlySpan<byte> data,
        bool bigEndian)
    {
        var normalized = data.ToArray();
        if (!bigEndian || normalized.Length == 0)
        {
            return normalized;
        }

        // These structural marker payloads are float/FormID/uint32 based in all known FNV
        // schemas. Normalize 4-byte words to PC little-endian so encoders can byte-preserve.
        if (normalized.Length % 4 != 0)
        {
            return normalized;
        }

        for (var offset = 0; offset < normalized.Length; offset += 4)
        {
            Array.Reverse(normalized, offset, 4);
        }

        return normalized;
    }

    #endregion

    #region LAND Record Extraction

    /// <summary>
    ///     Extract full LAND records with heightmap data from detected main records.
    ///     Call this after ScanForRecordsMemoryMapped to get detailed terrain data.
    /// </summary>
    internal static void ExtractLandRecords(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult)
    {
        // Xbox 360 LAND records can be 20+ KB compressed (decompressed ~25 KB). The previous
        // fixed 16 KB rent silently truncated any LAND past that, dropping later subrecords —
        // commonly the NE quadrant's BTXT/ATXT layers, which manifest as voids in the rendered
        // terrain layer. Start at 64 KB and grow on demand for any outlier record.
        const int InitialBufferSize = 64 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);

        try
        {
            var landRecords = scanResult.MainRecords.Where(r => r.RecordType == "LAND").ToList();

            foreach (var header in landRecords)
            {
                // Read the record data (after 24-byte header)
                var dataStart = header.Offset + 24;
                var dataSize = (int)header.DataSize;

                if (dataStart + dataSize > fileSize)
                {
                    continue;
                }

                if (dataSize > buffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(dataSize);
                }

                accessor.ReadArray(dataStart, buffer, 0, dataSize);

                byte[] workBuffer;
                int workSize;

                if (header.IsCompressed && dataSize > 4)
                {
                    var decompressed = EsmParser.DecompressRecordData(buffer.AsSpan(0, dataSize), header.IsBigEndian);
                    if (decompressed == null)
                    {
                        continue;
                    }

                    workBuffer = decompressed;
                    workSize = decompressed.Length;
                }
                else
                {
                    workBuffer = buffer;
                    workSize = dataSize;
                }

                var land = ExtractLandFromBuffer(workBuffer, workSize, header);
                if (land != null)
                {
                    scanResult.LandRecords.Add(land);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Match LAND records to nearby parent CELL/XCLC metadata for cell coordinates and worldspace.
        // In ESM structure, CELL (containing XCLC) precedes its child LAND record by ~100-500 bytes.
        // DMP fragments are less orderly, so keep a bounded proximity search but preserve the exact
        // parent CELL FormID whenever it is recoverable. That prevents later worldspace/cell matching
        // from collapsing unrelated worldspaces that share the same grid coordinate.
        if (scanResult.LandRecords.Count > 0 &&
            (scanResult.CellGrids.Count > 0 || scanResult.MainRecords.Any(r => r.RecordType == "CELL") ||
             scanResult.LandToWorldspaceMap.Count > 0))
        {
            var sortedGrids = scanResult.CellGrids.OrderBy(g => g.Offset).ToList();
            var sortedCells = scanResult.MainRecords
                .Where(r => r.RecordType == "CELL")
                .OrderBy(r => r.Offset)
                .ToList();
            var enriched = new List<ExtractedLandRecord>();

            foreach (var land in scanResult.LandRecords)
            {
                var match = FindClosestGridBefore(land.Header.Offset, sortedGrids);
                var parentCell = FindClosestCellBefore(land.Header.Offset, sortedCells);
                var parentCellFormId = match?.CellFormId ?? parentCell?.FormId;

                uint? worldspaceFormId = null;
                if (scanResult.LandToWorldspaceMap.TryGetValue(land.Header.FormId, out var landWorldspace))
                {
                    worldspaceFormId = landWorldspace;
                }

                uint? parentCellWorldspace = null;
                if (parentCellFormId.HasValue &&
                    scanResult.CellToWorldspaceMap.TryGetValue(parentCellFormId.Value, out var cellWorldspace))
                {
                    parentCellWorldspace = cellWorldspace;
                }

                if (worldspaceFormId is null && parentCellWorldspace.HasValue)
                {
                    worldspaceFormId = parentCellWorldspace;
                }

                var parentConflictsWithLandWorldspace =
                    worldspaceFormId.HasValue &&
                    parentCellWorldspace.HasValue &&
                    worldspaceFormId.Value != parentCellWorldspace.Value;
                if (parentConflictsWithLandWorldspace)
                {
                    match = null;
                    parentCellFormId = null;
                }

                if (match != null || parentCellFormId.HasValue || worldspaceFormId.HasValue)
                {
                    enriched.Add(land with
                    {
                        CellX = match?.GridX ?? land.CellX,
                        CellY = match?.GridY ?? land.CellY,
                        ParentCellFormId = parentCellFormId ?? land.ParentCellFormId,
                        WorldspaceFormId = worldspaceFormId ?? land.WorldspaceFormId
                    });
                }
                else
                {
                    enriched.Add(land);
                }
            }

            scanResult.LandRecords.Clear();
            scanResult.LandRecords.AddRange(enriched);
        }
    }

    private static CellGridSubrecord? FindClosestGridBefore(
        long recordOffset,
        IReadOnlyList<CellGridSubrecord> sortedGrids)
    {
        CellGridSubrecord? match = null;
        foreach (var grid in sortedGrids)
        {
            var gap = recordOffset - grid.Offset;
            if (gap is > 0 and < 10_000)
            {
                match = grid;
            }
            else if (grid.Offset > recordOffset)
            {
                break;
            }
        }

        return match;
    }

    private static DetectedMainRecord? FindClosestCellBefore(
        long recordOffset,
        IReadOnlyList<DetectedMainRecord> sortedCells)
    {
        DetectedMainRecord? match = null;
        foreach (var cell in sortedCells)
        {
            var gap = recordOffset - cell.Offset;
            if (gap is > 0 and < 10_000)
            {
                match = cell;
            }
            else if (cell.Offset > recordOffset)
            {
                break;
            }
        }

        return match;
    }

    internal static ExtractedLandRecord? ExtractLandFromBuffer(byte[] data, int dataSize, DetectedMainRecord header)
    {
        var parsed = LandSubrecordParser.Parse(data, dataSize, header.IsBigEndian, header.Offset);

        if (parsed.Heightmap == null && parsed.VclrByteCount == 0 && parsed.VtexCount == 0 &&
            parsed.BtxtCount == 0 && parsed.AtxtCount == 0 && parsed.VtxtCount == 0)
        {
            return null;
        }

        var visualData = parsed.VisualData;
        var textureLayers = visualData?.TextureLayers ?? [];

        return new ExtractedLandRecord
        {
            Header = header,
            Heightmap = parsed.Heightmap,
            ParsedHeightmap = parsed.Heightmap,
            TextureLayers = textureLayers,
            VisualData = visualData is not null && (visualData.HasAny || visualData.UnattachedVtxtCount > 0)
                ? visualData
                : null,
            VclrByteCount = parsed.VclrByteCount,
            VtexCount = parsed.VtexCount,
            BtxtCount = parsed.BtxtCount,
            AtxtCount = parsed.AtxtCount,
            VtxtCount = parsed.VtxtCount,
            VtxtByteCount = parsed.VtxtByteCount
        };
    }

    #endregion

    #region Position Data

    internal static void TryAddPositionSubrecord(byte[] data, int i, int dataLength, List<PositionSubrecord> records)
    {
        TryAddPositionSubrecordWithOffset(data, i, dataLength, 0, records);
    }

    internal static void TryAddPositionSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<PositionSubrecord> records)
    {
        if (i + 30 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 24)
        {
            return;
        }

        var pos = TryParsePositionData(data, i + 6, false);
        if (pos != null)
        {
            records.Add(pos with { Offset = baseOffset + i });
            return;
        }

        pos = TryParsePositionData(data, i + 6, true);
        if (pos != null)
        {
            records.Add(pos with { Offset = baseOffset + i });
        }
    }

    private static PositionSubrecord? TryParsePositionData(byte[] data, int offset, bool isBigEndian)
    {
        float x, y, z, rotX, rotY, rotZ;

        if (isBigEndian)
        {
            x = BinaryUtils.ReadFloatBE(data, offset);
            y = BinaryUtils.ReadFloatBE(data, offset + 4);
            z = BinaryUtils.ReadFloatBE(data, offset + 8);
            rotX = BinaryUtils.ReadFloatBE(data, offset + 12);
            rotY = BinaryUtils.ReadFloatBE(data, offset + 16);
            rotZ = BinaryUtils.ReadFloatBE(data, offset + 20);
        }
        else
        {
            x = BinaryUtils.ReadFloatLE(data, offset);
            y = BinaryUtils.ReadFloatLE(data, offset + 4);
            z = BinaryUtils.ReadFloatLE(data, offset + 8);
            rotX = BinaryUtils.ReadFloatLE(data, offset + 12);
            rotY = BinaryUtils.ReadFloatLE(data, offset + 16);
            rotZ = BinaryUtils.ReadFloatLE(data, offset + 20);
        }

        // Validate: world coordinates typically -300K to +300K, rotation in radians ~-2pi to +2pi
        if (!IsValidCoord(x) || !IsValidCoord(y) || !IsValidCoord(z)
            || !IsValidRot(rotX) || !IsValidRot(rotY) || !IsValidRot(rotZ))
        {
            return null;
        }

        return new PositionSubrecord(x, y, z, rotX, rotY, rotZ, 0, isBigEndian);
    }

    private static bool IsValidCoord(float v)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= 500000f;
    }

    private static bool IsValidRot(float v)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= 10f;
    }

    #endregion

    #region Cell Grid (XCLC) and Heightmap (VHGT)

    internal static void TryAddVhgtHeightmapWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        bool isBigEndian, List<DetectedVhgtHeightmap> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = isBigEndian ? BinaryUtils.ReadUInt16BE(data, i + 4) : BinaryUtils.ReadUInt16LE(data, i + 4);

        // VHGT data = HeightOffset (4 bytes) + HeightDeltas (1089 sbytes) + padding (0-3 bytes)
        // Minimum valid size: 1093 (4 + 1089), maximum: 1096 (with 3 padding bytes)
        if (len < 1093 || len > 1096 || i + 6 + len > dataLength)
        {
            return;
        }

        var heightOffset = isBigEndian ? BinaryUtils.ReadFloatBE(data, i + 6) : BinaryUtils.ReadFloatLE(data, i + 6);
        if (float.IsNaN(heightOffset) || float.IsInfinity(heightOffset) || Math.Abs(heightOffset) > 100_000f)
        {
            return;
        }

        // Deltas start at i+10 (after 6-byte subrecord header + 4-byte HeightOffset)
        var deltaCount = Math.Min(1089, len - 4);
        var deltas = new sbyte[1089];
        for (var d = 0; d < deltaCount; d++)
        {
            deltas[d] = (sbyte)data[i + 10 + d];
        }

        records.Add(new DetectedVhgtHeightmap
        {
            HeightOffset = heightOffset,
            Offset = baseOffset + i,
            IsBigEndian = isBigEndian,
            HeightDeltas = deltas
        });
    }

    internal static void TryAddXclcSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        bool isBigEndian, List<CellGridSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = isBigEndian ? BinaryUtils.ReadUInt16BE(data, i + 4) : BinaryUtils.ReadUInt16LE(data, i + 4);

        // XCLC is 12 bytes (X: int32, Y: int32, flags: uint32)
        if (len != 12 || i + 6 + len > dataLength)
        {
            return;
        }

        var gridX = (int)(isBigEndian ? BinaryUtils.ReadUInt32BE(data, i + 6) : BinaryUtils.ReadUInt32LE(data, i + 6));
        var gridY = (int)(isBigEndian
            ? BinaryUtils.ReadUInt32BE(data, i + 10)
            : BinaryUtils.ReadUInt32LE(data, i + 10));

        // Validate grid coordinates (typical range is -100 to +100 for exterior cells)
        if (gridX is < -200 or > 200 || gridY is < -200 or > 200)
        {
            return;
        }

        records.Add(new CellGridSubrecord
        {
            GridX = gridX,
            GridY = gridY,
            LandFlags = 0,
            Offset = baseOffset + i,
            IsBigEndian = isBigEndian
        });
    }

    #endregion
}
