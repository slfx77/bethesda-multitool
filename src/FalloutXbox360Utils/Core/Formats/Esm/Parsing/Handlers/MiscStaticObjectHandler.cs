using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class MiscStaticObjectHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    /// <summary>
    ///     Parse all Static (STAT) records.
    /// </summary>
    internal List<StaticRecord> ParseStatics()
    {
        var statics = new List<StaticRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("STAT"))
            {
                statics.Add(new StaticRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return statics;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("STAT"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    statics.Add(new StaticRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                string? modelPath = null;
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "OBND" when sub.DataLength == 12:
                            bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                    }
                }

                statics.Add(new StaticRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    ModelPath = modelPath,
                    Bounds = bounds,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _context.MergeRuntimeOverlayRecords(
            statics,
            [0x20],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeStatic(entry),
            MergeStatic,
            "statics");

        return statics;
    }

    /// <summary>
    ///     Parse all Furniture (FURN) records.
    /// </summary>
    internal List<FurnitureRecord> ParseFurniture()
    {
        var furniture = new List<FurnitureRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("FURN"))
            {
                furniture.Add(new FurnitureRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return furniture;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("FURN"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    furniture.Add(new FurnitureRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FindFullNameNear(record.Offset),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                string? fullName = null;
                string? modelPath = null;
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(subData);
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

                furniture.Add(new FurnitureRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    FullName = fullName,
                    ModelPath = modelPath,
                    Bounds = bounds,
                    Script = script != 0 ? script : null,
                    MarkerFlags = markerFlags,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _context.MergeRuntimeOverlayRecords(
            furniture,
            [0x27],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeFurniture(entry),
            MergeFurniture,
            "furniture");

        return furniture;
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
