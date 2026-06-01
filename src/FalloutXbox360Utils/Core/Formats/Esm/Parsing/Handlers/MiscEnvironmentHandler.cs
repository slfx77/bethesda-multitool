using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

internal sealed class MiscEnvironmentHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    #region Water

    /// <summary>
    ///     Parse all Water (WATR) records.
    /// </summary>
    internal List<WaterRecord> ParseWater()
    {
        var water = ParseRecordList("WATR", 4096,
            ParseWaterFromAccessor,
            record => new WaterRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(water, 0x4E, w => w.FormId,
            (reader, entry) => reader.ReadRuntimeWater(entry), "water records");

        return water;
    }

    private WaterRecord? ParseWaterFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new WaterRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null, noiseTexture = null;
        byte opacity = 0;
        byte[]? waterFlags = null;
        uint? soundFormId = null;
        ushort damage = 0;
        Dictionary<string, object?>? visualProps = null;
        Dictionary<string, object?>? relatedWater = null;

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
                case "NNAM":
                    noiseTexture = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ANAM" when sub.DataLength >= 1:
                    opacity = subData[0];
                    break;
                case "FNAM":
                {
                    waterFlags = new byte[sub.DataLength];
                    subData.CopyTo(waterFlags);
                    break;
                }
                case "SNAM" when sub.DataLength == 4:
                    soundFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength == 2:
                    damage = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData)
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData);
                    break;
                case "DNAM" when sub.DataLength == 196:
                {
                    if (SubrecordSchemaView.TryRead("DNAM", "WATR", subData, record.IsBigEndian) is { } v)
                    {
                        visualProps = v.Raw;
                    }

                    break;
                }
                case "GNAM" when sub.DataLength == 12:
                {
                    if (SubrecordSchemaView.TryRead("GNAM", "WATR", subData, record.IsBigEndian) is { } v)
                    {
                        relatedWater = v.Raw;
                    }

                    break;
                }
            }
        }

        return new WaterRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            NoiseTexture = noiseTexture,
            Opacity = opacity,
            WaterFlags = waterFlags,
            SoundFormId = soundFormId != 0 ? soundFormId : null,
            Damage = damage,
            VisualProperties = visualProps,
            RelatedWater = relatedWater,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Weather

    /// <summary>
    ///     Parse all Weather (WTHR) records.
    /// </summary>
    internal List<WeatherRecord> ParseWeather()
    {
        return ParseRecordList("WTHR", 4096,
            ParseWeatherFromAccessor,
            record => new WeatherRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private WeatherRecord? ParseWeatherFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new WeatherRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        uint? imageSpaceMod = null;
        var sounds = new List<WeatherSound>();

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
                case "ONAM" when sub.DataLength == 4:
                    imageSpaceMod = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength == 8:
                {
                    if (SubrecordSchemaView.TryRead("SNAM", "WTHR", subData, record.IsBigEndian) is { } v)
                    {
                        sounds.Add(new WeatherSound
                        {
                            SoundFormId = v.UInt32("Sound"),
                            Type = v.UInt32("Type")
                        });
                    }

                    break;
                }
            }
        }

        return new WeatherRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ImageSpaceModifier = imageSpaceMod != 0 ? imageSpaceMod : null,
            Sounds = sounds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Sounds

    /// <summary>
    ///     Parse all Sound (SOUN) records.
    /// </summary>
    internal List<SoundRecord> ParseSounds()
    {
        var sounds = ParseRecordList("SOUN", 2048,
            ParseSoundFromAccessor,
            record => new SoundRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(sounds, 0x0D, s => s.FormId,
            (reader, entry) => reader.ReadRuntimeSound(entry), "sounds");

        return sounds;
    }

    private SoundRecord? ParseSoundFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new SoundRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fileName = null;
        ObjectBounds? bounds = null;
        ushort minAtten = 0, maxAtten = 0;
        short staticAtten = 0;
        uint flags = 0;
        byte startTime = 0, endTime = 0;

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
                case "FNAM":
                    fileName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "SNDD" when sub.DataLength >= 36:
                {
                    if (SubrecordSchemaView.TryRead("SNDD", "SOUN", subData, record.IsBigEndian) is { } v)
                    {
                        minAtten = v.Byte("MinAttenuationDistance");
                        maxAtten = v.Byte("MaxAttenuationDistance");
                        staticAtten = v.Int16("StaticAttenuation");
                        flags = v.UInt32("Flags");
                        startTime = v.Byte("StartTime");
                        endTime = v.Byte("EndTime");
                    }

                    break;
                }
            }
        }

        return new SoundRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Bounds = bounds,
            FileName = fileName,
            MinAttenuationDistance = minAtten,
            MaxAttenuationDistance = maxAtten,
            StaticAttenuation = staticAtten,
            Flags = flags,
            StartTime = startTime,
            EndTime = endTime,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Music Types

    /// <summary>
    ///     Parse all Music Type (MUSC) records.
    /// </summary>
    internal List<MusicTypeRecord> ParseMusicTypes()
    {
        var musicTypes = ParseRecordList("MUSC", 512,
            ParseMusicTypeFromAccessor,
            record => new MusicTypeRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(musicTypes, 0x66, m => m.FormId,
            (reader, entry) => reader.ReadRuntimeMusicType(entry), "music types");

        return musicTypes;
    }

    private MusicTypeRecord? ParseMusicTypeFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new MusicTypeRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fileName = null;
        float attenuation = 0;

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
                case "FNAM":
                    fileName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ANAM" when sub.DataLength >= 4:
                    attenuation = record.IsBigEndian
                        ? BinaryUtils.ReadFloatBE(subData)
                        : BitConverter.ToSingle(subData);
                    break;
            }
        }

        return new MusicTypeRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FileName = fileName,
            Attenuation = attenuation,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

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

    private static float ReadFloat(ReadOnlySpan<byte> data, int offset, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[offset..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
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

    #region Audio Location Controllers

    /// <summary>
    ///     Parse all Audio Location Controller (ALOC) records.
    /// </summary>
    internal List<AudioLocationControllerRecord> ParseAudioLocationControllers()
    {
        var controllers = ParseAccessorOnly("ALOC", 512, ParseAudioLocationControllerFromAccessor);

        Context.MergeRuntimeRecords(controllers, 0x70, c => c.FormId,
            (reader, entry) => reader.ReadRuntimeAudioLocationController(entry),
            "audio location controllers");

        return controllers;
    }

    private AudioLocationControllerRecord? ParseAudioLocationControllerFromAccessor(
        DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null;
        uint locationDelay = 0, layerTime = 0, loopTime = 0, mediaStartTime = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "NAM3" when sub.DataLength >= 4:
                    locationDelay = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "NAM4" when sub.DataLength >= 4:
                    layerTime = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "NAM5" when sub.DataLength >= 4:
                    loopTime = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "NAM6" when sub.DataLength >= 4:
                    mediaStartTime = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
            }
        }

        return new AudioLocationControllerRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            LocationDelay = locationDelay,
            LayerTime = layerTime,
            LoopTime = loopTime,
            MediaStartTime = mediaStartTime,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Load Screen Types

    /// <summary>
    ///     Parse all Load Screen Type (LSCT) records.
    /// </summary>
    internal List<LoadScreenTypeRecord> ParseLoadScreenTypes()
    {
        var types = ParseAccessorOnly("LSCT", 256, ParseLoadScreenTypeFromAccessor);

        Context.MergeRuntimeRecords(types, 0x6E, t => t.FormId,
            (reader, entry) => reader.ReadRuntimeLoadScreenType(entry), "load screen types");

        return types;
    }

    private LoadScreenTypeRecord? ParseLoadScreenTypeFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        Dictionary<string, object?>? layoutData = null;

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
                case "DATA" when sub.DataLength == 88:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "LSCT", subData, record.IsBigEndian) is { } v)
                    {
                        layoutData = v.Raw;
                    }

                    break;
                }
            }
        }

        return new LoadScreenTypeRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            LayoutData = layoutData,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
