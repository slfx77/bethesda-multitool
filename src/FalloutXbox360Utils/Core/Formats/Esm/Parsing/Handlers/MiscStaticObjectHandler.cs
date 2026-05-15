using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

internal sealed class MiscStaticObjectHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    /// <summary>
    ///     Parse all Static (STAT) records.
    /// </summary>
    internal List<StaticRecord> ParseStatics()
    {
        var statics = ParseRecordList("STAT", 2048,
            ParseStaticFromAccessor,
            record => new StaticRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeOverlayRecords(
            statics,
            [0x20],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeStatic(entry),
            MergeStatic,
            "statics");

        return statics;
    }

    private StaticRecord? ParseStaticFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new StaticRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? modelPath = null;
        byte[]? textureHashData = null;
        ObjectBounds? bounds = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODT" when sub.DataLength > 0:
                    textureHashData = subData.ToArray();
                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
            }
        }

        return new StaticRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ModelPath = modelPath,
            TextureHashData = textureHashData,
            Bounds = bounds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Parse all Furniture (FURN) records.
    /// </summary>
    internal List<FurnitureRecord> ParseFurniture()
    {
        var furniture = ParseRecordList("FURN", 2048,
            ParseFurnitureFromAccessor,
            record => new FurnitureRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeOverlayRecords(
            furniture,
            [0x27],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeFurniture(entry),
            MergeFurniture,
            "furniture");

        return furniture;
    }

    private FurnitureRecord? ParseFurnitureFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new FurnitureRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        byte[]? textureHashData = null;
        ObjectBounds? bounds = null;
        uint? script = null;
        uint markerFlags = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODT" when sub.DataLength > 0:
                    textureHashData = subData.ToArray();
                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "MNAM" when sub.DataLength == 4:
                    markerFlags = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    break;
            }
        }

        return new FurnitureRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            TextureHashData = textureHashData,
            Bounds = bounds,
            Script = script != 0 ? script : null,
            MarkerFlags = markerFlags,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Parse all Static Collection (SCOL) records. Each SCOL groups one or more STAT
    ///     bases under a single record; per base it carries a packed list of placements
    ///     (X/Y/Z/RotX/RotY/RotZ/Scale = 7 floats × 28 bytes each).
    /// </summary>
    internal List<StaticCollectionRecord> ParseStaticCollections()
    {
        return ParseRecordList("SCOL", 8192,
            ParseScolFromAccessor,
            record => new StaticCollectionRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private StaticCollectionRecord? ParseScolFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new StaticCollectionRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? modelPath = null;
        byte[]? textureHashData = null;
        ObjectBounds? bounds = null;
        var parts = new List<StaticCollectionPart>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODT" when sub.DataLength > 0:
                    textureHashData = subData.ToArray();
                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "ONAM" when sub.DataLength == 4:
                    var onamFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    parts.Add(new StaticCollectionPart { OnamFormId = onamFormId });
                    break;
                case "DATA" when sub.DataLength > 0 && sub.DataLength % 28 == 0 && parts.Count > 0:
                {
                    var placements = parts[^1].Placements;
                    var placementCount = sub.DataLength / 28;
                    for (var i = 0; i < placementCount; i++)
                    {
                        var slice = subData.Slice(i * 28, 28);
                        var floats = new float[7];
                        for (var f = 0; f < 7; f++)
                        {
                            var floatSlice = slice.Slice(f * 4, 4);
                            floats[f] = record.IsBigEndian
                                ? BinaryPrimitives.ReadSingleBigEndian(floatSlice)
                                : BinaryPrimitives.ReadSingleLittleEndian(floatSlice);
                        }

                        placements.Add(new StaticCollectionPlacement(
                            floats[0], floats[1], floats[2],
                            floats[3], floats[4], floats[5],
                            floats[6]));
                    }

                    break;
                }
                default:
                    Logger.Instance.Debug(
                        $"  [SCOL] Unexpected subrecord '{sub.Signature}' (len {sub.DataLength}) in 0x{record.FormId:X8} — dropped silently. Update ParseScolFromAccessor if this becomes common.");
                    break;
            }
        }

        return new StaticCollectionRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ModelPath = modelPath,
            TextureHashData = textureHashData,
            Bounds = bounds,
            Parts = parts,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static StaticRecord MergeStatic(StaticRecord esm, StaticRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            ModelPath = esm.ModelPath ?? runtime.ModelPath,
            Bounds = esm.Bounds ?? runtime.Bounds,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }

    private static FurnitureRecord MergeFurniture(FurnitureRecord esm, FurnitureRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            FullName = esm.FullName ?? runtime.FullName,
            ModelPath = esm.ModelPath ?? runtime.ModelPath,
            Bounds = esm.Bounds ?? runtime.Bounds,
            Script = esm.Script ?? runtime.Script,
            MarkerFlags = esm.MarkerFlags != 0 ? esm.MarkerFlags : runtime.MarkerFlags,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }
}
