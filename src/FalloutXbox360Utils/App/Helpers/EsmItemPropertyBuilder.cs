using System.Collections;
using System.Reflection;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds property entries for item-related records and list items, including
///     factions, inventory, leveled lists, placed references, and generic list entries.
/// </summary>
internal static class EsmItemPropertyBuilder
{
    /// <summary>
    ///     Processes multi-line script text (SourceText, DecompiledText) into an expandable property.
    /// </summary>
    internal static void AddScriptText(
        List<EsmPropertyEntry> properties,
        string propertyName,
        string displayName,
        string textContent,
        ScriptRecord? scriptRecord)
    {
        var lines = textContent.Split('\n');
        var subItems = new List<EsmPropertyEntry>
        {
            new() { Name = "", Value = textContent }
        };

        // Expand SourceText by default; fall back to DecompiledText when no source
        var expandByDefault = scriptRecord != null &&
                              (propertyName == "SourceText" ||
                               (propertyName == "DecompiledText" && !scriptRecord.HasSource));

        properties.Add(new EsmPropertyEntry
        {
            Name = displayName,
            Value = $"{lines.Length} lines",
            Category = "General",
            IsExpandable = true,
            IsExpandedByDefault = expandByDefault,
            SubItems = subItems
        });
    }

    /// <summary>
    ///     Processes compiled bytecode (SCDA) into an expandable hex panel property.
    /// </summary>
    internal static void AddCompiledData(
        List<EsmPropertyEntry> properties,
        string displayName,
        string propertyName,
        byte[] compiledBytes)
    {
        var hexLines = new List<string>();
        for (var i = 0; i < compiledBytes.Length; i += 16)
        {
            var lineBytes = compiledBytes.Skip(i).Take(16);
            hexLines.Add(string.Join(" ", lineBytes.Select(b => b.ToString("X2"))));
        }

        var hexBlock = string.Join("\n", hexLines);
        var subItems = new List<EsmPropertyEntry>
        {
            new() { Name = "", Value = hexBlock }
        };

        properties.Add(new EsmPropertyEntry
        {
            Name = displayName,
            Value = $"{compiledBytes.Length} bytes",
            Category = EsmPropertyFormatter.CategorizeProperty(propertyName),
            IsExpandable = true,
            SubItems = subItems
        });
    }

    /// <summary>
    ///     Processes a recipe's Required Skill (Actor Value code) into a property entry.
    ///     Uses AVIF-sourced names from the resolver when available, falling back to hardcoded names.
    /// </summary>
    internal static void AddRequiredSkill(List<EsmPropertyEntry> properties, int requiredSkillAv,
        FormIdResolver? resolver = null)
    {
        var skillName = resolver?.GetActorValueName(requiredSkillAv)
                        ?? EsmPropertyFormatter.ActorValueToSkillName(requiredSkillAv)
                        ?? $"AV#{requiredSkillAv}";
        properties.Add(new EsmPropertyEntry
        {
            Name = "Required Skill",
            Value = requiredSkillAv >= 0 ? skillName : "None",
            Category = "General"
        });
    }

