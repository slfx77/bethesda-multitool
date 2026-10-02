using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

/// <summary>
///     Encodes a <see cref="MessageRecord" /> (MESG) as PC-format subrecord bytes.
///     In-game popup messages, tutorials, and notifications.
///     fopdoc canonical order:
///     EDID, FULL?, DESC?, ICON?, QNAM?(quest), DNAM?(flags u32), TNAM?(display time u32), (ITXT CTDA*)*(buttons).
/// </summary>
public sealed class MesgEncoder : IRecordEncoder
{
    public string RecordType => "MESG";
    public Type ModelType => typeof(MessageRecord);

    internal static EncodedRecord EncodeNew(MessageRecord mesg)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (mesg.UnassignedConditions.Count > 0 || mesg.ButtonConditions.Count > mesg.Buttons.Count)
        {
            warnings.Add($"New MESG 0x{mesg.FormId:X8} suppressed: conditions have no known button owner.");
            return new EncodedRecord { Subrecords = [], Warnings = warnings };
        }

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

        for (var i = 0; i < mesg.Buttons.Count; i++)
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ITXT", mesg.Buttons[i] ?? string.Empty));
            foreach (var condition in mesg.GetButtonConditions(i))
            {
                subs.Add(new EncodedSubrecord("CTDA", InfoEncoder.BuildCtdaSubrecord(condition)));
                if (condition.Parameter1String is not null)
                {
                    subs.Add(NewRecordSubrecords.EncodeStringSubrecord("CIS1", condition.Parameter1String));
                }

                if (condition.Parameter2String is not null)
                {
                    subs.Add(NewRecordSubrecords.EncodeStringSubrecord("CIS2", condition.Parameter2String));
                }
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
