using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

internal sealed class MiscWorldObjectHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    #region Activators

    /// <summary>
    ///     Parse all Activator (ACTI) records.
    /// </summary>
    internal List<ActivatorRecord> ParseActivators()
    {
        var activators = ParseRecordList("ACTI", 4096,
            ParseActivatorFromAccessor,
            record => new ActivatorRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeOverlayRecords(
            activators,
            [0x15],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeActivator(entry),
            MergeActivator,
            "activators");

        return activators;
    }

    private ActivatorRecord? ParseActivatorFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ActivatorRecord
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
        ObjectBounds? bounds = null;
        uint? script = null;
        uint? activationSound = null;
        uint? radioStation = null;
        uint? waterType = null;

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
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength == 4:
                    activationSound = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "RNAM" when sub.DataLength == 4:
                    radioStation = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "WNAM" when sub.DataLength == 4:
                    waterType = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new ActivatorRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Bounds = bounds,
            Script = script != 0 ? script : null,
            ActivationSoundFormId = activationSound != 0 ? activationSound : null,
            RadioStationFormId = radioStation != 0 ? radioStation : null,
            WaterTypeFormId = waterType != 0 ? waterType : null,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Lights

    /// <summary>
    ///     Parse all Light (LIGH) records.
    /// </summary>
    internal List<LightRecord> ParseLights()
    {
        var lights = ParseRecordList("LIGH", 2048,
            ParseLightFromAccessor,
            record => new LightRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeOverlayRecords(
            lights,
            [0x1E],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeLight(entry),
            MergeLight,
            "lights");

        return lights;
    }

    private LightRecord? ParseLightFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new LightRecord
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
        ObjectBounds? bounds = null;
        var duration = 0;
        uint radius = 0, color = 0, flags = 0;
        float falloffExponent = 0, fov = 0, weight = 0;
        var value = 0;

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
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 32:
                {
                    // LIGH DATA: Duration(int32) + Radius(uint32) + Color(RGBA uint32) +
                    // Flags(uint32) + FalloffExponent(float) + FOV(float) + Value(int32) + Weight(float)
                    if (record.IsBigEndian)
                    {
                        duration = BinaryPrimitives.ReadInt32BigEndian(subData);
                        radius = BinaryPrimitives.ReadUInt32BigEndian(subData[4..]);
                        color = BinaryPrimitives.ReadUInt32BigEndian(subData[8..]);
                        flags = BinaryPrimitives.ReadUInt32BigEndian(subData[12..]);
                        falloffExponent = BinaryPrimitives.ReadSingleBigEndian(subData[16..]);
                        fov = BinaryPrimitives.ReadSingleBigEndian(subData[20..]);
                        value = BinaryPrimitives.ReadInt32BigEndian(subData[24..]);
                        weight = BinaryPrimitives.ReadSingleBigEndian(subData[28..]);
                    }
                    else
                    {
                        duration = BinaryPrimitives.ReadInt32LittleEndian(subData);
                        radius = BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                        color = BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                        flags = BinaryPrimitives.ReadUInt32LittleEndian(subData[12..]);
                        falloffExponent = BinaryPrimitives.ReadSingleLittleEndian(subData[16..]);
                        fov = BinaryPrimitives.ReadSingleLittleEndian(subData[20..]);
                        value = BinaryPrimitives.ReadInt32LittleEndian(subData[24..]);
                        weight = BinaryPrimitives.ReadSingleLittleEndian(subData[28..]);
                    }

                    break;
                }
            }
        }

        return new LightRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Bounds = bounds,
            Duration = duration,
            Radius = radius,
            Color = color,
            Flags = flags,
            FalloffExponent = falloffExponent,
            FOV = fov,
            Value = value,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Doors

    /// <summary>
    ///     Parse all Door (DOOR) records.
    /// </summary>
    internal List<DoorRecord> ParseDoors()
    {
        var doors = ParseRecordList("DOOR", 2048,
            ParseDoorFromAccessor,
            record => new DoorRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeOverlayRecords(
            doors,
            [0x1C],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeDoor(entry),
            MergeDoor,
            "doors");

        return doors;
    }

    private DoorRecord? ParseDoorFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new DoorRecord
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
        ObjectBounds? bounds = null;
        uint? script = null;
        uint? openSound = null;
        uint? closeSound = null;
        uint? loopSound = null;
        byte flags = 0;

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
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength == 4:
                    openSound = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ANAM" when sub.DataLength == 4:
                    closeSound = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "BNAM" when sub.DataLength == 4:
                    loopSound = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "FNAM" when sub.DataLength == 1:
                    flags = subData[0];
                    break;
            }
        }

        return new DoorRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Bounds = bounds,
            Script = script != 0 ? script : null,
            OpenSoundFormId = openSound != 0 ? openSound : null,
            CloseSoundFormId = closeSound != 0 ? closeSound : null,
            LoopSoundFormId = loopSound != 0 ? loopSound : null,
            Flags = flags,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static ActivatorRecord MergeActivator(ActivatorRecord esm, ActivatorRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            FullName = esm.FullName ?? runtime.FullName,
            ModelPath = esm.ModelPath ?? runtime.ModelPath,
            Bounds = esm.Bounds ?? runtime.Bounds,
            Script = esm.Script ?? runtime.Script,
            ActivationSoundFormId = esm.ActivationSoundFormId ?? runtime.ActivationSoundFormId,
            RadioStationFormId = esm.RadioStationFormId ?? runtime.RadioStationFormId,
            WaterTypeFormId = esm.WaterTypeFormId ?? runtime.WaterTypeFormId,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }

    private static LightRecord MergeLight(LightRecord esm, LightRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            FullName = esm.FullName ?? runtime.FullName,
            ModelPath = esm.ModelPath ?? runtime.ModelPath,
            Bounds = esm.Bounds ?? runtime.Bounds,
            Duration = esm.Duration != 0 ? esm.Duration : runtime.Duration,
            Radius = esm.Radius != 0 ? esm.Radius : runtime.Radius,
            Color = esm.Color != 0 ? esm.Color : runtime.Color,
            Flags = esm.Flags != 0 ? esm.Flags : runtime.Flags,
            FalloffExponent = esm.FalloffExponent is not 0f ? esm.FalloffExponent : runtime.FalloffExponent,
            FOV = esm.FOV is not 0f ? esm.FOV : runtime.FOV,
            Value = esm.Value != 0 ? esm.Value : runtime.Value,
            Weight = esm.Weight is not 0f ? esm.Weight : runtime.Weight,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }

    private static DoorRecord MergeDoor(DoorRecord esm, DoorRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            FullName = esm.FullName ?? runtime.FullName,
            ModelPath = esm.ModelPath ?? runtime.ModelPath,
            Bounds = esm.Bounds ?? runtime.Bounds,
            Script = esm.Script ?? runtime.Script,
            OpenSoundFormId = esm.OpenSoundFormId ?? runtime.OpenSoundFormId,
            CloseSoundFormId = esm.CloseSoundFormId ?? runtime.CloseSoundFormId,
            LoopSoundFormId = esm.LoopSoundFormId ?? runtime.LoopSoundFormId,
            Flags = esm.Flags != 0 ? esm.Flags : runtime.Flags,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }

    #endregion
}
