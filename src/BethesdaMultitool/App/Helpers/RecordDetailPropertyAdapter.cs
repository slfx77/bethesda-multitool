using BethesdaMultitool.Core.Formats.Esm.Presentation;

namespace BethesdaMultitool;

/// <summary>Flattens a Core <see cref="RecordDetailModel"/> into the GUI's <see cref="EsmPropertyEntry"/> rows.</summary>
internal static class RecordDetailPropertyAdapter
{
    /// <summary>Converts a record-detail model's sections and entries into a flat property-entry list.</summary>
    internal static List<EsmPropertyEntry> Convert(RecordDetailModel model)
    {
        var properties = new List<EsmPropertyEntry>();

        foreach (var section in model.Sections)
        {
            foreach (var entry in section.Entries)
            {
                switch (entry.Kind)
                {
                    case RecordDetailEntryKind.List:
                        properties.Add(new EsmPropertyEntry
                        {
                            Name = entry.Label,
                            Value = "",
                            Category = section.Title,
                            IsExpandable = true,
                            IsExpandedByDefault = entry.ExpandByDefault,
                            SubItems = entry.Items?.Select(item => new EsmPropertyEntry
                            {
                                Name = item.Label,
                                Value = item.Value,
                                LinkedFormId = item.LinkedFormId
                            }).ToList()
                        });
                        break;

                    default:
                        properties.Add(new EsmPropertyEntry
                        {
                            Name = entry.Label,
                            Value = entry.Value ?? "",
                            Category = section.Title,
                            LinkedFormId = entry.LinkedFormId
                        });
                        break;
                }
            }
        }

        return properties;
    }
}
