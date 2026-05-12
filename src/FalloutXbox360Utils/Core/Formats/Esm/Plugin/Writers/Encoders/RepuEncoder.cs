using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="ReputationRecord" /> (REPU) as PC-format subrecord bytes.
///     FNV-specific faction reputation threshold definitions.
///     fopdoc canonical order: EDID, FULL?, DATA(8B: float PositiveValue + float NegativeValue).
/// </summary>
public sealed class RepuEncoder : IRecordEncoder
{
    public string RecordType => "REPU";
    public Type ModelType => typeof(ReputationRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(ReputationRecord repu)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(repu.EditorId))
        {
            warnings.Add($"New REPU 0x{repu.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", repu.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(repu.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", repu.FullName));
        }

        var data = new byte[8];
        SubrecordEncoder.WriteFloat(data, 0, repu.PositiveValue);
        SubrecordEncoder.WriteFloat(data, 4, repu.NegativeValue);
        subs.Add(new EncodedSubrecord("DATA", data));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
