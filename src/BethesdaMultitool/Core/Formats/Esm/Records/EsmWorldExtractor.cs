using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Subrecords;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Records;

/// <summary>
///     Extracts world-related ESM records (REFR, LAND) and subrecords (positions, heightmaps, cell grids)
///     from memory dumps and ESM files. Supports both PC (little-endian) and Xbox 360 (big-endian) formats.
/// </summary>
internal static class EsmWorldExtractor
{
    /// <summary>RADIO_RANGE_COUNT from the engine's RADIO_RANGE_TYPE enum — first invalid value.</summary>
    private const uint RadioRangeTypeCount = 5;

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
            // PGRE (placed grenade — mines) rides the same funnel: its DATA/NAME/X-subrecord
            // layout is REFR-shaped, and routing it here (rather than the display-only
            // PlacedGrenadeRecord parse, which keeps only 3 of DATA's 6 floats) is what feeds
            // captured mines into cell PlacedObjects for the planner cell pipeline.
            var refrRecords = scanResult.MainRecords
                .Where(r => r.RecordType is "REFR" or "ACHR" or "ACRE" or "PGRE")
                .ToList();

            var childGrups = BuildChildGrupIntervals(accessor, fileSize, scanResult);

            foreach (var header in refrRecords)
            {
                var dataStart = header.Offset + header.HeaderSize;
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
                    // Carved-GRUP parentage, PGRE-scoped: a mine inside a type-8/9/10 child
                    // GRUP labeled 0x00069EC2 IS a child of cell 0x00069EC2 — that is the file
                    // structure, not an inference. Without it every proto-only mine orphans
                    // into the unresolved virtual bucket, which dies in the planner. Kept
                    // PGRE-only deliberately: widening it to REFR/ACHR/ACRE would re-parent
                    // hundreds of today-orphaned refs (a data-policy change, not a bug fix).
                    if (header.RecordType == "PGRE"
                        && refr.ParentCellFormId is null
                        && TryFindEnclosingChildGrup(childGrups, header) is uint parentCellFormId)
                    {
                        refr = refr with { ParentCellFormId = parentCellFormId };
                    }

                    scanResult.RefrRecords.Add(refr);
                }
            }

            // PGRE telemetry (like PWAT's nifPlanes): captured mines are rare (13 across the
            // 50-dump corpus) and every earlier loss in this class was silent — say what came in.
            var pgreCount = scanResult.RefrRecords.Count(r => r.Header.RecordType == "PGRE");
            if (pgreCount > 0)
            {
                Logger.Instance.Info("[PGRE] Extracted {0} captured placed grenade(s): {1}",
                    pgreCount,
                    string.Join(", ", scanResult.RefrRecords
                        .Where(r => r.Header.RecordType == "PGRE")
                        .Select(r => $"0x{r.Header.FormId:X8}")));
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
        RadioData? radioData = null;
        short? count = null;
        uint? ownerFormId = null;
        uint? encounterZoneFormId = null;
        uint? materialSwapFormId = null;
        uint? emittanceFormId = null;
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
        uint? specialRenderingFlags = null;
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
                            header.Offset + header.HeaderSize + sub.DataOffset, header.IsBigEndian);
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
                    // Signed ExtraRadius adjustment; negative retail values are intentional.
                    if (float.IsFinite(parsedRadius))
                    {
                        radius = parsedRadius;
                    }

                    break;
                }

                // XRDO — radio broadcast config: Range, Type, StaticPercentage, PositionRef.
                // Parsed into the model rather than passed through structurally so the ESM and DMP
                // paths converge on one representation and the FormID at +12 gets remapped.
                case "XRDO" when sub.DataLength >= 16:
                {
                    var rangeType = header.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                    var range = header.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    var staticPct = header.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);

                    if (rangeType < RadioRangeTypeCount && float.IsFinite(range) && float.IsFinite(staticPct))
                    {
                        radioData = new RadioData
                        {
                            Radius = range,
                            RangeType = rangeType,
                            StaticPercentage = staticPct,
                            PositionRefFormId = SubrecordSchemaReader.ReadNameFormId(
                                subData[12..], header.IsBigEndian)
                        };
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

                case "XMSP" when sub.DataLength == 4: // FO4/FO76 per-placement material swap (MSWP)
                    materialSwapFormId = SubrecordSchemaReader.ReadNameFormId(subData, header.IsBigEndian);
                    break;

                case "XEMI" when sub.DataLength == 4:
                    emittanceFormId = SubrecordSchemaReader.ReadNameFormId(subData, header.IsBigEndian);
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
                            header.Offset + header.HeaderSize + sub.DataOffset, header.IsBigEndian);
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

                case "XSRF" when sub.DataLength >= 4: // Special Rendering Flags (0x2 = Imposter)
                    specialRenderingFlags = header.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
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
            RadioData = radioData,
            Count = count,
            OwnerFormId = ownerFormId,
            EncounterZoneFormId = encounterZoneFormId,
            MaterialSwapFormId = materialSwapFormId,
            EmittanceFormId = emittanceFormId,
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
            SpecialRenderingFlags = specialRenderingFlags,
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

    /// <summary>
    ///     Carved cell-children GRUP intervals. The scanner stores a GRUP header as a
    ///     <see cref="DetectedMainRecord" /> with groupType in <c>Flags</c> and the label
    ///     (parent cell FormID for types 8/9/10) in <c>FormId</c>, but deliberately zeroes
    ///     DataSize — so the true extent is re-read from the file here for containment checks.
    /// </summary>
    private static List<(long Start, long End, uint CellFormId)> BuildChildGrupIntervals(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult)
    {
        var intervals = new List<(long Start, long End, uint CellFormId)>();
        var sizeBytes = new byte[4];

        foreach (var record in scanResult.MainRecords)
        {
            if (record.RecordType != "GRUP"
                || record.Flags is not (8 or 9 or 10)
                || record.FormId == 0
                || record.Offset + 24 > fileSize)
            {
                continue;
            }

            accessor.ReadArray(record.Offset + 4, sizeBytes, 0, 4);
            var groupSize = record.IsBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(sizeBytes)
                : BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);

            // A child GRUP the size of a whole master is a mis-scan, not a container.
            if (groupSize is < 24 or > 64 * 1024 * 1024)
            {
                continue;
            }

            intervals.Add((record.Offset, record.Offset + groupSize, record.FormId));
        }

        intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return intervals;
    }

    /// <summary>
    ///     Enclosing child-GRUP lookup for one record header. Child GRUPs (types 8/9/10)
    ///     never nest inside each other, so the last interval starting before the record is
    ///     the only containment candidate; a record past its end has no carved parent.
    /// </summary>
    private static uint? TryFindEnclosingChildGrup(
        List<(long Start, long End, uint CellFormId)> intervals,
        DetectedMainRecord header)
    {
        if (intervals.Count == 0)
        {
            return null;
        }

        var lo = 0;
        var hi = intervals.Count - 1;
        var candidate = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (intervals[mid].Start < header.Offset)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (candidate < 0)
        {
            return null;
        }

        var (start, end, cellFormId) = intervals[candidate];
        var recordEnd = header.Offset + header.HeaderSize + header.DataSize;
        return header.Offset >= start + 24 && recordEnd <= end ? cellFormId : null;
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
        ExtractLandRecords(new MmfMemoryAccessor(accessor), fileSize, scanResult);
    }

    internal static void ExtractLandRecords(
        IMemoryAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult)
    {
        // Xbox 360 LAND records can be 20+ KB compressed (decompressed ~25 KB). The previous
        // fixed 16 KB rent silently truncated any LAND past that, dropping later subrecords —
        // commonly the NE quadrant's BTXT/ATXT layers, which manifest as voids in the rendered
        // terrain layer. Start at 64 KB and grow on demand for any outlier record.
        const int InitialBufferSize = 64 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);

        // Build into a LOCAL list and publish it to the shared scanResult in a SINGLE atomic reference
        // assignment at the very end. The GUI runs this extraction as an unawaited background task
        // concurrently with other readers of scanResult (the world-map build, the FormID index, the
        // renderer). Incrementally Add()-ing — and then Clear()+AddRange()-ing — the shared List let a
        // concurrent reader observe its internal array mid-resize, yielding an invalid object reference
        // → a fatal GC-time ExecutionEngineException (heap "corruption" with no catchable exception,
        // reproducing only at WastelandNV scale where the extraction window is long). With the atomic
        // swap, a reader sees either the prior list or the new complete one, never a torn array.
        var lands = new List<ExtractedLandRecord>();

        try
        {
            var landRecords = scanResult.MainRecords.Where(r => r.RecordType == "LAND").ToList();

            foreach (var header in landRecords)
            {
                // Read the record data (after 24-byte header)
                var dataStart = header.Offset + header.HeaderSize;
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
                    lands.Add(land);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Associate LAND records with their parent CELL (for cell coordinates and worldspace).
        // PREFERRED: the structural LAND->CELL map built from the GRUP hierarchy (LandToCellMap) —
        // exact and immune to record density. FALLBACK (memory dumps, which have no GRUP headers, so
        // the map is empty): a bounded offset-proximity search. Proximity is not enough for ESM
        // inputs: in dense ESM cells the LAND can sit >10 KB after its CELL (lots of placed-object
        // REFRs in between), past the proximity window, and the cell renders as a hole.
        if (lands.Count > 0 &&
            (scanResult.LandToCellMap.Count > 0 || scanResult.CellGrids.Count > 0 ||
             scanResult.MainRecords.Any(r => r.RecordType == "CELL") ||
             scanResult.LandToWorldspaceMap.Count > 0))
        {
            var sortedGrids = scanResult.CellGrids.OrderBy(g => g.Offset).ToList();
            var sortedCells = scanResult.MainRecords
                .Where(r => r.RecordType == "CELL")
                .OrderBy(r => r.Offset)
                .ToList();

            // CELL FormID -> its XCLC grid coords, for structural CellX/CellY backfill once the
            // structural parent is known. CellGrids carry a structural parent CELL FormID (read from
            // the owning CELL record header), so this lookup is exact.
            var gridByCellFormId = new Dictionary<uint, CellGridSubrecord>();
            foreach (var grid in scanResult.CellGrids)
            {
                if (grid.CellFormId is uint cellFormId)
                {
                    gridByCellFormId.TryAdd(cellFormId, grid);
                }
            }

            var enriched = new List<ExtractedLandRecord>(lands.Count);

            foreach (var land in lands)
            {
                CellGridSubrecord? match;
                uint? parentCellFormId;
                bool fromStructural;
                if (scanResult.LandToCellMap.TryGetValue(land.Header.FormId, out var structuralCellFormId))
                {
                    // Structural parentage: exact, and skips the per-LAND O(n) proximity scans.
                    fromStructural = true;
                    parentCellFormId = structuralCellFormId;
                    match = gridByCellFormId.GetValueOrDefault(structuralCellFormId);
                }
                else
                {
                    fromStructural = false;
                    match = FindClosestGridBefore(land.Header.Offset, sortedGrids);
                    var parentCell = FindClosestCellBefore(land.Header.Offset, sortedCells);
                    parentCellFormId = match?.CellFormId ?? parentCell?.FormId;
                }

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

                // Cross-worldspace guard only applies to the proximity fallback, where the parent can
                // be misattributed to a neighbor in another worldspace. A structural parent is
                // authoritative and must never be nullified.
                var parentConflictsWithLandWorldspace =
                    !fromStructural &&
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

            // Atomic publish: one reference write, so a concurrent reader never sees a torn list.
            scanResult.LandRecords = enriched;
        }
        else
        {
            // No enrichment performed — still publish the raw extracted list atomically (never
            // mutate the shared list in place while a background reader may be iterating it).
            scanResult.LandRecords = lands;
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
