using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class RecordParser
{
    #region Map Markers

    /// <summary>
    ///     Extract map markers from REFR records that have the XMRK subrecord.
    /// </summary>
    public List<PlacedReference> ExtractMapMarkers()
    {
        var markers = new List<PlacedReference>();

        // Map markers come from REFR records with XMRK subrecord
        foreach (var refr in _scanResult.RefrRecords)
        {
            if (!refr.IsMapMarker)
            {
                continue;
            }

            var marker = new PlacedReference
            {
                FormId = refr.Header.FormId,
                BaseFormId = refr.BaseFormId,
                BaseEditorId = refr.BaseEditorId ?? GetEditorId(refr.BaseFormId),
                RecordType = refr.Header.RecordType,
                X = refr.Position?.X ?? 0,
                Y = refr.Position?.Y ?? 0,
                Z = refr.Position?.Z ?? 0,
                RotX = refr.Position?.RotX ?? 0,
                RotY = refr.Position?.RotY ?? 0,
                RotZ = refr.Position?.RotZ ?? 0,
                Scale = refr.Scale,
                OwnerFormId = refr.OwnerFormId,
                IsMapMarker = true,
                MarkerType = refr.MarkerType.HasValue ? (MapMarkerType)refr.MarkerType.Value : null,
                MarkerName = refr.MarkerName,
                Offset = refr.Header.Offset,
                IsBigEndian = refr.Header.IsBigEndian
            };

            markers.Add(marker);
        }

        return markers;
    }

    #endregion

    #region Cells

    /// <summary>
    ///     Reconstruct all Cell records from the scan result.
    /// </summary>
    public List<CellRecord> ReconstructCells()
    {
        var cells = new List<CellRecord>();
        var cellRecords = GetRecordsByType("CELL").ToList();

        // Build a lookup of placed references by proximity to cells
        var refrRecords = _scanResult.RefrRecords;

        if (_accessor == null)
        {
            foreach (var record in cellRecords)
            {
                var cell = ReconstructCellFromScanResult(record, refrRecords);
                if (cell != null)
                {
                    cells.Add(cell);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in cellRecords)
                {
                    var cell = ReconstructCellFromAccessor(record, refrRecords, buffer);
                    if (cell != null)
                    {
                        cells.Add(cell);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return cells;
    }

    private CellRecord? ReconstructCellFromAccessor(DetectedMainRecord record,
        List<ExtractedRefrRecord> refrRecords, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructCellFromScanResult(record, refrRecords);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        int? gridX = null;
        int? gridY = null;
        byte flags = 0;

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
                case "DATA" when sub.DataLength >= 1:
                    flags = subData[0];
                    break;
                case "XCLC" when sub.DataLength >= 12:
                    {
                        var fields = SubrecordDataReader.ReadFields("XCLC", null, subData, record.IsBigEndian);
                        if (fields.Count > 0)
                        {
                            gridX = SubrecordDataReader.GetInt32(fields, "X");
                            gridY = SubrecordDataReader.GetInt32(fields, "Y");
                        }
                    }

                    break;
            }
        }

        // Find nearby REFRs
        var nearbyRefs = refrRecords
            .Where(r => r.Header.Offset > record.Offset && r.Header.Offset < record.Offset + 100000)
            .Take(100)
            .Select(r => new PlacedReference
            {
                FormId = r.Header.FormId,
                BaseFormId = r.BaseFormId,
                BaseEditorId = r.BaseEditorId ?? GetEditorId(r.BaseFormId),
                RecordType = r.Header.RecordType,
                X = r.Position?.X ?? 0,
                Y = r.Position?.Y ?? 0,
                Z = r.Position?.Z ?? 0,
                RotX = r.Position?.RotX ?? 0,
                RotY = r.Position?.RotY ?? 0,
                RotZ = r.Position?.RotZ ?? 0,
                Scale = r.Scale,
                OwnerFormId = r.OwnerFormId,
                Offset = r.Header.Offset,
                IsBigEndian = r.Header.IsBigEndian
            })
            .ToList();

        // Find associated heightmap
        var heightmap = _scanResult.LandRecords
            .FirstOrDefault(l => l.CellX == gridX && l.CellY == gridY)?.Heightmap;

        return new CellRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            GridX = gridX,
            GridY = gridY,
            Flags = flags,
            PlacedObjects = nearbyRefs,
            Heightmap = heightmap,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private CellRecord? ReconstructCellFromScanResult(DetectedMainRecord record,
        List<ExtractedRefrRecord> refrRecords)
    {
        // Find XCLC near this CELL record
        var cellGrid = _scanResult.CellGrids
            .FirstOrDefault(g => Math.Abs(g.Offset - record.Offset) < 200);

        // Find nearby REFRs
        var nearbyRefs = refrRecords
            .Where(r => r.Header.Offset > record.Offset && r.Header.Offset < record.Offset + 100000)
            .Take(100)
            .Select(r => new PlacedReference
            {
                FormId = r.Header.FormId,
                BaseFormId = r.BaseFormId,
                BaseEditorId = r.BaseEditorId ?? GetEditorId(r.BaseFormId),
                RecordType = r.Header.RecordType,
                X = r.Position?.X ?? 0,
                Y = r.Position?.Y ?? 0,
                Z = r.Position?.Z ?? 0,
                RotX = r.Position?.RotX ?? 0,
                RotY = r.Position?.RotY ?? 0,
                RotZ = r.Position?.RotZ ?? 0,
                Scale = r.Scale,
                OwnerFormId = r.OwnerFormId,
                Offset = r.Header.Offset,
                IsBigEndian = r.Header.IsBigEndian
            })
            .ToList();

        return new CellRecord
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            GridX = cellGrid?.GridX,
            GridY = cellGrid?.GridY,
            PlacedObjects = nearbyRefs,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Worldspaces

    /// <summary>
    ///     Reconstruct all Worldspace records from the scan result.
    /// </summary>
    public List<WorldspaceRecord> ReconstructWorldspaces()
    {
        var worldspaces = new List<WorldspaceRecord>();
        var wrldRecords = GetRecordsByType("WRLD").ToList();

        if (_accessor == null)
        {
            foreach (var record in wrldRecords)
            {
                var worldspace = ReconstructWorldspaceFromScanResult(record);
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
                    var worldspace = ReconstructWorldspaceFromAccessor(record, buffer);
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

    private WorldspaceRecord? ReconstructWorldspaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructWorldspaceFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        uint? parentWorldspace = null;
        uint? climate = null;
        uint? water = null;

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
                    parentWorldspace = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNAM" when sub.DataLength == 4:
                    climate = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM2" when sub.DataLength == 4:
                    water = ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new WorldspaceRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ParentWorldspaceFormId = parentWorldspace,
            ClimateFormId = climate,
            WaterFormId = water,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private WorldspaceRecord? ReconstructWorldspaceFromScanResult(DetectedMainRecord record)
    {
        return new WorldspaceRecord
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            FullName = FindFullNameNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