    /// <summary>
    ///     Processes a GenericEsmRecord Fields dictionary into an expandable property.
    /// </summary>
    internal static void AddFieldsDictionary(
        List<EsmPropertyEntry> properties,
        Dictionary<string, object?> fieldsDict)
    {
        var subItems = new List<EsmPropertyEntry>();
        foreach (var (key, fieldVal) in fieldsDict)
        {
            var fieldStr = fieldVal switch
            {
                null => "null",
                byte[] b => $"[{b.Length} bytes]",
                uint fid => $"0x{fid:X8}",
                string s => s,
                Dictionary<string, object?> schemaFields => string.Join(", ",
                    schemaFields.Select(f =>
                        $"{f.Key}={EsmPropertyFormatter.FormatSchemaFieldValue(f.Value)}")),
                _ => fieldVal.ToString() ?? ""
            };
            subItems.Add(new EsmPropertyEntry { Name = key, Value = fieldStr ?? "" });
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = $"Subrecords ({fieldsDict.Count} fields)",
            Value = "",
            Category = "General",
            IsExpandable = true,
            SubItems = subItems
        });
    }

    /// <summary>
    ///     Processes an IList property into an expandable property with sub-items.
    /// </summary>
    internal static void AddListProperty(
        List<EsmPropertyEntry> properties,
        string displayName,
        string propertyName,
        IList list,
        FormIdResolver? resolver)
    {
        var subItems = new List<EsmPropertyEntry>();
        foreach (var item in list)
        {
            subItems.Add(CreateListItemEntry(item, resolver));
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = $"{displayName} ({list.Count} items)",
            Value = "",
            Category = EsmPropertyFormatter.CategorizeProperty(propertyName),
            IsExpandable = true,
            SubItems = subItems
        });
    }

    /// <summary>
    ///     Creates an EsmPropertyEntry for a list item with column layout for FormID references.
    /// </summary>
    internal static EsmPropertyEntry CreateListItemEntry(
        object? item,
        FormIdResolver? resolver)
    {
        if (item == null)
        {
            return new EsmPropertyEntry { Name = "", Value = "" };
        }

        // Factions: 4 columns - Editor ID, Full Name, Form ID, Rank
        if (item is FactionMembership faction)
        {
            var fullName = resolver?.GetDisplayName(faction.FactionFormId);
            var editorId = resolver?.GetEditorId(faction.FactionFormId);
            return new EsmPropertyEntry
            {
                Col1 = editorId ?? "",
                Col2 = fullName ?? "",
                Col3 = $"0x{faction.FactionFormId:X8}",
                Col4 = $"Rank {faction.Rank}",
                Col3FormId = faction.FactionFormId
            };
        }

        // Faction Ranks: Rank Number, Male Title, Female Title
        if (item is FactionRank rank)
        {
            return new EsmPropertyEntry
            {
                Col1 = $"Rank {rank.RankNumber}",
                Col2 = rank.MaleTitle ?? "",
                Col3 = rank.FemaleTitle ?? "",
                Col4 = rank.Insignia ?? ""
            };
        }

        // Faction Relations: Editor ID/Name, Full Name, Modifier, Combat Reaction
        if (item is FactionRelation relation)
        {
            var fullName = resolver?.GetDisplayName(relation.FactionFormId);
            var editorId = resolver?.GetEditorId(relation.FactionFormId);
            return new EsmPropertyEntry
            {
                Col1 = editorId ?? $"0x{relation.FactionFormId:X8}",
                Col2 = fullName ?? "",
                Col3 = $"Modifier: {relation.Modifier}",
                Col4 = $"Combat: {relation.CombatFlags}",
                LinkedFormId = relation.FactionFormId
            };
        }

        // Inventory: 4 columns - Quantity, Editor ID, Full Name, Form ID
        if (item is InventoryItem inv)
        {
            var fullName = resolver?.GetDisplayName(inv.ItemFormId);
            var editorId = resolver?.GetEditorId(inv.ItemFormId);
            return new EsmPropertyEntry
            {
                Col1 = $"{inv.Count}\u00d7",
                Col2 = editorId ?? "",
                Col3 = fullName ?? "",
                Col4 = $"0x{inv.ItemFormId:X8}",
                Col4FormId = inv.ItemFormId
            };
        }

        // Leveled List Entries: 4 columns - Level, Editor ID, Full Name, Form ID + Count
        if (item is LeveledEntry entry)
        {
            var fullName = resolver?.GetDisplayName(entry.FormId);
            var editorId = resolver?.GetEditorId(entry.FormId);
            return new EsmPropertyEntry
            {
                Col1 = $"Lvl {entry.Level}",
                Col2 = editorId ?? "",
                Col3 = fullName ?? "",
                Col4 = $"0x{entry.FormId:X8} (\u00d7{entry.Count})",
                Col4FormId = entry.FormId
            };
        }

        // Placed references: Base EditorID/Type | Position | Base FormID | Own FormID
        if (item is PlacedReference refr)
        {
            var refName = PlacedObjectCategoryResolver.GetReferenceEditorId(refr, resolver)
                          ?? PlacedObjectCategoryResolver.GetReferenceAwareName(refr, resolver);
            var baseName = resolver?.GetBestName(refr.BaseFormId) ?? refr.BaseEditorId ?? refr.RecordType;
            var pos = $"({refr.X:F0}, {refr.Y:F0}, {refr.Z:F0})";
            return new EsmPropertyEntry
            {
                Col1 = refName,
                Col2 = pos,
                Col3 = $"0x{refr.FormId:X8}",
                Col4 = $"{baseName} (0x{refr.BaseFormId:X8})",
                Col3FormId = refr.FormId
            };
        }

        // For uint items that might be FormIDs (Spells, Packages, etc.)
        if (item is uint formId && formId != 0)
        {
            var fullName = resolver?.GetDisplayName(formId);
            var editorId = resolver?.GetEditorId(formId);
            return new EsmPropertyEntry
            {
                Name = EsmPropertyFormatter.FormatFormIdReference(formId, editorId, fullName),
                Value = "",
                LinkedFormId = formId
            };
        }

        // For complex objects, format as Name = type, Value = properties
        var type = item.GetType();
        if (!type.IsPrimitive && type != typeof(string) && !type.IsEnum)
        {
            var props = EsmPropertyFormatter.GetCachedProperties(type);
            if (props.Length > 0)
            {
                var parts = new List<string>();
                foreach (var prop in props)
                {
                    if (!prop.CanRead)
                    {
                        continue;
                    }

                    var val = prop.GetValue(item);
                    if (val == null)
                    {
                        continue;
                    }

                    // Resolve FormID-like fields in sub-items
                    if (val is uint fid && fid != 0 &&
                        (prop.Name.EndsWith("FormId", StringComparison.Ordinal) ||
                         prop.Name == "FormId" ||
                         EsmPropertyFormatter.KnownFormIdFields.Contains(prop.Name)))
                    {
                        var dispName = resolver?.GetDisplayName(fid);
                        var edId = resolver?.GetEditorId(fid);
                        parts.Add(
                            $"{EsmPropertyFormatter.FormatPropertyName(prop.Name)}: {EsmPropertyFormatter.FormatFormIdReference(fid, edId, dispName)}");
                    }
                    else if (val is float f)
                    {
                        parts.Add($"{EsmPropertyFormatter.FormatPropertyName(prop.Name)}: {f:F2}");
                    }
                    else
                    {
                        parts.Add($"{EsmPropertyFormatter.FormatPropertyName(prop.Name)}: {val}");
                    }
                }

                return new EsmPropertyEntry { Name = "", Value = string.Join(", ", parts) };
            }
        }

        return new EsmPropertyEntry { Name = "", Value = item.ToString() ?? "" };
    }
}
