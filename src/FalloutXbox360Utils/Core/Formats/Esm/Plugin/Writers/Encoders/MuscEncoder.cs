using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="MusicTypeRecord" /> (MUSC) as PC-format subrecord bytes.
///     Music type with file path and attenuation.
///     fopdoc canonical order: EDID, FNAM(string path), ANAM(float attenuation in dB).
/// </summary>
public sealed class MuscEncoder : IRecordEncoder
{
    public string RecordType => "MUSC";
    public Type ModelType => typeof(MusicTypeRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(MusicTypeRecord musc)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(musc.EditorId))
        {
            warnings.Add($"New MUSC 0x{musc.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", musc.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(musc.FileName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FNAM", musc.FileName));
        }

        subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("ANAM", musc.Attenuation));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
