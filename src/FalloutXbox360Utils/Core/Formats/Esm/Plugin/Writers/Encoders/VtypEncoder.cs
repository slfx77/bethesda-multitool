using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="VoiceTypeRecord" /> (VTYP) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, DNAM(1B flags). Bit 0 = allow default dialogue, bit 1 = female.
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class VtypEncoder : IRecordEncoder
{
    public string RecordType => "VTYP";
    public Type ModelType => typeof(VoiceTypeRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(VoiceTypeRecord vtyp)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(vtyp.EditorId))
        {
            warnings.Add($"New VTYP 0x{vtyp.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", vtyp.EditorId ?? string.Empty));
        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("DNAM", vtyp.Flags));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
