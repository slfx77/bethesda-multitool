using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.CLI.Formatters;

/// <summary>
///     Flattens a RecordCollection into a uniform list of (FormId, Type, EditorId, DisplayName)
///     entries for use by list, show, and diff commands.
/// </summary>
internal static class RecordFlattener
{
    internal static List<FlatRecord> Flatten(RecordCollection records)
    {
        var result = new List<FlatRecord>();

        // Characters
        result.AddRange(records.Npcs.Select(r => new FlatRecord(r.FormId, "NPC_", r.EditorId, r.FullName)));
        result.AddRange(records.Creatures.Select(r => new FlatRecord(r.FormId, "CREA", r.EditorId, r.FullName)));
        result.AddRange(records.Races.Select(r => new FlatRecord(r.FormId, "RACE", r.EditorId, r.FullName)));
        result.AddRange(records.Factions.Select(r => new FlatRecord(r.FormId, "FACT", r.EditorId, r.FullName)));
        result.AddRange(records.Classes.Select(r => new FlatRecord(r.FormId, "CLAS", r.EditorId, r.FullName)));

        // Quests & Dialogue
        result.AddRange(records.Quests.Select(r => new FlatRecord(r.FormId, "QUST", r.EditorId, r.FullName)));
        result.AddRange(records.DialogTopics.Select(r => new FlatRecord(r.FormId, "DIAL", r.EditorId, r.FullName)));
        result.AddRange(records.Dialogues.Select(r =>
            new FlatRecord(r.FormId, "INFO", r.EditorId, r.Responses.FirstOrDefault()?.Text)));
        result.AddRange(records.Notes.Select(r => new FlatRecord(r.FormId, "NOTE", r.EditorId, r.FullName)));
        result.AddRange(records.Books.Select(r => new FlatRecord(r.FormId, "BOOK", r.EditorId, r.FullName)));
        result.AddRange(records.Terminals.Select(r => new FlatRecord(r.FormId, "TERM", r.EditorId, r.FullName)));
        result.AddRange(records.Scripts.Select(r => new FlatRecord(r.FormId, "SCPT", r.EditorId, null)));
        result.AddRange(records.Messages.Select(r => new FlatRecord(r.FormId, "MESG", r.EditorId, r.FullName)));

        // Items
        result.AddRange(records.Weapons.Select(r => new FlatRecord(r.FormId, "WEAP", r.EditorId, r.FullName)));
        result.AddRange(records.Armor.Select(r => new FlatRecord(r.FormId, "ARMO", r.EditorId, r.FullName)));
        result.AddRange(records.Ammo.Select(r => new FlatRecord(r.FormId, "AMMO", r.EditorId, r.FullName)));
        result.AddRange(records.Consumables.Select(r => new FlatRecord(r.FormId, "ALCH", r.EditorId, r.FullName)));
        result.AddRange(records.MiscItems.Select(r => new FlatRecord(r.FormId, "MISC", r.EditorId, r.FullName)));
        result.AddRange(records.Keys.Select(r => new FlatRecord(r.FormId, "KEYM", r.EditorId, r.FullName)));
        result.AddRange(records.Containers.Select(r => new FlatRecord(r.FormId, "CONT", r.EditorId, r.FullName)));
        result.AddRange(records.WeaponMods.Select(r => new FlatRecord(r.FormId, "IMOD", r.EditorId, r.FullName)));

        // Abilities
        result.AddRange(records.Perks.Select(r => new FlatRecord(r.FormId, "PERK", r.EditorId, r.FullName)));
        result.AddRange(records.Spells.Select(r => new FlatRecord(r.FormId, "SPEL", r.EditorId, r.FullName)));
        result.AddRange(records.Enchantments.Select(r => new FlatRecord(r.FormId, "ENCH", r.EditorId, r.FullName)));
        result.AddRange(records.BaseEffects.Select(r => new FlatRecord(r.FormId, "MGEF", r.EditorId, r.FullName)));

        // World
        result.AddRange(records.Cells.Select(r => new FlatRecord(r.FormId, "CELL", r.EditorId, r.FullName)));
        result.AddRange(records.Worldspaces.Select(r => new FlatRecord(r.FormId, "WRLD", r.EditorId, r.FullName)));
        result.AddRange(records.LeveledLists.Select(r => new FlatRecord(r.FormId, "LVLI", r.EditorId, null)));
        result.AddRange(records.Statics.Select(r => new FlatRecord(r.FormId, "STAT", r.EditorId, null)));
        result.AddRange(records.Activators.Select(r => new FlatRecord(r.FormId, "ACTI", r.EditorId, r.FullName)));
        result.AddRange(records.Doors.Select(r => new FlatRecord(r.FormId, "DOOR", r.EditorId, r.FullName)));
        result.AddRange(records.Furniture.Select(r => new FlatRecord(r.FormId, "FURN", r.EditorId, null)));
        result.AddRange(records.Lights.Select(r => new FlatRecord(r.FormId, "LIGH", r.EditorId, r.FullName)));

        // Game Data
        result.AddRange(records.GameSettings.Select(r => new FlatRecord(r.FormId, "GMST", r.EditorId, r.DisplayValue)));
        result.AddRange(records.Globals.Select(r => new FlatRecord(r.FormId, "GLOB", r.EditorId, r.DisplayValue)));
        result.AddRange(records.Recipes.Select(r => new FlatRecord(r.FormId, "RCPE", r.EditorId, r.FullName)));
        result.AddRange(records.Challenges.Select(r => new FlatRecord(r.FormId, "CHAL", r.EditorId, r.FullName)));
        result.AddRange(records.Reputations.Select(r => new FlatRecord(r.FormId, "REPU", r.EditorId, r.FullName)));
        result.AddRange(records.FormLists.Select(r => new FlatRecord(r.FormId, "FLST", r.EditorId, null)));
        result.AddRange(records.Projectiles.Select(r => new FlatRecord(r.FormId, "PROJ", r.EditorId, r.FullName)));
        result.AddRange(records.Explosions.Select(r => new FlatRecord(r.FormId, "EXPL", r.EditorId, r.FullName)));
        result.AddRange(records.MusicTypes.Select(r => new FlatRecord(r.FormId, "MUSC", r.EditorId, r.FileName)));

        // AI
        result.AddRange(records.Packages.Select(r => new FlatRecord(r.FormId, "PACK", r.EditorId, null)));
        result.AddRange(records.CombatStyles.Select(r => new FlatRecord(r.FormId, "CSTY", r.EditorId, null)));

        // Stats
        result.AddRange(
            records.ActorValueInfos.Select(r => new FlatRecord(r.FormId, "AVIF", r.EditorId, r.FullName)));

        // Generic
        result.AddRange(
            records.GenericRecords.Select(r => new FlatRecord(r.FormId, r.RecordType, r.EditorId, r.FullName)));

        return result.OrderBy(r => r.Type).ThenBy(r => r.FormId).ToList();
    }

    internal record FlatRecord(uint FormId, string Type, string? EditorId, string? DisplayName);
}
