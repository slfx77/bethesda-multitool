using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Reverse index of FormID usages across scripts, lists, inventories, packages, and attached scripts.
/// </summary>
internal sealed class FormUsageIndex
{
    private readonly Dictionary<uint, List<FormUsageReference>> _usageByTargetFormId = [];

    public int GetUseCount(uint targetFormId)
    {
        return _usageByTargetFormId.TryGetValue(targetFormId, out var usages) ? usages.Count : 0;
    }

    public IReadOnlyList<FormUsageReference> GetUsages(uint targetFormId)
    {
        return _usageByTargetFormId.TryGetValue(targetFormId, out var usages)
            ? usages
            : [];
    }

    public static FormUsageIndex Build(RecordCollection records)
    {
        var index = new FormUsageIndex();

        AddScriptReferenceUses(index, records.Scripts);
        AddDialogueScriptUses(index, records.Dialogues);
        AddContainerUses(index, records.Containers);
        AddNpcInventoryUses(index, records.Npcs);
        AddLeveledListUses(index, records.LeveledLists);
        AddFormListUses(index, records.FormLists);
        AddAttachedScriptUses(index, records.Quests, q => q.FormId, q => q.Script, "Quest", "Attached script");
        AddAttachedScriptUses(index, records.Npcs, n => n.FormId, n => n.Script, "NPC", "Attached script");
        AddAttachedScriptUses(index, records.Creatures, c => c.FormId, c => c.Script, "Creature", "Attached script");
        AddAttachedScriptUses(index, records.Containers, c => c.FormId, c => c.Script, "Container", "Attached script");
        AddAttachedScriptUses(index, records.Activators, a => a.FormId, a => a.Script, "Activator", "Attached script");
        AddAttachedScriptUses(index, records.Doors, d => d.FormId, d => d.Script, "Door", "Attached script");
        AddAttachedScriptUses(index, records.Furniture, f => f.FormId, f => f.Script, "Furniture", "Attached script");
        AddPackageUses(index, records.Packages);
        AddActorPackageUses(index, records.Npcs, records.Creatures, records.Packages);
        AddDialogueConditionUses(index, records.Dialogues);
        AddDialogueTopicUses(index, records.DialogTopics);

        return index;
    }

    private static void AddScriptReferenceUses(FormUsageIndex index, IEnumerable<ScriptRecord> scripts)
    {
        foreach (var script in scripts)
        {
            foreach (var referencedObject in script.ReferencedObjects)
            {
                if ((referencedObject & 0x80000000) != 0)
                {
                    continue;
                }

                index.Add(referencedObject,
                    new FormUsageReference(script.FormId, "Script", "SCRO reference"));
            }
        }
    }

    private static void AddDialogueScriptUses(FormUsageIndex index, IEnumerable<DialogueRecord> dialogues)
    {
        foreach (var dialogue in dialogues)
        {
            // Use TopicFormId as the source so Used By shows the topic EditorId
            // and links navigate to the Dialogue Browser tab
            var sourceFormId = dialogue.TopicFormId is > 0
                ? dialogue.TopicFormId.Value
                : dialogue.FormId;

            for (var i = 0; i < dialogue.ResultScripts.Count; i++)
            {
                foreach (var referencedObject in dialogue.ResultScripts[i].ReferencedObjects)
                {
                    index.Add(referencedObject,
                        new FormUsageReference(sourceFormId, "Dialogue",
                            $"Result script {i + 1}"));
                }
            }
        }
    }

