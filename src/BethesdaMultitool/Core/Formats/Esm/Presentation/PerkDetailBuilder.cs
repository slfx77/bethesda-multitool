using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation;

/// <summary>Uses the report projection for the perk's GUI fields and ordered condition groups.</summary>
internal static class PerkDetailBuilder
{
    internal static RecordDetailModel Build(PerkRecord record, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{record.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", record.EditorId),
                RecordDetailHelpers.Scalar("Name", record.FullName),
                RecordDetailHelpers.Scalar("Offset", $"0x{record.Offset:X}"),
                RecordDetailHelpers.Scalar("Byte order", record.IsBigEndian ? "big-endian" : "little-endian")
            ]),
            RecordDetailHelpers.Section("Settings",
            [
                RecordDetailHelpers.Scalar("Description", record.Description),
                RecordDetailHelpers.Scalar("Ranks", record.Ranks.ToString(CultureInfo.InvariantCulture)),
                RecordDetailHelpers.Scalar("Minimum level", record.MinLevel.ToString(CultureInfo.InvariantCulture)),
                RecordDetailHelpers.Scalar("Playable", record.Playable.ToString(CultureInfo.InvariantCulture)),
                RecordDetailHelpers.Scalar("Trait", record.Trait.ToString(CultureInfo.InvariantCulture)),
                RecordDetailHelpers.Scalar("Hidden", record.Hidden?.ToString(CultureInfo.InvariantCulture)),
                RecordDetailHelpers.Scalar("Icon path", record.IconPath),
                RecordDetailHelpers.Scalar("Recovery", string.Join("; ", record.RuntimeRecoveryIssues))
            ])
        };
        for (var index = 0; index < record.Entries.Count; index++)
        {
            var entry = record.Entries[index];
            sections.Add(RecordDetailHelpers.Section($"Entry [{index}]",
                PerkEffectProjection.Fields(entry).Concat(PerkEffectProjection.Conditions(entry))
                    .Select(field => RecordDetailHelpers.Scalar(field.Key, field.Value))));
        }
        for (var index = 0; index < record.Conditions.Count; index++)
        {
            var condition = record.Conditions[index];
            List<RecordDetailEntry> fields =
            [
                RecordDetailHelpers.Scalar("Function", $"{condition.FunctionName} (#{condition.FunctionIndex})"),
                RecordDetailHelpers.Scalar("Parameter 1", condition.Parameter1Display ??
                    (condition.Parameter1FormId is { } form ? resolver.FormatWithEditorId(form) :
                        condition.Parameter1.ToString(CultureInfo.InvariantCulture))),
                RecordDetailHelpers.Scalar("Parameter 2", condition.Parameter2.ToString(CultureInfo.InvariantCulture)),
                RecordDetailHelpers.Scalar("Comparison", $"{condition.OperatorDisplay} {PerkEffectProjection.Comparison(condition)}")
            ];
            fields.AddRange(PerkEffectProjection.ConditionFields(condition)
                .Select(field => RecordDetailHelpers.Scalar(field.Key, field.Value)));
            sections.Add(RecordDetailHelpers.Section($"Condition [{index}]", fields));
        }
        return RecordDetailHelpers.Model("PERK", record.FormId, record.EditorId, record.FullName, sections);
    }
}
