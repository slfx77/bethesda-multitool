using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

/// <summary>
///     Encodes a <see cref="MessageRecord" /> (MESG) as PC-format subrecord bytes.
///     In-game popup messages, tutorials, and notifications.
///     fopdoc canonical order:
///     EDID, FULL?, DESC?, ICON?, QNAM?(quest), DNAM?(flags u32), TNAM?(display time u32), ITXT*(buttons).
///     Per-button CTDA conditions are not modeled yet.
/// </summary>
public sealed class MesgEncoder : IRecordEncoder
{
    public string RecordType => "MESG";
    public Type ModelType => typeof(MessageRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(MessageRecord mesg)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(mesg.EditorId))
        {
            warnings.Add($"New MESG 0x{mesg.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", mesg.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(mesg.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", mesg.FullName));
        }

        if (!string.IsNullOrEmpty(mesg.Description))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", mesg.Description));
        }

        if (!string.IsNullOrEmpty(mesg.Icon))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", mesg.Icon));
        }

        if (mesg.QuestFormId != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("QNAM", mesg.QuestFormId));
        }

        if (mesg.Flags != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("DNAM", mesg.Flags));
        }

        if (mesg.DisplayTime != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("TNAM", mesg.DisplayTime));
        }

        foreach (var buttonText in mesg.Buttons)
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ITXT", buttonText ?? string.Empty));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
