using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Processing;

/// <summary>
///     Compares two VersionSnapshots to produce a VersionDiffResult.
///     Applies DMP-awareness rules for incomplete data sources.
/// </summary>
public static class SnapshotDiffer
{
    /// <summary>
    ///     Computes the diff between two snapshots.
    ///     Applies DMP-awareness: missing from a DMP source is NOT treated as removed.
    /// </summary>
    public static VersionDiffResult Diff(VersionSnapshot from, VersionSnapshot to)
    {
        return new VersionDiffResult
        {
            FromBuild = from.Build,
            ToBuild = to.Build,
            QuestChanges = DiffCategory(from.Quests, to.Quests, "QUST",
                RecordFieldComparer.CompareQuests, from.Build, to.Build),
            NpcChanges = DiffCategory(from.Npcs, to.Npcs, "NPC_",
                RecordFieldComparer.CompareNpcs, from.Build, to.Build),
            DialogueChanges = DiffCategory(from.Dialogues, to.Dialogues, "INFO",
                RecordFieldComparer.CompareDialogues, from.Build, to.Build),
            WeaponChanges = DiffCategory(from.Weapons, to.Weapons, "WEAP",
                RecordFieldComparer.CompareWeapons, from.Build, to.Build),
            ArmorChanges = DiffCategory(from.Armor, to.Armor, "ARMO",
                RecordFieldComparer.CompareArmor, from.Build, to.Build),
            ItemChanges = DiffCategory(from.Items, to.Items, "ITEM",
                RecordFieldComparer.CompareItems, from.Build, to.Build),
            ScriptChanges = DiffCategory(from.Scripts, to.Scripts, "SCPT",
                RecordFieldComparer.CompareScripts, from.Build, to.Build),
            LocationChanges = DiffCategory(from.Locations, to.Locations, "CELL",
                RecordFieldComparer.CompareLocations, from.Build, to.Build),
            PlacementChanges = DiffCategory(from.Placements, to.Placements, "REFR",
                RecordFieldComparer.ComparePlacements, from.Build, to.Build),
            CreatureChanges = DiffCategory(from.Creatures, to.Creatures, "CREA",
                RecordFieldComparer.CompareCreatures, from.Build, to.Build),
            PerkChanges = DiffCategory(from.Perks, to.Perks, "PERK",
                RecordFieldComparer.ComparePerks, from.Build, to.Build),
            AmmoChanges = DiffCategory(from.Ammo, to.Ammo, "AMMO",
                RecordFieldComparer.CompareAmmo, from.Build, to.Build),
            LeveledListChanges = DiffCategory(from.LeveledLists, to.LeveledLists, "LVLX",
                RecordFieldComparer.CompareLeveledLists, from.Build, to.Build),
            NoteChanges = DiffCategory(from.Notes, to.Notes, "NOTE",
                RecordFieldComparer.CompareNotes, from.Build, to.Build),
            TerminalChanges = DiffCategory(from.Terminals, to.Terminals, "TERM",
                RecordFieldComparer.CompareTerminals, from.Build, to.Build)
        };
    }

    private static List<RecordChange> DiffCategory<T>(
        Dictionary<uint, T> fromDict,
        Dictionary<uint, T> toDict,
        string recordType,
        Func<T, T, List<FieldChange>> comparer,
        BuildInfo fromBuild,
        BuildInfo toBuild)
        where T : class
    {
        var changes = new List<RecordChange>();

        // Records in 'to' but not in 'from' = Added
        foreach (var (formId, record) in toDict)
        {
            if (!fromDict.ContainsKey(formId))
            {
                changes.Add(new RecordChange
                {
                    FormId = formId,
                    EditorId = GetEditorId(record),
                    FullName = GetFullName(record),
                    RecordType = recordType,
                    ChangeType = ChangeType.Added
                });
            }
        }

        // Records in 'from' but not in 'to'
        foreach (var (formId, record) in fromDict)
        {
            if (!toDict.ContainsKey(formId))
            {
                // DMP-awareness: if 'to' is a DMP, missing records aren't conclusive
                if (!toBuild.IsAuthoritative)
                {
                    continue; // Skip - DMP is incomplete, can't conclude removal
                }

                // If 'from' is also a DMP, missing from ESM is interesting (potential cut content)
                changes.Add(new RecordChange
                {
                    FormId = formId,
                    EditorId = GetEditorId(record),
                    FullName = GetFullName(record),
                    RecordType = recordType,
                    ChangeType = ChangeType.Removed
                });
            }
        }

        // Records in both = compare fields
        foreach (var (formId, fromRecord) in fromDict)
        {
            if (toDict.TryGetValue(formId, out var toRecord))
            {
                var fieldChanges = comparer(fromRecord, toRecord);
                if (fieldChanges.Count > 0)
                {
                    changes.Add(new RecordChange
                    {
                        FormId = formId,
                        EditorId = GetEditorId(toRecord) ?? GetEditorId(fromRecord),
                        FullName = GetFullName(toRecord) ?? GetFullName(fromRecord),
                        RecordType = recordType,
                        ChangeType = ChangeType.Changed,
                        FieldChanges = fieldChanges
                    });
                }
            }
        }

        FilterDmpUnreliableFields(changes, recordType, fromBuild, toBuild);
        return changes;
    }

