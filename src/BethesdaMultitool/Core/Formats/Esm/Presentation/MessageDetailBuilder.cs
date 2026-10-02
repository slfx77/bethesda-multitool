using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation;

/// <summary>Displays selected message values and independent runtime button evidence.</summary>
internal static class MessageDetailBuilder
{
    internal static RecordDetailModel Build(MessageRecord record, FormIdResolver resolver,
        ConditionDisplayContext context)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{record.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", record.EditorId),
                RecordDetailHelpers.Scalar("Title", record.FullName),
                RecordDetailHelpers.Scalar("Offset", $"0x{record.Offset:X}"),
                RecordDetailHelpers.Scalar("Byte order", record.IsBigEndian ? "big-endian" : "little-endian")
            ]),
            RecordDetailHelpers.Section("Selected values",
            [
                RecordDetailHelpers.Scalar("Description", record.Description),
                RecordDetailHelpers.Scalar("Description source", RuntimeMessageEvidenceFormatter.SourceToken(record.DescriptionSource)),
                RecordDetailHelpers.Scalar("Buttons source", RuntimeMessageEvidenceFormatter.SourceToken(record.ButtonSource)),
                RecordDetailHelpers.Scalar("Stored button count", record.StoredButtonCount?.ToString(CultureInfo.InvariantCulture)),
                RecordDetailHelpers.Scalar("Message box", record.IsMessageBox.ToString()),
                RecordDetailHelpers.Scalar("Auto display", record.IsAutoDisplay.ToString()),
                RecordDetailHelpers.Scalar("Display time", record.DisplayTime.ToString(CultureInfo.InvariantCulture)),
                RecordDetailHelpers.Scalar("Icon", record.Icon),
                RecordDetailHelpers.Link("Quest", record.QuestFormId, resolver)
            ])
        };
        if (record.ButtonSource == MessageFieldSource.RuntimeObject || record.RuntimeButtons is null)
            sections.Add(Evidence("Selected runtime evidence", record.RuntimeEvidence));
        if (record.UnassignedConditions.Count > 0)
            sections.Add(RecordDetailHelpers.Section("Unassigned conditions",
                [RecordDetailHelpers.Scalar("Ownership", "Before first button; unknown"),
                    RecordDetailHelpers.Scalar("Conditions", Conditions(record.UnassignedConditions))]));
        AddButtons("Selected button", record.Buttons, record.ButtonConditions, record.ButtonSource,
            record.ButtonSource == MessageFieldSource.RuntimeObject
                ? record.RuntimeButtons?.ObjectFileOffset ?? record.Offset : record.Offset,
            record.ButtonSource == MessageFieldSource.RuntimeObject ? record.RuntimeEvidence : null);
        foreach (var alternative in RuntimeMessageEvidenceFormatter.Alternatives(record))
        {
            var heading = $"Runtime alternative @ 0x{alternative.ObjectFileOffset:X}";
            sections.Add(Evidence(heading, alternative.Evidence));
            AddButtons(heading + " button", alternative.Buttons, alternative.ButtonConditions,
                MessageFieldSource.RuntimeObject, alternative.ObjectFileOffset, alternative.Evidence);
        }
        return RecordDetailHelpers.Model("MESG", record.FormId, record.EditorId, record.FullName, sections);

        string Conditions(IReadOnlyList<DialogueCondition> conditions) =>
            string.Join("\n", MessageConditionFormatter.Format(conditions, context));

        void AddButtons(string title, IReadOnlyList<string> buttons, IReadOnlyList<List<DialogueCondition>> conditions,
            MessageFieldSource source, long offset, RuntimeMessageEvidence? evidence)
        {
            for (var index = 0; index < buttons.Count; index++)
            {
                var button = evidence?.Buttons.ElementAtOrDefault(index);
                sections.Add(RecordDetailHelpers.Section($"{title} [{index}]",
                [
                    RecordDetailHelpers.Scalar("Text", buttons[index]),
                    RecordDetailHelpers.Scalar("Source", RuntimeMessageEvidenceFormatter.SourceToken(source)),
                    RecordDetailHelpers.Scalar("Source offset", $"0x{offset:X}"),
                    RecordDetailHelpers.Scalar("Conditions", Conditions(index < conditions.Count ? conditions[index] : [])),
                    RecordDetailHelpers.Scalar("Text status", button?.TextStatus),
                    RecordDetailHelpers.Scalar("Condition status", button?.ConditionStatus),
                    RecordDetailHelpers.Scalar("Item VA", button is null ? null : $"0x{button.ItemVirtualAddress:X8}"),
                    RecordDetailHelpers.Scalar("Text VA", button?.TextVirtualAddress is { } va ? $"0x{va:X8}" : null)
                ]));
            }
        }
    }

    private static RecordDetailSection Evidence(string title, RuntimeMessageEvidence? evidence) =>
        RecordDetailHelpers.Section(title,
            [RecordDetailHelpers.Scalar("Capture", string.Join("\n", RuntimeMessageEvidenceFormatter.Format(evidence)))]);
}
