using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Reporting;

/// <summary>
///     Classifies records into sub-groups for the multi-page markdown report.
///     Each classifier maps a tracked record object to a human-readable group name
///     used for organizing tables on category pages.
/// </summary>
internal static class MarkdownGroupClassifier
{
    internal static string LookupGroupForFormId(
        MarkdownMultiPageWriter.CategoryDef cat, uint formId, List<VersionSnapshot> snapshots)
    {
        // Find the record in any snapshot to classify it
        foreach (var snapshot in snapshots)
        {
            foreach (var (key, value) in cat.RecordSelector(snapshot))
            {
                if (key == formId)
                {
                    return cat.GroupClassifier(value, snapshots);
                }
            }
        }

        return "Other";
    }

    internal static string ClassifyQuest(object record)
    {
        if (record is TrackedQuest quest)
        {
            if (quest.Stages.Count > 0 || quest.Objectives.Count > 0)
            {
                return "Quests with Stages/Objectives";
            }

            return "Other Quests";
        }

        return "Other";
    }

    internal static string GetWeaponTypeName(object record)
    {
        if (record is TrackedWeapon weapon)
        {
            return weapon.WeaponType switch
            {
                0 => "Hand-to-Hand",
                1 => "Melee (1-Handed)",
                2 => "Melee (2-Handed)",
                3 => "Pistols",
                4 => "Rifles",
                5 => "Automatic Weapons",
                6 => "Energy Pistols",
                7 => "Energy Rifles",
                8 => "Energy Automatic",
                9 => "Thrown",
                10 => "Mines",
                11 => "Launchers",
                12 => "Fat Man",
                _ => $"Unknown Type ({weapon.WeaponType})"
            };
        }

        return "Weapons";
    }

    internal static string GetArmorSlotGroup(object record)
    {
        if (record is TrackedArmor armor)
        {
            var flags = armor.BipedFlags;

            // Biped flags (Fallout NV): bit 0 = Head, bit 1 = Hair, bit 2 = Upper Body,
            // bit 3 = Left Hand, bit 4 = Right Hand, bit 5 = Weapon, bit 14 = Body Addon,
            // etc. Simplified classification:
            var hasHead = (flags & 0x3) != 0; // Head or Hair
            var hasBody = (flags & 0x4) != 0; // Upper Body
            var hasHands = (flags & 0x18) != 0; // Left/Right Hand

            if (hasBody && hasHead)
            {
                return "Full Suits / Power Armor";
            }

            if (hasBody)
            {
                return "Body Armor";
            }

            if (hasHead)
            {
                return "Headgear";
            }

            if (hasHands)
            {
                return "Gloves / Gauntlets";
            }

            return "Accessories / Other";
        }

        return "Armor";
    }

    internal static string GetItemSubtype(object record)
    {
        if (record is TrackedItem item)
        {
            return item.RecordType switch
            {
                "ALCH" => "Consumables",
                "MISC" => "Miscellaneous Items",
                "KEYM" => "Keys",
                _ => item.RecordType
            };
        }

        return "Items";
    }

    internal static string GetScriptGroup(object record)
    {
        if (record is TrackedScript script)
        {
            return $"{script.ScriptType} Scripts";
        }

        return "Scripts";
    }

    internal static string GetDialogueQuestGroup(object record, List<VersionSnapshot> snapshots)
    {
        if (record is TrackedDialogue dialogue && dialogue.QuestFormId.HasValue)
        {
            var questId = dialogue.QuestFormId.Value;
            foreach (var snapshot in snapshots)
            {
                if (snapshot.Quests.TryGetValue(questId, out var quest))
                {
                    var name = quest.FullName ?? quest.EditorId ?? $"0x{questId:X8}";
                    return $"{name} (0x{questId:X8})";
                }
            }

            return $"Quest 0x{questId:X8}";
        }

        return "Other Dialogues";
    }

    internal static string GetLocationGroup(object record)
    {
        if (record is TrackedLocation loc)
        {
            if (loc.RecordType == "WRLD")
            {
                return "Worldspaces";
            }

            return loc.IsInterior ? "Interior Cells" : "Exterior Cells";
        }

        return "Locations";
    }

    internal static string GetPlacementGroup(object record)
    {
        if (record is TrackedPlacement placement)
        {
            return placement.IsMapMarker ? "Map Markers" : "Other Placements";
        }

        return "Placements";
    }

    internal static string GetCreatureTypeGroup(object record)
    {
        if (record is TrackedCreature creature)
        {
            return creature.CreatureType switch
            {
                0 => "Animals",
                1 => "Mutated Animals",
                2 => "Mutated Insects",
                3 => "Abominations",
                4 => "Super Mutants",
                5 => "Feral Ghouls",
                6 => "Robots",
                7 => "Giant Insects",
                _ => $"Unknown Type ({creature.CreatureType})"
            };
        }

        return "Creatures";
    }

    internal static string GetPerkGroup(object record)
    {
        if (record is TrackedPerk perk)
        {
            return perk.Trait != 0 ? "Traits" : "Perks";
        }

        return "Perks";
    }

    internal static string GetLeveledListGroup(object record)
    {
        if (record is TrackedLeveledList list)
        {
            return list.ListType switch
            {
                "LVLC" => "Leveled Creatures",
                "LVLN" => "Leveled NPCs",
                "LVLI" => "Leveled Items",
                _ => $"Leveled Lists ({list.ListType})"
            };
        }

        return "Leveled Lists";
    }

    internal static string GetNoteGroup(object record)
    {
        if (record is TrackedNote note)
        {
            return note.NoteType switch
            {
                0 => "Sound Notes",
                1 => "Text Notes",
                2 => "Image Notes",
                3 => "Voice Notes",
                _ => $"Notes (Type {note.NoteType})"
            };
        }

        return "Notes";
    }
}
