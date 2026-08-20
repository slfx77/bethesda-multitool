using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Parses terrain- and texture-related ESM record types (texture sets, landscape textures,
///     grass) on behalf of <see cref="MiscEnvironmentHandler" />.
/// </summary>
internal sealed class TerrainTextureRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    private static float ReadFloat(ReadOnlySpan<byte> data, int offset, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[offset..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
    }

    #region Texture Sets

    /// <summary>
    ///     Parse all Texture Set (TXST) records.
    /// </summary>
    internal List<TextureSetRecord> ParseTextureSets()
    {
        return ParseRecordList("TXST", 2048,
            ParseTextureSetFromAccessor,
            record => new TextureSetRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private TextureSetRecord? ParseTextureSetFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new TextureSetRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        ObjectBounds? bounds = null;
        string? tx00 = null, tx01 = null, tx02 = null, tx03 = null, tx04 = null, tx05 = null;
        TxstDecalData? decalData = null;
        ushort txstFlags = 0;

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
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "TX00":
                    tx00 = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "TX01":
                    tx01 = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "TX02":
                    tx02 = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "TX03":
                    tx03 = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "TX04":
                    tx04 = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "TX05":
                    tx05 = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DODT" when sub.DataLength == 36:
                    decalData = ReadTxstDecalData(subData, record.IsBigEndian);
                    break;
                case "DNAM" when sub.DataLength >= 2:
                    txstFlags = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData)
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData);
                    break;
            }
        }

        return new TextureSetRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Bounds = bounds,
            DiffuseTexture = tx00,
            NormalTexture = tx01,
            EnvironmentTexture = tx02,
            GlowTexture = tx03,
            ParallaxTexture = tx04,
            EnvironmentMapTexture = tx05,
            DecalData = decalData,
            Flags = txstFlags,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static TxstDecalData ReadTxstDecalData(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        // Layout (36 bytes) per SubrecordCellAndMiscSchemas DODT registration:
        //   MinWidth(f32) MaxWidth(f32) MinHeight(f32) MaxHeight(f32)
        //   Depth(f32) Shininess(f32) ParallaxScale(f32)
        //   ParallaxPasses(u8) Flags(u8) Padding(2)
        //   Color(u32 ARGB)
        return new TxstDecalData
        {
            MinWidth = ReadFloat(data, 0, isBigEndian),
            MaxWidth = ReadFloat(data, 4, isBigEndian),
            MinHeight = ReadFloat(data, 8, isBigEndian),
            MaxHeight = ReadFloat(data, 12, isBigEndian),
            Depth = ReadFloat(data, 16, isBigEndian),
            Shininess = ReadFloat(data, 20, isBigEndian),
            ParallaxScale = ReadFloat(data, 24, isBigEndian),
            ParallaxPasses = data[28],
            Flags = data[29],
            ColorArgb = isBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data[32..])
                : BinaryPrimitives.ReadUInt32LittleEndian(data[32..])
        };
    }

    #endregion

    #region Material Swaps

    /// <summary>
    ///     Parse all Material Swap (MSWP) records — FO4/FO76 whole-<c>.bgsm</c> substitutions applied
    ///     per placement via the REFR <c>XMSP</c> FormID. Unconditional (no game gate): MSWP simply
    ///     doesn't exist in earlier games, so the signature-keyed record list is empty there.
    /// </summary>
    internal List<MaterialSwapRecord> ParseMaterialSwaps()
    {
        return ParseRecordList("MSWP", 2048,
            ParseMaterialSwapFromAccessor,
            record => new MaterialSwapRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private MaterialSwapRecord? ParseMaterialSwapFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new MaterialSwapRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? pendingOriginal = null;
        var swaps = new Dictionary<string, string>(StringComparer.Ordinal);

        // Subrecord shape: repeating BNAM (original material path) → SNAM (replacement path) pairs.
        // A leading FNAM ("Tree Folder" string) and CNAM may precede the pairs — neither affects the
        // swap table, so both are skipped. Paths are stored normalized so the decode-time lookup
        // (keyed on NifTexturePathUtility.Normalize of the NIF's material path) matches directly.
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
                case "BNAM":
                    // Starts a new pair; an unmatched previous BNAM (no SNAM) is dropped.
                    var original = EsmStringUtils.ReadNullTermString(subData);
                    pendingOriginal = string.IsNullOrEmpty(original) ? null : original;
                    break;
                case "SNAM" when pendingOriginal is not null:
                    var replacement = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(replacement))
                    {
                        // Indexer, not TryAdd: a later duplicate of the same BNAM wins (engine order).
                        swaps[MaterialSwapRecord.NormalizeMaterialPath(pendingOriginal)] =
                            MaterialSwapRecord.NormalizeMaterialPath(replacement);
                    }

                    pendingOriginal = null;
                    break;
            }
        }

        return new MaterialSwapRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Swaps = swaps,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Landscape Textures

    /// <summary>
    ///     Parse all Landscape Texture (LTEX) records used by LAND texture layers.
    /// </summary>
    internal List<LandscapeTextureRecord> ParseLandscapeTextures()
    {
        return ParseRecordList("LTEX", 2048,
            ParseLandscapeTextureFromAccessor,
            record => new LandscapeTextureRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private LandscapeTextureRecord? ParseLandscapeTextureFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new LandscapeTextureRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? iconPath = null;
        string? smallIconPath = null;
        uint? textureSetFormId = null;
        string? materialPath = null;
        byte[]? havokData = null;
        byte[]? specularData = null;
        var grassFormIds = new List<uint>();

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
                case "ICON":
                    iconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MICO":
                    smallIconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "TNAM" when sub.DataLength == 4:
                    textureSetFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                // Starfield: the landscape diffuse is named by a material path, not a TXST link.
                case "BNAM":
                    materialPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "HNAM" when sub.DataLength > 0:
                    havokData = subData.ToArray();
                    break;
                case "SNAM" when sub.DataLength > 0:
                    specularData = subData.ToArray();
                    break;
                case "GNAM" when sub.DataLength >= 4:
                    for (var i = 0; i + 4 <= sub.DataLength; i += 4)
                    {
                        grassFormIds.Add(RecordParserContext.ReadFormId(subData.Slice(i, 4), record.IsBigEndian));
                    }

                    break;
            }
        }

        return new LandscapeTextureRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            IconPath = iconPath,
            SmallIconPath = smallIconPath,
            TextureSetFormId = textureSetFormId is > 0 ? textureSetFormId : null,
            MaterialPath = string.IsNullOrEmpty(materialPath) ? null : materialPath,
            HavokData = havokData,
            SpecularData = specularData,
            GrassFormIds = grassFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Grass

    /// <summary>
    ///     Parse all Grass (GRAS) records. Referenced from LTEX via the GNAM subrecord:
    ///     each grass type is a small mesh the engine scatters across terrain painted with
    ///     a particular landscape texture.
    /// </summary>
    internal List<GrassRecord> ParseGrass()
    {
        return ParseRecordList("GRAS", 1024,
            ParseGrassFromAccessor,
            record => new GrassRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private GrassRecord? ParseGrassFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new GrassRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        ObjectBounds? bounds = null;
        string? modelPath = null;
        float? modelBound = null;
        byte[]? modelTextureData = null;
        GrassData? grassData = null;

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
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODB" when sub.DataLength == 4:
                    modelBound = ReadFloat(subData, 0, record.IsBigEndian);
                    break;
                case "MODT":
                    modelTextureData = subData.ToArray();
                    break;
                case "DATA" when sub.DataLength == 32:
                    grassData = ReadGrassData(subData, record.IsBigEndian);
                    break;
            }
        }

        return new GrassRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Bounds = bounds,
            ModelPath = modelPath,
            ModelBound = modelBound,
            ModelTextureData = modelTextureData,
            Data = grassData,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static GrassData ReadGrassData(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        // Layout (32 bytes) per SubrecordCellAndMiscSchemas DATA/GRAS registration:
        //   Density(u8) MinSlope(u8) MaxSlope(u8) Pad(1)
        //   UnitsFromWaterAmount(u16) Pad(2)
        //   UnitsFromWaterType(u32)
        //   PositionRange(f32) HeightRange(f32) ColorRange(f32) WavePeriod(f32)
        //   Flags(u8) Pad(3)
        return new GrassData
        {
            Density = data[0],
            MinSlope = data[1],
            MaxSlope = data[2],
            UnitsFromWaterAmount = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data[4..])
                : BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            UnitsFromWaterType = isBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data[8..])
                : BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            PositionRange = ReadFloat(data, 12, isBigEndian),
            HeightRange = ReadFloat(data, 16, isBigEndian),
            ColorRange = ReadFloat(data, 20, isBigEndian),
            WavePeriod = ReadFloat(data, 24, isBigEndian),
            Flags = data[28]
        };
    }

    #endregion
}
