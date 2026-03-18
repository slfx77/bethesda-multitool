using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class MiscEnvironmentHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region Water

    /// <summary>
    ///     Parse all Water (WATR) records.
    /// </summary>
    internal List<WaterRecord> ParseWater()
    {
        var water = new List<WaterRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("WATR"))
            {
                water.Add(new WaterRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return water;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType("WATR"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    water.Add(new WaterRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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

                water.Add(new WaterRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
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
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return water;
    }

    #endregion

    #region Weather

    /// <summary>
    ///     Parse all Weather (WTHR) records.
    /// </summary>
    internal List<WeatherRecord> ParseWeather()
    {
        var weather = new List<WeatherRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("WTHR"))
            {
                weather.Add(new WeatherRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return weather;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType("WTHR"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    weather.Add(new WeatherRecord
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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

                weather.Add(new WeatherRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    ImageSpaceModifier = imageSpaceMod != 0 ? imageSpaceMod : null,
                    Sounds = sounds,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return weather;
    }

    #endregion

    #region Sounds

    /// <summary>
    ///     Parse all Sound (SOUN) records.
    /// </summary>
    internal List<SoundRecord> ParseSounds()
    {
        var sounds = new List<SoundRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("SOUN"))
            {
                sounds.Add(new SoundRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return sounds;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("SOUN"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    sounds.Add(new SoundRecord
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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

                sounds.Add(new SoundRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
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
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _context.MergeRuntimeRecords(sounds, 0x0D, s => s.FormId,
            (reader, entry) => reader.ReadRuntimeSound(entry), "sounds");

        return sounds;
    }

    #endregion

    #region Texture Sets

    /// <summary>
    ///     Parse all Texture Set (TXST) records.
    /// </summary>
    internal List<TextureSetRecord> ParseTextureSets()
    {
        var textureSets = new List<TextureSetRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("TXST"))
            {
                textureSets.Add(new TextureSetRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return textureSets;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("TXST"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    textureSets.Add(new TextureSetRecord
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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

                textureSets.Add(new TextureSetRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
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
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return textureSets;
    }

    #endregion
}
