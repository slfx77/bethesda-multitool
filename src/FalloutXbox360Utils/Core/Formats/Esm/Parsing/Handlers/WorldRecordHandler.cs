using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class WorldRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;
    private readonly CellRecordHandler _cellHandler = new(context);

    #region Map Markers

    /// <summary>
    ///     Extract map markers from REFR records that have the XMRK subrecord.
    /// </summary>
    internal List<PlacedReference> ExtractMapMarkers()
    {
        var markers = new List<PlacedReference>();

        // Map markers come from REFR records with XMRK subrecord
        foreach (var refr in _context.ScanResult.RefrRecords)
        {
            if (!refr.IsMapMarker)
            {
                continue;
            }

            var marker = new PlacedReference
            {
                FormId = refr.Header.FormId,
                BaseFormId = refr.BaseFormId,
                BaseEditorId = refr.BaseEditorId ?? _context.GetEditorId(refr.BaseFormId),
                RecordType = refr.Header.RecordType,
                X = refr.Position?.X ?? 0,
                Y = refr.Position?.Y ?? 0,
                Z = refr.Position?.Z ?? 0,
                RotX = refr.Position?.RotX ?? 0,
                RotY = refr.Position?.RotY ?? 0,
                RotZ = refr.Position?.RotZ ?? 0,
                Scale = refr.Scale,
                OwnerFormId = refr.OwnerFormId,
                EnableParentFormId = refr.EnableParentFormId,
                EnableParentFlags = refr.EnableParentFlags,
                IsPersistent = refr.Header.IsPersistent,
                IsInitiallyDisabled = refr.Header.IsInitiallyDisabled,
                IsMapMarker = true,
                MarkerType = refr.MarkerType.HasValue ? (MapMarkerType)refr.MarkerType.Value : null,
                MarkerName = refr.MarkerName,
                LinkedRefFormId = refr.LinkedRefFormId,
                Offset = refr.Header.Offset,
                IsBigEndian = refr.Header.IsBigEndian
            };

            markers.Add(marker);
        }

        return markers;
    }

    #endregion

    #region Enrichment

    /// <summary>
    ///     Enrich placed references in cells with base object bounds and model paths.
    ///     Joins PlacedReference.BaseFormId to pre-built indexes from parsed base objects.
    /// </summary>
    internal static void EnrichPlacedReferences(
        List<CellRecord> cells,
        Dictionary<uint, ObjectBounds> boundsIndex,
        Dictionary<uint, string> modelIndex)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (!cell.PlacedObjects.Any(obj =>
                    boundsIndex.ContainsKey(obj.BaseFormId) || modelIndex.ContainsKey(obj.BaseFormId)))
            {
                continue;
            }

            var enriched = cell.PlacedObjects.Select(obj =>
            {
                boundsIndex.TryGetValue(obj.BaseFormId, out var bounds);
                modelIndex.TryGetValue(obj.BaseFormId, out var modelPath);
                return bounds != null || modelPath != null
                    ? obj with { Bounds = bounds ?? obj.Bounds, ModelPath = modelPath ?? obj.ModelPath }
                    : obj;
            }).ToList();

            cells[i] = cell with { PlacedObjects = enriched };
        }
    }

    #endregion

    #region Cells

    /// <summary>
    ///     Parse all Cell records from the scan result.
    ///     Delegates to <see cref="CellRecordHandler"/>.
    /// </summary>
    internal List<CellRecord> ParseCells()
    {
        return _cellHandler.ParseCells();
    }

    /// <summary>
    ///     DMP fallback: infer worldspace membership for exterior cells.
    ///     Delegates to <see cref="CellLinkageHandler"/>.
    /// </summary>
    internal static void InferCellWorldspaces(List<CellRecord> cells, List<WorldspaceRecord> worldspaces)
    {
        CellLinkageHandler.InferCellWorldspaces(cells, worldspaces);
    }

    /// <summary>
    ///     Links parsed cells to their parent worldspace's Cells list.
    ///     Delegates to <see cref="CellLinkageHandler"/>.
    /// </summary>
    internal static void LinkCellsToWorldspaces(List<CellRecord> cells, List<WorldspaceRecord> worldspaces)
    {
        CellLinkageHandler.LinkCellsToWorldspaces(cells, worldspaces);
    }

    /// <summary>
    ///     DMP fallback: create virtual cells for orphan placed references.
    ///     Delegates to <see cref="CellLinkageHandler"/>.
    /// </summary>
    internal static List<CellRecord> CreateVirtualCells(
        List<CellRecord> existingCells,
        IReadOnlyList<ExtractedRefrRecord> allRefrs,
        RecordParserContext context)
    {
        return CellLinkageHandler.CreateVirtualCells(existingCells, allRefrs, context);
    }

    #endregion

    #region Worldspaces

    /// <summary>
    ///     Parse all Worldspace records from the scan result.
    /// </summary>
    internal List<WorldspaceRecord> ParseWorldspaces()
    {
        var worldspaces = new List<WorldspaceRecord>();
        var wrldRecords = _context.GetRecordsByType("WRLD").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in wrldRecords)
            {
                var worldspace = ParseWorldspaceFromScanResult(record);
                if (worldspace != null)
                {
                    worldspaces.Add(worldspace);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in wrldRecords)
                {
                    var worldspace = ParseWorldspaceFromAccessor(record, buffer);
                    if (worldspace != null)
                    {
                        worldspaces.Add(worldspace);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return worldspaces;
    }

    private WorldspaceRecord? ParseWorldspaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseWorldspaceFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        uint? parentWorldspace = null;
        uint? climate = null;
        uint? water = null;
        float? defaultLandHeight = null;
        float? defaultWaterHeight = null;
        int? mapUsableWidth = null;
        int? mapUsableHeight = null;
        short? mapNWCellX = null;
        short? mapNWCellY = null;
        short? mapSECellX = null;
        short? mapSECellY = null;
        float? boundsMinX = null;
        float? boundsMinY = null;
        float? boundsMaxX = null;
        float? boundsMaxY = null;
        uint? encounterZone = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "WNAM" when sub.DataLength == 4:
                    parentWorldspace = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNAM" when sub.DataLength == 4:
                    climate = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM2" when sub.DataLength == 4:
                    water = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "DNAM" when sub.DataLength >= 8:
                    defaultLandHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    defaultWaterHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
                case "MNAM" when sub.DataLength >= 16:
                    mapUsableWidth = (int)(record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData));
                    mapUsableHeight = (int)(record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]));
                    mapNWCellX = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[8..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[8..]);
                    mapNWCellY = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[10..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[10..]);
                    mapSECellX = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[12..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[12..]);
                    mapSECellY = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[14..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[14..]);
                    break;
                case "NAM0" when sub.DataLength >= 8:
                    boundsMinX = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    boundsMinY = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
                case "NAM9" when sub.DataLength >= 8:
                    boundsMaxX = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    boundsMaxY = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
                case "XEZN" when sub.DataLength == 4:
                    encounterZone = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new WorldspaceRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            ParentWorldspaceFormId = parentWorldspace,
            ClimateFormId = climate,
            WaterFormId = water,
            DefaultLandHeight = defaultLandHeight,
            DefaultWaterHeight = defaultWaterHeight,
            MapUsableWidth = mapUsableWidth,
            MapUsableHeight = mapUsableHeight,
            MapNWCellX = mapNWCellX,
            MapNWCellY = mapNWCellY,
            MapSECellX = mapSECellX,
            MapSECellY = mapSECellY,
            BoundsMinX = boundsMinX,
            BoundsMinY = boundsMinY,
            BoundsMaxX = boundsMaxX,
            BoundsMaxY = boundsMaxY,
            EncounterZoneFormId = encounterZone,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private WorldspaceRecord? ParseWorldspaceFromScanResult(DetectedMainRecord record)
    {
        return new WorldspaceRecord
        {
            FormId = record.FormId,
            EditorId = _context.GetEditorId(record.FormId),
            FullName = _context.FindFullNameNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
