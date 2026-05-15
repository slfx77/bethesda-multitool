using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="SoundRecord" /> (SOUN) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, OBND?, FNAM(string path), SNDD(36B).
///     SNDD layout (36B): uint8 MinAttenuationDistance(0) + uint8 MaxAttenuationDistance(1) +
///     int8 FreqAdjustment(2) + pad(3) + uint32 Flags(4) + int16 StaticAttenuation(8) +
///     uint8 EndTime(10) + uint8 StartTime(11) + 5×int16 Attenuation1-5(12-21) +
///     int16 ReverbAttenuation(22) + int32 Priority(24) + int32 LoopBegin(28) +
///     int32 LoopEnd(32). Model only covers a subset; the rest writes zero.
/// </summary>
public sealed class SounEncoder : IRecordEncoder
{
    public string RecordType => "SOUN";
    public Type ModelType => typeof(SoundRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(SoundRecord soun)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(soun.EditorId))
        {
            warnings.Add($"New SOUN 0x{soun.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", soun.EditorId ?? string.Empty));

        if (soun.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(soun.Bounds));
        }

        if (!string.IsNullOrEmpty(soun.FileName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FNAM", soun.FileName));
        }

        subs.Add(new EncodedSubrecord("SNDD", BuildSnddSubrecord(soun)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildSnddSubrecord(SoundRecord soun)
    {
        // The model widens MinAttenuationDistance and MaxAttenuationDistance to ushort but the
        // wire format is uint8 per PDB. Truncate via cast — values rarely exceed 255.
        var data = new byte[36];
        data[0] = (byte)soun.MinAttenuationDistance;
        data[1] = (byte)soun.MaxAttenuationDistance;
        data[2] = (byte)soun.RandomPercentChance; // FreqAdjustment slot per schema
        // byte 3 padding
        SubrecordEncoder.WriteUInt32(data, 4, soun.Flags);
        SubrecordEncoder.WriteInt16(data, 8, soun.StaticAttenuation);
        data[10] = soun.EndTime;
        data[11] = soun.StartTime;
        // bytes 12-23: Attenuation1-5 + ReverbAttenuation — not in model, leave zero.
        // bytes 24-35: Priority + LoopBegin + LoopEnd — not in model, leave zero.
        return data;
    }
}
