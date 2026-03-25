using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class MiscEnvironmentHandler(RecordParserContext context) : RecordHandlerBase(context)
{

    #region Water

    /// <summary>
    ///     Parse all Water (WATR) records.
    /// </summary>
    internal List<WaterRecord> ParseWater()
    {
        return ParseRecordList("WATR", 4096,
            ParseWaterFromAccessor,
            record => new WaterRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
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
                    var fields = SubrecordDataReader.ReadFields("DNAM", "WATR", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        visualProps = fields;
                    }

                    break;
                }
                case "GNAM" when sub.DataLength == 12:
                {
                    var fields = SubrecordDataReader.ReadFields("GNAM", "WATR", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        relatedWater = fields;
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
                    var fields = SubrecordDataReader.ReadFields("SNAM", "WTHR", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        sounds.Add(new WeatherSound
                        {
                            SoundFormId = SubrecordDataReader.GetUInt32(fields, "Sound"),
                            Type = SubrecordDataReader.GetUInt32(fields, "Type")
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
                    var fields = SubrecordDataReader.ReadFields("SNDD", "SOUN", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        minAtten = SubrecordDataReader.GetByte(fields, "MinAttenuationDistance");
                        maxAtten = SubrecordDataReader.GetByte(fields, "MaxAttenuationDistance");
                        staticAtten = SubrecordDataReader.GetInt16(fields, "StaticAttenuation");
                        flags = SubrecordDataReader.GetUInt32(fields, "Flags");
                        startTime = SubrecordDataReader.GetByte(fields, "StartTime");
                        endTime = SubrecordDataReader.GetByte(fields, "EndTime");
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
            Flags = txstFlags,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
