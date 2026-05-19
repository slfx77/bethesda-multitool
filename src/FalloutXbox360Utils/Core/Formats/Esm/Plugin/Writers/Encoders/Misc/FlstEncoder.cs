using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a <see cref="FormListRecord" /> (FLST) as PC-format subrecord bytes.
///     Utility record containing an ordered list of FormIDs (used for leveled lists,
///     quest targets, faction relations, BNAM filters, etc.).
///     fopdoc canonical order: EDID, LNAM* (4-byte FormID each).
/// </summary>
public sealed class FlstEncoder : IRecordEncoder
{
    public string RecordType => "FLST";
    public Type ModelType => typeof(FormListRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(FormListRecord flst)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(flst.EditorId))
        {
            warnings.Add($"New FLST 0x{flst.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", flst.EditorId ?? string.Empty));

        foreach (var formId in flst.FormIds)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("LNAM", formId));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
