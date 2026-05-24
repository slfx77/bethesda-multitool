using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

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
    // The model widens MinAttenuationDistance / MaxAttenuationDistance to ushort but the
    // wire format is uint8 per PDB; downstream serializer truncates via cast. Schema fields
    // we don't populate (Attenuation1..5, ReverbAttenuation, Priority, LoopBegin, LoopEnd)
    // are zero-filled — matches the prior encoder's behaviour of leaving those bytes at 0.
    private static readonly Dictionary<string, Func<SoundRecord, object?>> SnddExtractors = new(StringComparer.Ordinal)
    {
        ["MinAttenuationDistance"] = m => (byte)m.MinAttenuationDistance,
        ["MaxAttenuationDistance"] = m => (byte)m.MaxAttenuationDistance,
        ["FreqAdjustment"] = m => (sbyte)m.RandomPercentChance,
        ["Flags"] = m => m.Flags,
        ["StaticAttenuation"] = m => m.StaticAttenuation,
        ["EndTime"] = m => m.EndTime,
        ["StartTime"] = m => m.StartTime,
    };

    public string RecordType => "SOUN";
    public Type ModelType => typeof(SoundRecord);

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

        subs.Add(SchemaModelSerializer.SerializeSubrecord("SNDD", "SOUN", 36, soun, SnddExtractors));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