    private static void AddDialogueConditionUses(FormUsageIndex index, IEnumerable<DialogueRecord> dialogues)
    {
        foreach (var dialogue in dialogues)
        {
            if (dialogue.Conditions.Count == 0)
            {
                continue;
            }

            var sourceFormId = dialogue.TopicFormId is > 0
                ? dialogue.TopicFormId.Value
                : dialogue.FormId;

            foreach (var cond in dialogue.Conditions)
            {
                if (cond.Parameter1 != 0 && DialogueConditionDisplayFormatter.IsFormReference(cond, 0))
                {
                    index.Add(cond.Parameter1,
                        new FormUsageReference(sourceFormId, "Dialogue", "Condition reference"));
                }

                if (cond.Parameter2 != 0 && DialogueConditionDisplayFormatter.IsFormReference(cond, 1))
                {
                    index.Add(cond.Parameter2,
                        new FormUsageReference(sourceFormId, "Dialogue", "Condition reference"));
                }

                if (cond.Reference != 0)
                {
                    index.Add(cond.Reference,
                        new FormUsageReference(sourceFormId, "Dialogue", "Condition reference"));
                }
            }
        }
    }

    private static void AddDialogueTopicUses(FormUsageIndex index, IEnumerable<DialogTopicRecord> topics)
    {
        foreach (var topic in topics)
        {
            if (topic.QuestFormId is not > 0)
            {
                continue;
            }

            var context = DialogTopicRecord.GetTopicTypeName(topic.TopicType);
            if (topic.ResponseCount > 0)
            {
                context += $" ({topic.ResponseCount} responses)";
            }

            index.Add(topic.QuestFormId.Value,
                new FormUsageReference(topic.FormId, "Dialog Topic", context));
        }
    }

    private static void AddContainerUses(FormUsageIndex index, IEnumerable<ContainerRecord> containers)
    {
        foreach (var container in containers)
        {
            foreach (var item in container.Contents)
            {
                index.Add(item.ItemFormId,
                    new FormUsageReference(container.FormId, "Container",
                        $"Contents x{item.Count}"));
            }
        }
    }

    private static void AddNpcInventoryUses(FormUsageIndex index, IEnumerable<NpcRecord> npcs)
    {
        foreach (var npc in npcs)
        {
            foreach (var item in npc.Inventory)
            {
                index.Add(item.ItemFormId,
                    new FormUsageReference(npc.FormId, "NPC",
                        $"Inventory x{item.Count}"));
            }
        }
    }

    private static void AddLeveledListUses(FormUsageIndex index, IEnumerable<LeveledListRecord> leveledLists)
    {
        foreach (var list in leveledLists)
        {
            foreach (var entry in list.Entries)
            {
                index.Add(entry.FormId,
                    new FormUsageReference(list.FormId, "Leveled List",
                        $"Entry level {entry.Level}, count {entry.Count}"));
            }
        }
    }

    private static void AddFormListUses(FormUsageIndex index, IEnumerable<FormListRecord> formLists)
    {
        foreach (var formList in formLists)
        {
            foreach (var formId in formList.FormIds)
            {
                index.Add(formId,
                    new FormUsageReference(formList.FormId, "Form List", "Form list entry"));
            }
        }
    }

    private static void AddAttachedScriptUses<T>(
        FormUsageIndex index,
        IEnumerable<T> records,
        Func<T, uint> sourceFormIdSelector,
        Func<T, uint?> scriptFormIdSelector,
        string sourceKind,
        string context)
    {
        foreach (var record in records)
        {
            var scriptFormId = scriptFormIdSelector(record);
            if (scriptFormId is > 0)
            {
                index.Add(scriptFormId.Value,
                    new FormUsageReference(sourceFormIdSelector(record), sourceKind, context));
            }
        }
    }

    private static void AddPackageUses(FormUsageIndex index, IEnumerable<PackageRecord> packages)
    {
        foreach (var package in packages)
        {
            foreach (var (targetFormId, context) in EnumeratePackageFormUses(package))
            {
                index.Add(targetFormId,
                    new FormUsageReference(package.FormId, "Package", context));
            }
        }
    }

