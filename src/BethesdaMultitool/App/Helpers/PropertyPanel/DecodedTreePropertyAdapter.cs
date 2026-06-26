using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;

namespace BethesdaMultitool;

/// <summary>
///     Adapts a schema-decoded <see cref="DecodedNode" /> tree (produced for games read by the
///     schema-driven parser — Oblivion today) into the <see cref="EsmPropertyEntry" /> rows the detail
///     panel renders. Member order is preserved (the schema's order is the meaningful one), struct/array
///     nodes become expandable sub-trees, and FormID nodes resolve to a name at display time via the
///     <see cref="FormIdResolver" /> so references render as clickable links.
/// </summary>
internal static class DecodedTreePropertyAdapter
{
    public static List<EsmPropertyEntry> Convert(
        GenericEsmRecord record, IReadOnlyList<DecodedNode> tree, FormIdResolver? resolver)
    {
        var entries = new List<EsmPropertyEntry>
        {
            new() { Name = "Record Type", Value = record.RecordType, Category = "Identity" },
            new()
            {
                Name = "Form ID",
                Value = $"0x{record.FormId:X8}",
                Category = "Identity",
                LinkedFormId = record.FormId == 0 ? null : record.FormId
            }
        };

        if (!string.IsNullOrEmpty(record.EditorId))
        {
            entries.Add(new EsmPropertyEntry { Name = "Editor ID", Value = record.EditorId, Category = "Identity" });
        }

        if (!string.IsNullOrEmpty(record.FullName))
        {
            entries.Add(new EsmPropertyEntry { Name = "Name", Value = record.FullName, Category = "Identity" });
        }

        foreach (var node in tree)
        {
            entries.Add(ConvertNode(node, resolver));
        }

        return entries;
    }

    private static EsmPropertyEntry ConvertNode(DecodedNode node, FormIdResolver? resolver)
    {
        if (node.Children.Count > 0)
        {
            return new EsmPropertyEntry
            {
                Name = node.Label,
                Value = node.Value ?? "",
                Category = "Fields",
                IsExpandable = true,
                SubItems = node.Children.Select(c => ConvertNode(c, resolver)).ToList()
            };
        }

        var value = node.Value ?? "";
        uint? linked = null;
        if (node.FormId is { } formId && formId != 0)
        {
            linked = formId;
            var name = resolver?.GetBestNameWithRefChain(formId);
            if (!string.IsNullOrEmpty(name))
            {
                value = $"{name} ({value})";
            }
        }

        return new EsmPropertyEntry
        {
            Name = node.Label,
            Value = value,
            Category = "Fields",
            LinkedFormId = linked
        };
    }
}