    private static string? GetEditorId<T>(T record)
    {
        return record switch
        {
            TrackedQuest q => q.EditorId,
            TrackedNpc n => n.EditorId,
            TrackedDialogue d => d.EditorId,
            TrackedWeapon w => w.EditorId,
            TrackedArmor a => a.EditorId,
            TrackedItem i => i.EditorId,
            TrackedScript s => s.EditorId,
            TrackedLocation l => l.EditorId,
            TrackedPlacement p => p.EditorId,
            TrackedCreature c => c.EditorId,
            TrackedPerk p => p.EditorId,
            TrackedAmmo a => a.EditorId,
            TrackedLeveledList l => l.EditorId,
            TrackedNote n => n.EditorId,
            TrackedTerminal t => t.EditorId,
            _ => null
        };
    }

    private static string? GetFullName<T>(T record)
    {
        return record switch
        {
            TrackedQuest q => q.FullName,
            TrackedNpc n => n.FullName,
            TrackedWeapon w => w.FullName,
            TrackedArmor a => a.FullName,
            TrackedItem i => i.FullName,
            TrackedLocation l => l.FullName,
            TrackedPlacement p => p.MarkerName,
            TrackedCreature c => c.FullName,
            TrackedPerk p => p.FullName,
            TrackedAmmo a => a.FullName,
            TrackedNote n => n.FullName,
            TrackedTerminal t => t.FullName,
            _ => null
        };
    }

    #region DMP Field Suppression

    /// <summary>
    ///     Fields that cannot be reliably extracted from DMP runtime structs.
    ///     When either build source is a DMP, these fields are filtered from diff results.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> DmpUnreliableFields = new()
    {
        ["ITEM"] = ["Effects"],
        ["QUST"] = ["QuestDelay", "Script"]
    };

    /// <summary>
    ///     Filters out field changes that are unreliable when either source is a DMP.
    /// </summary>
    private static void FilterDmpUnreliableFields(
        List<RecordChange> changes, string recordType,
        BuildInfo fromBuild, BuildInfo toBuild)
    {
        if (fromBuild.IsAuthoritative && toBuild.IsAuthoritative)
        {
            return; // Both ESM — no filtering needed
        }

        DmpUnreliableFields.TryGetValue(recordType, out var unreliableFields);
        var isQuest = recordType == "QUST";

        if (unreliableFields == null && !isQuest)
        {
            return;
        }

        foreach (var change in changes.Where(c => c.ChangeType == ChangeType.Changed && c.FieldChanges.Count > 0))
        {
            change.FieldChanges.RemoveAll(fc => IsUnreliableDmpField(fc.FieldName, unreliableFields, isQuest));
        }

        // Remove changes that no longer have any field changes
        changes.RemoveAll(c => c.ChangeType == ChangeType.Changed && c.FieldChanges.Count == 0);
    }

    private static bool IsUnreliableDmpField(string fieldName, HashSet<string>? unreliableFields, bool isQuest)
    {
        if (unreliableFields != null && unreliableFields.Contains(fieldName))
        {
            return true;
        }

        return isQuest && (fieldName.StartsWith("Stage ", StringComparison.Ordinal) || fieldName.StartsWith("Objective ", StringComparison.Ordinal));
    }

    #endregion
}
