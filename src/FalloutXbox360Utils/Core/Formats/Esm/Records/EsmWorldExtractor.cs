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
            var refrRecords = scanResult.MainRecords.Where(r => r.RecordType == "REFR").ToList();

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
        uint? ownerFormId = null;
        uint? destinationDoorFormId = null;
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

                case "XOWN" when sub.DataLength == 4:
                    ownerFormId = SubrecordSchemaReader.ReadNameFormId(subData, header.IsBigEndian);
                    break;

                case "XTEL" when sub.DataLength >= 4: // Door teleport destination
                    destinationDoorFormId = header.IsBigEndian
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
            OwnerFormId = ownerFormId,
            DestinationDoorFormId = destinationDoorFormId,
            BaseEditorId = editorIdMap?.GetValueOrDefault(baseFormId),
            IsMapMarker = isMapMarker,
            MarkerType = markerType,
            MarkerName = markerName
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
                // Find the XCLC that is closest before this LAND record (within 500 bytes)
                CellGridSubrecord? match = null;
                foreach (var grid in sortedGrids)
                {
                    var gap = land.Header.Offset - grid.Offset;
                    if (gap is > 0 and < 100_000)
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
        if (i + 30 > dataLength) // 4 sig + 2 len + 24 data
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 24) // Position data is exactly 24 bytes (6 floats)
        {
            return;
        }

        // Try little-endian first
        var pos = TryParsePositionData(data, i + 6, false);
        if (pos != null)
        {
            records.Add(pos with { Offset = i });
            return;
        }

        // Try big-endian
        pos = TryParsePositionData(data, i + 6, true);
        if (pos != null)
        {
            records.Add(pos with { Offset = i });
        }
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

        // Validate position values are reasonable for Fallout NV world
        // World coordinates typically range from -200000 to +200000
        // Rotation values are in radians, typically -2pi to +2pi
        if (!IsValidPosition(x, y, z) || !IsValidRotation(rotX, rotY, rotZ))
        {
            return null;
        }

        return new PositionSubrecord(x, y, z, rotX, rotY, rotZ, 0, isBigEndian);
    }

    private static bool IsValidPosition(float x, float y, float z)
    {
        // Fallout NV world coordinates are typically in range -300000 to +300000
        const float maxCoord = 500000f;

        if (float.IsNaN(x) || float.IsInfinity(x) || Math.Abs(x) > maxCoord)
        {
            return false;
        }

        if (float.IsNaN(y) || float.IsInfinity(y) || Math.Abs(y) > maxCoord)
        {
            return false;
        }

        if (float.IsNaN(z) || float.IsInfinity(z) || Math.Abs(z) > maxCoord)
        {
            return false;
        }

        return true;
    }

    private static bool IsValidRotation(float rotX, float rotY, float rotZ)
    {
        // Rotation values in radians, typically -2pi to +2pi, but allow some margin
        const float maxRot = 10f;

        if (float.IsNaN(rotX) || float.IsInfinity(rotX) || Math.Abs(rotX) > maxRot)
        {
            return false;
        }

        if (float.IsNaN(rotY) || float.IsInfinity(rotY) || Math.Abs(rotY) > maxRot)
        {
            return false;
        }

        if (float.IsNaN(rotZ) || float.IsInfinity(rotZ) || Math.Abs(rotZ) > maxRot)
        {
            return false;
        }

        return true;
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

        var len = isBigEndian
            ? BinaryUtils.ReadUInt16BE(data, i + 4)
            : BinaryUtils.ReadUInt16LE(data, i + 4);

        // VHGT is 1089 bytes (4 byte offset + 33x33 height deltas + 3 padding)
        if (len != 1089 || i + 6 + len > dataLength)
        {
            return;
        }

        var heightOffset = isBigEndian
            ? BinaryUtils.ReadFloatBE(data, i + 6)
            : BinaryUtils.ReadFloatLE(data, i + 6);

        if (float.IsNaN(heightOffset) || float.IsInfinity(heightOffset))
        {
            return;
        }

        records.Add(new DetectedVhgtHeightmap
        {
            HeightOffset = heightOffset,
            Offset = baseOffset + i,
            IsBigEndian = isBigEndian,
            HeightDeltas = data.Skip(i + 10).Take(1089).Select(b => (sbyte)b).ToArray()
        });
    }

    internal static void TryAddXclcSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        bool isBigEndian, List<CellGridSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var len = isBigEndian
            ? BinaryUtils.ReadUInt16BE(data, i + 4)
            : BinaryUtils.ReadUInt16LE(data, i + 4);

        // XCLC is 12 bytes (X: int32, Y: int32, flags: uint32)
        if (len != 12 || i + 6 + len > dataLength)
        {
            return;
        }

        int gridX, gridY;
        if (isBigEndian)
        {
            gridX = (int)BinaryUtils.ReadUInt32BE(data, i + 6);
            gridY = (int)BinaryUtils.ReadUInt32BE(data, i + 10);
        }
        else
        {
            gridX = (int)BinaryUtils.ReadUInt32LE(data, i + 6);
            gridY = (int)BinaryUtils.ReadUInt32LE(data, i + 10);
        }

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

    #region Land Record Enrichment

    /// <summary>
    ///     Enrich LAND records with runtime data from TESForm pointers.
    /// </summary>
    internal static void EnrichLandRecordsWithRuntimeData(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? editorIdMap)
    {
        // This method can be extended to read additional runtime data
        // from TESForm pointers associated with LAND records
        _ = accessor;
        _ = fileSize;
        _ = scanResult;
        _ = editorIdMap;
    }

    /// <summary>
    ///     Enrich LAND records with runtime data loaded from memory.
    /// </summary>
    internal static void EnrichLandRecordsWithRuntimeData(
        EsmRecordScanResult scanResult,
        Dictionary<uint, RuntimeLoadedLandData> runtimeLandData)
    {
        // Match runtime land data with detected LAND records by FormId
        // ExtractedLandRecord is immutable, so we replace entries with updated versions
        for (var i = 0; i < scanResult.LandRecords.Count; i++)
        {
            var landRecord = scanResult.LandRecords[i];
            if (runtimeLandData.TryGetValue(landRecord.Header.FormId, out var runtimeData))
            {
                // Create new record with runtime cell coordinates
                scanResult.LandRecords[i] = landRecord with
                {
                    RuntimeCellX = runtimeData.CellX,
                    RuntimeCellY = runtimeData.CellY
                };
            }
        }
    }

    #endregion
}
