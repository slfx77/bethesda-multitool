using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Extracts world-related ESM records (REFR, LAND) and subrecords (positions, heightmaps, cell grids)
///     from memory dumps and ESM files. Supports both PC (little-endian) and Xbox 360 (big-endian) formats.
/// </summary>
internal static class EsmWorldExtractor
{
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
        var buffer = ArrayPool<byte>.Shared.Rent(1024); // REFR records are typically small

        try
        {
            var refrRecords = scanResult.MainRecords
                .Where(r => r.RecordType is "REFR" or "ACHR" or "ACRE")
                .ToList();

            foreach (var header in refrRecords)
            {
                var dataStart = header.Offset + 24;
                var dataSize = (int)Math.Min(header.DataSize, 1024);

                if (dataStart + dataSize > fileSize)
                {
                    continue;
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
        uint? enableParentFormId = null;
        byte? enableParentFlags = null;
        uint? linkedRefKeywordFormId = null;
        uint? linkedRefFormId = null;
        var isMapMarker = false;
        ushort? markerType = null;
        string? markerName = null;

        // Iterate through subrecords using the standard subrecord header format
        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, header.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
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
            EnableParentFormId = enableParentFormId,
            EnableParentFlags = enableParentFlags,
            BaseEditorId = editorIdMap?.GetValueOrDefault(baseFormId),
            IsMapMarker = isMapMarker,
            MarkerType = markerType,
            MarkerName = markerName,
            LinkedRefKeywordFormId = linkedRefKeywordFormId,
            LinkedRefFormId = linkedRefFormId
        };
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
        var buffer = ArrayPool<byte>.Shared.Rent(16384); // LAND records: compressed ~2-6KB, decompressed ~12KB

        try
        {
            var landRecords = scanResult.MainRecords.Where(r => r.RecordType == "LAND").ToList();

            foreach (var header in landRecords)
            {
                // Read the record data (after 24-byte header)
                var dataStart = header.Offset + 24;
                var dataSize = (int)Math.Min(header.DataSize, 16384);

                if (dataStart + dataSize > fileSize)
                {
                    continue;
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
                if (land?.Heightmap != null)
                {
                    scanResult.LandRecords.Add(land);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Match LAND records to nearby XCLC cell grids for cell coordinates.
        // In ESM structure, CELL (containing XCLC) precedes its child LAND record by ~100-150 bytes.
        if (scanResult.CellGrids.Count > 0 && scanResult.LandRecords.Count > 0)
        {
            var sortedGrids = scanResult.CellGrids.OrderBy(g => g.Offset).ToList();
            var enriched = new List<ExtractedLandRecord>();

            foreach (var land in scanResult.LandRecords)
            {
                // Find the XCLC that is closest before this LAND record.
                // In ESM structure, CELL (containing XCLC) precedes its child LAND by ~100-500 bytes.
                // In DMP files, 10KB is generous enough without matching unrelated cells.
                CellGridSubrecord? match = null;
                foreach (var grid in sortedGrids)
                {
                    var gap = land.Header.Offset - grid.Offset;
                    if (gap is > 0 and < 10_000)
                    {
                        match = grid;
                    }
                    else if (grid.Offset > land.Header.Offset)
                    {
                        break;
                    }
                }

                if (match != null)
                {
                    enriched.Add(land with { CellX = match.GridX, CellY = match.GridY });
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

    private static ExtractedLandRecord? ExtractLandFromBuffer(byte[] data, int dataSize, DetectedMainRecord header)
    {
        LandHeightmap? heightmap = null;
        var textureLayers = new List<LandTextureLayer>();

        // Iterate through subrecords using the standard subrecord header format
        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, header.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            if (sub.Signature == "VHGT")
            {
                // Use schema reader for VHGT heightmap
                var vhgt = SubrecordSchemaReader.ReadVhgtHeightmap(subData, header.IsBigEndian);
                if (vhgt.HasValue)
                {
                    heightmap = new LandHeightmap
                    {
                        HeightOffset = vhgt.Value.heightOffset,
                        HeightDeltas = vhgt.Value.deltas,
                        Offset = header.Offset + 24 + sub.DataOffset
                    };
                }
            }
            else if (sub.Signature is "ATXT" or "BTXT" && sub.DataLength >= 8)
            {
                // Use schema reader for texture layer FormID
                var textureFormId = SubrecordSchemaReader.ReadNameFormId(subData, header.IsBigEndian);
                if (textureFormId.HasValue)
                {
                    var quadrant = subData[4];
                    var layer = header.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[6..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[6..]);

                    textureLayers.Add(new LandTextureLayer(textureFormId.Value, quadrant, layer,
                        header.Offset + 24 + sub.DataOffset));
                }
            }
        }

        if (heightmap == null)
        {
            return null;
        }

        return new ExtractedLandRecord
        {
            Header = header,
            Heightmap = heightmap,
            TextureLayers = textureLayers
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
