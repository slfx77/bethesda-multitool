using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a <see cref="CaravanDeckRecord" /> (CDCK) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, DATA(uint32 joker count), CARD* (one 4-byte FormID per card).
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class CdckEncoder : IRecordEncoder
{
    public string RecordType => "CDCK";
    public Type ModelType => typeof(CaravanDeckRecord);

    internal static EncodedRecord EncodeNew(CaravanDeckRecord cdck)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(cdck.EditorId))
        {
            warnings.Add($"New CDCK 0x{cdck.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", cdck.EditorId ?? string.Empty));
        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("DATA", cdck.JokerCount));

        foreach (var cardFormId in cdck.Cards)
        {
            if (cardFormId == 0)
            {
                continue;
            }
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("CARD", cardFormId));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