    private static void AddActorPackageUses(
        FormUsageIndex index,
        IEnumerable<NpcRecord> npcs,
        IEnumerable<CreatureRecord> creatures,
        IEnumerable<PackageRecord> packages)
    {
        var packagesByFormId = packages
            .GroupBy(p => p.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        AddActorPackageUses(index, npcs, packagesByFormId, "NPC");
        AddActorPackageUses(index, creatures, packagesByFormId, "Creature");
    }

    private static void AddActorPackageUses(
        FormUsageIndex index,
        IEnumerable<NpcRecord> actors,
        IReadOnlyDictionary<uint, PackageRecord> packagesByFormId,
        string actorKind)
    {
        foreach (var actor in actors)
        {
            foreach (var packageFormId in actor.Packages)
            {
                index.Add(packageFormId,
                    new FormUsageReference(actor.FormId, actorKind, "Assigned AI package"));

                if (!packagesByFormId.TryGetValue(packageFormId, out var package))
                {
                    continue;
                }

                foreach (var (targetFormId, context) in EnumeratePackageFormUses(package))
                {
                    index.Add(targetFormId,
                        new FormUsageReference(actor.FormId, actorKind,
                            $"AI package {BuildPackageLabel(package)}: {context}"));
                }
            }
        }
    }

    private static void AddActorPackageUses(
        FormUsageIndex index,
        IEnumerable<CreatureRecord> actors,
        IReadOnlyDictionary<uint, PackageRecord> packagesByFormId,
        string actorKind)
    {
        foreach (var actor in actors)
        {
            foreach (var packageFormId in actor.Packages)
            {
                index.Add(packageFormId,
                    new FormUsageReference(actor.FormId, actorKind, "Assigned AI package"));

                if (!packagesByFormId.TryGetValue(packageFormId, out var package))
                {
                    continue;
                }

                foreach (var (targetFormId, context) in EnumeratePackageFormUses(package))
                {
                    index.Add(targetFormId,
                        new FormUsageReference(actor.FormId, actorKind,
                            $"AI package {BuildPackageLabel(package)}: {context}"));
                }
            }
        }
    }

    private static IEnumerable<(uint TargetFormId, string Context)> EnumeratePackageFormUses(PackageRecord package)
    {
        if (package.Location is { Union: > 0 } location && IsPackageLocationFormReference(location))
        {
            yield return (location.Union, $"Location ({DescribePackageLocation(location)})");
        }

        if (package.Location2 is { Union: > 0 } location2 && IsPackageLocationFormReference(location2))
        {
            yield return (location2.Union, $"Location 2 ({DescribePackageLocation(location2)})");
        }

        if (package.Target is { FormIdOrType: > 0 } target && IsPackageTargetFormReference(target))
        {
            yield return (target.FormIdOrType, $"Target ({target.TypeName})");
        }

        if (package.Target2 is { FormIdOrType: > 0 } target2 && IsPackageTargetFormReference(target2))
        {
            yield return (target2.FormIdOrType, $"Target 2 ({target2.TypeName})");
        }
    }

    private static bool IsPackageLocationFormReference(PackageLocation location)
    {
        return location.Type is 0 or 1 or 4 or 12;
    }

    private static bool IsPackageTargetFormReference(PackageTarget target)
    {
        return target.Type is 0 or 1 or 3;
    }

    private static string DescribePackageLocation(PackageLocation location)
    {
        return location.Type switch
        {
            0 => "Near Reference",
            1 => "In Cell",
            4 => "Object ID",
            12 => "Near Linked Ref",
            _ => $"Type {location.Type}"
        };
    }

    private static string BuildPackageLabel(PackageRecord package)
    {
        return !string.IsNullOrWhiteSpace(package.EditorId)
            ? package.EditorId
            : $"0x{package.FormId:X8}";
    }

    private void Add(uint targetFormId, FormUsageReference usage)
    {
        if (targetFormId == 0)
        {
            return;
        }

        if (!_usageByTargetFormId.TryGetValue(targetFormId, out var usages))
        {
            usages = [];
            _usageByTargetFormId[targetFormId] = usages;
        }

        if (!usages.Contains(usage))
        {
            usages.Add(usage);
        }
    }
}
