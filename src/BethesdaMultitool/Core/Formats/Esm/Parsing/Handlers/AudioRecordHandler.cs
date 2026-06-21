using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Parses audio-related ESM record types (sounds, music types, audio location controllers)
///     on behalf of <see cref="MiscEnvironmentHandler" />.
/// </summary>
internal sealed class AudioRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
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
                        Context.ReadFullName(data.AsSpan(sub.DataOffset, sub.DataLength));
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
}
