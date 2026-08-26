using System.Buffers.Binary;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

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
                    fullName = Context.ReadFullName(subData);
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
        var collections = ParseRecordList("SCOL", 8192,
            ParseScolFromAccessor,
            record => new StaticCollectionRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        // Runtime overlay (USER RULING 2026-08-05, playtest finding 3): dumps carry
        // BGSStaticCollection structs for SCOLs whose serialized records were never captured
        // (proto-only sidewalk collections in TheStripWorld — 133 runtime structs, 0 serialized
        // headers in xex21). Without this call those bases never emit and every ref to them
        // drops as refr.dangling-base. The runtime read recovers the baked model + bounds only;
        // the PDB layout has no part list (see RuntimeStaticCollectionReader).
        Context.MergeRuntimeOverlayRecords(
            collections,
            [0x21],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeStaticCollection(entry),
            MergeStaticCollection,
            "staticCollections");

        return collections;
    }

    /// <summary>
    ///     Parse all Placeable Water (PWAT) records — a water-plane placement bound to a
    ///     parent WATR type via DNAM, with per-placement reflection/refraction flags.
    /// </summary>
    internal List<PlaceableWaterRecord> ParsePlaceableWaters()
    {
        var waters = ParseRecordList("PWAT", 512,
            ParsePwatFromAccessor,
            record => new PlaceableWaterRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        // PWAT needs its own reader to recover the parent WATR: the reference lives inside an
        // 8-byte embedded struct, which the generic record path hex-dumps rather than walks.
        // RuntimePlaceableWaterReader follows it.
        Context.MergeRuntimeOverlayRecords(
            waters,
            [0x23],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimePlaceableWater(entry),
            MergePlaceableWater,
            "placeableWaters");

        return waters;
    }

    private static PlaceableWaterRecord MergePlaceableWater(
        PlaceableWaterRecord esm, PlaceableWaterRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            ModelPath = esm.ModelPath ?? runtime.ModelPath,
            Bounds = esm.Bounds ?? runtime.Bounds,
            // The serialized DNAM is authoritative when it was captured; the runtime copy
            // only backfills a water/flags pair the ESM side never supplied.
            WaterFormId = esm.WaterFormId != 0 ? esm.WaterFormId : runtime.WaterFormId,
            Flags = esm.WaterFormId != 0 ? esm.Flags : runtime.Flags,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }

    /// <summary>
    ///     Parse all Tree (TREE) records — SpeedTree seeds plus leaf-animation and billboard
    ///     parameters. The DMP corpus carries no serialized TREE bytes at all, so in practice
    ///     every recovered tree arrives through the runtime overlay below.
    /// </summary>
    internal List<TreeRecord> ParseTrees()
    {
        var trees = ParseRecordList("TREE", 512,
            ParseTreeFromAccessor,
            record => new TreeRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        // TREE needs its own reader to recover SNAM and CNAM: both live in embedded structs
        // larger than the 8-byte limit above which the generic record path emits a placeholder
        // string instead of walking. RuntimeTreeReader decodes them from the PDB-declared layout.
        Context.MergeRuntimeOverlayRecords(
            trees,
            [0x25],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeTree(entry),
            MergeTree,
            "trees");

        return trees;
    }

    private static TreeRecord MergeTree(TreeRecord esm, TreeRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            ModelPath = esm.ModelPath ?? runtime.ModelPath,
            IconPath = esm.IconPath ?? runtime.IconPath,
            Bounds = esm.Bounds ?? runtime.Bounds,
            ModelTextureData = esm.ModelTextureData ?? runtime.ModelTextureData,
            // Serialized values win when present; the runtime copy only backfills what the
            // ESM side never supplied. A captured seed list of length 0 is not "recovered".
            Seeds = esm.Seeds is { Count: > 0 } ? esm.Seeds : runtime.Seeds,
            Data = esm.Data ?? runtime.Data,
            BillboardSize = esm.BillboardSize ?? runtime.BillboardSize,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }

    private TreeRecord? ParseTreeFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new TreeRecord
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
        string? iconPath = null;
        byte[]? textureHashData = null;
        ObjectBounds? bounds = null;
        List<uint>? seeds = null;
        TreeData? treeData = null;
        TreeBillboardSize? billboard = null;

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
                case "ICON":
                    iconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                // SNAM is a packed uint32 seed array of any length (1 seed on 9 of the 10 proto
                // records, 5 on WhiteOak01). The exact-multiple guard rejects a truncated tail.
                case "SNAM" when sub.DataLength >= 4 && sub.DataLength % 4 == 0:
                    seeds = ReadTreeSeeds(subData, record.IsBigEndian);
                    break;
                // Exact-length guards, not >=: this handler is game-agnostic, and Skyrim's TREE
                // CNAM is a 48-byte struct with no BNAM at all. Decoding that as FNV's OBJ_TREE
                // would silently produce garbage rather than skipping.
                case "CNAM" when sub.DataLength == 32:
                    treeData = ReadTreeData(subData, record.IsBigEndian);
                    break;
                case "BNAM" when sub.DataLength == 8:
                    billboard = new TreeBillboardSize
                    {
                        Width = BinaryUtils.ReadFloat(subData, 0, record.IsBigEndian),
                        Height = BinaryUtils.ReadFloat(subData, 4, record.IsBigEndian)
                    };
                    break;
            }
        }

        return new TreeRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ModelPath = modelPath,
            ModelTextureData = textureHashData,
            IconPath = iconPath,
            Bounds = bounds,
            Seeds = seeds,
            Data = treeData,
            BillboardSize = billboard,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static List<uint> ReadTreeSeeds(ReadOnlySpan<byte> subData, bool bigEndian)
    {
        var seeds = new List<uint>(subData.Length / 4);
        for (var i = 0; i + 4 <= subData.Length; i += 4)
        {
            seeds.Add(bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(subData[i..])
                : BinaryPrimitives.ReadUInt32LittleEndian(subData[i..]));
        }

        return seeds;
    }

    /// <summary>
    ///     Decodes the 32-byte CNAM payload. Field 5 is a SIGNED INT32
    ///     (<c>OBJ_TREE.iCanopyShadowRadius</c>), not a float — see <see cref="TreeData" />.
    /// </summary>
    private static TreeData ReadTreeData(ReadOnlySpan<byte> d, bool bigEndian)
    {
        return new TreeData
        {
            LeafCurvature = BinaryUtils.ReadFloat(d, 0, bigEndian),
            MinLeafAngle = BinaryUtils.ReadFloat(d, 4, bigEndian),
            MaxLeafAngle = BinaryUtils.ReadFloat(d, 8, bigEndian),
            BranchDimmingValue = BinaryUtils.ReadFloat(d, 12, bigEndian),
            LeafDimmingValue = BinaryUtils.ReadFloat(d, 16, bigEndian),
            ShadowRadius = bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(d[20..])
                : BinaryPrimitives.ReadInt32LittleEndian(d[20..]),
            RockSpeed = BinaryUtils.ReadFloat(d, 24, bigEndian),
            RustleSpeed = BinaryUtils.ReadFloat(d, 28, bigEndian)
        };
    }

    private PlaceableWaterRecord? ParsePwatFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new PlaceableWaterRecord
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
        uint waterFormId = 0;
        uint flags = 0;

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
                // DNAM is { uint32 Flags, FormID Water } — the SAME order as the runtime
                // BGSPlaceableWaterData struct (flags at +0, TESWaterForm* at +4), per xEdit
                // wbRecord(PWAT) and all 29 retail PWATs.
                case "DNAM" when sub.DataLength == 8:
                    flags = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    waterFormId = RecordParserContext.ReadFormId(subData[4..], record.IsBigEndian);
                    break;
            }
        }

        return new PlaceableWaterRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ModelPath = modelPath,
            ModelTextureData = textureHashData,
            Bounds = bounds,
            WaterFormId = waterFormId,
            Flags = flags,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static StaticCollectionRecord MergeStaticCollection(
        StaticCollectionRecord esm, StaticCollectionRecord runtime)
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

    /// <summary>
    ///     Fallout 4/76 SCOL fields this reader knowingly does not model — presentation, snapping,
    ///     LOD and layer metadata that no consumer of <see cref="StaticCollectionRecord" /> reads.
    ///     They are skipped without a log line so the "unexpected subrecord" warning keeps meaning
    ///     "we have never seen this"; before this set existed, one Fallout 76 load buried the log
    ///     under 4,492 lines of them.
    /// </summary>
    private static readonly HashSet<string> KnownUnmodelledScolSubrecords =
    [
        "MODB", "MODS", "MODD", // model variant/alt-texture blocks (the merged NIF is in MODL)
        "FULL", "MNAM", "FLTR", // display name, workshop menu, editor filter string
        "PTRN", "SNTP", "PHST", "DEFL", "XALG", "OPDS", "PRPS", // transform/snap/physics/layer/props
        "NAM1", "LODP", // LOD selection
        "ENLM", "ENLT", "ENLS", "AUUV" // Fallout 76 lighting/UV metadata
    ];

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
                // Fallout 3/New Vegas pack one FormID here; Fallout 76 packs two — the base object
                // followed by an optional override (zero on ~75% of parts). Accepting only 4 dropped
                // every part of all 17,384 SeventySix.esm collections, and because a DATA block is
                // only attached to the part its ONAM just opened, all 119,958 placement blocks fell
                // with them: the whole collection graph parsed empty.
                case "ONAM" when sub.DataLength is 4 or 8:
                    var onamFormId = RecordParserContext.ReadFormId(subData[..4], record.IsBigEndian);
                    uint? secondaryFormId = null;
                    if (sub.DataLength == 8)
                    {
                        var secondary = RecordParserContext.ReadFormId(subData[4..8], record.IsBigEndian);
                        secondaryFormId = secondary != 0 ? secondary : null;
                    }

                    parts.Add(new StaticCollectionPart
                    {
                        OnamFormId = onamFormId,
                        SecondaryFormId = secondaryFormId
                    });
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
                    if (!KnownUnmodelledScolSubrecords.Contains(sub.Signature))
                    {
                        Logger.Instance.Debug(
                            $"  [SCOL] Unexpected subrecord '{sub.Signature}' (len {sub.DataLength}) in 0x{record.FormId:X8} — dropped silently. Update ParseScolFromAccessor if this becomes common.");
                    }

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
