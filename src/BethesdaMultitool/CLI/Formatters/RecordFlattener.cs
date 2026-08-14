using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.CLI.Formatters;

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
        result.AddRange(
            records.EncounterZones.Select(r => new FlatRecord(r.FormId, "ECZN", r.EditorId, r.FullName)));
        result.AddRange(records.Eyes.Select(r => new FlatRecord(r.FormId, "EYES", r.EditorId, r.FullName)));
        result.AddRange(records.Hair.Select(r => new FlatRecord(r.FormId, "HAIR", r.EditorId, r.FullName)));
        result.AddRange(records.HeadParts.Select(r => new FlatRecord(r.FormId, "HDPT", r.EditorId, r.FullName)));
        result.AddRange(records.VoiceTypes.Select(r => new FlatRecord(r.FormId, "VTYP", r.EditorId, null)));

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
        result.AddRange(records.Ingredients.Select(r => new FlatRecord(r.FormId, "INGR", r.EditorId, r.FullName)));
        result.AddRange(records.MiscItems.Select(r => new FlatRecord(r.FormId, "MISC", r.EditorId, r.FullName)));
        result.AddRange(records.Keys.Select(r => new FlatRecord(r.FormId, "KEYM", r.EditorId, r.FullName)));
        result.AddRange(records.Containers.Select(r => new FlatRecord(r.FormId, "CONT", r.EditorId, r.FullName)));
        result.AddRange(records.WeaponMods.Select(r => new FlatRecord(r.FormId, "IMOD", r.EditorId, r.FullName)));
        result.AddRange(records.ArmorAddons.Select(r => new FlatRecord(r.FormId, "ARMA", r.EditorId, r.FullName)));

        // Abilities
        result.AddRange(records.Perks.Select(r => new FlatRecord(r.FormId, "PERK", r.EditorId, r.FullName)));
        result.AddRange(records.Spells.Select(r => new FlatRecord(r.FormId, "SPEL", r.EditorId, r.FullName)));
        result.AddRange(records.Enchantments.Select(r => new FlatRecord(r.FormId, "ENCH", r.EditorId, r.FullName)));
        result.AddRange(records.BaseEffects.Select(r => new FlatRecord(r.FormId, "MGEF", r.EditorId, r.FullName)));

        // World
        result.AddRange(records.Cells.Select(r => new FlatRecord(r.FormId, "CELL", r.EditorId, r.FullName)));
        result.AddRange(records.Worldspaces.Select(r => new FlatRecord(r.FormId, "WRLD", r.EditorId, r.FullName)));
        result.AddRange(records.LeveledLists.Select(r => new FlatRecord(r.FormId, r.ListType, r.EditorId, null)));
        result.AddRange(records.Statics.Select(r => new FlatRecord(r.FormId, "STAT", r.EditorId, null)));
        result.AddRange(
            records.StaticCollections.Select(r => new FlatRecord(r.FormId, "SCOL", r.EditorId, null)));
        result.AddRange(records.Activators.Select(r => new FlatRecord(r.FormId, "ACTI", r.EditorId, r.FullName)));
        result.AddRange(records.Doors.Select(r => new FlatRecord(r.FormId, "DOOR", r.EditorId, r.FullName)));
        result.AddRange(records.Furniture.Select(r => new FlatRecord(r.FormId, "FURN", r.EditorId, null)));
        result.AddRange(records.Lights.Select(r => new FlatRecord(r.FormId, "LIGH", r.EditorId, r.FullName)));
        result.AddRange(records.CameraPaths.Select(r => new FlatRecord(r.FormId, "CPTH", r.EditorId, null)));
        result.AddRange(records.LoadScreenTypes.Select(r => new FlatRecord(r.FormId, "LSCT", r.EditorId, null)));
        result.AddRange(records.IdleAnimations.Select(r => new FlatRecord(r.FormId, "IDLE", r.EditorId, null)));
        result.AddRange(records.PlacedGrenades.Select(r => new FlatRecord(r.FormId, "PGRE", r.EditorId, null)));
        result.AddRange(records.Regions.Select(r => new FlatRecord(r.FormId, "REGN", r.EditorId, null)));
        result.AddRange(records.NavMeshInfoMaps.Select(r => new FlatRecord(r.FormId, "NAVI", r.EditorId, null)));
        result.AddRange(records.NavMeshes.Select(r => new FlatRecord(r.FormId, "NAVM", r.EditorId, null)));

        // Game Data
        result.AddRange(records.GameSettings.Select(r => new FlatRecord(r.FormId, "GMST", r.EditorId, r.DisplayValue)));
        result.AddRange(records.Globals.Select(r => new FlatRecord(r.FormId, "GLOB", r.EditorId, r.DisplayValue)));
        result.AddRange(records.Recipes.Select(r => new FlatRecord(r.FormId, "RCPE", r.EditorId, r.FullName)));
        result.AddRange(
            records.RecipeCategories.Select(r => new FlatRecord(r.FormId, "RCCT", r.EditorId, r.FullName)));
        result.AddRange(
            records.ConstructibleObjects.Select(r => new FlatRecord(r.FormId, "COBJ", r.EditorId, r.FullName)));
        result.AddRange(records.Challenges.Select(r => new FlatRecord(r.FormId, "CHAL", r.EditorId, r.FullName)));
        result.AddRange(records.Reputations.Select(r => new FlatRecord(r.FormId, "REPU", r.EditorId, r.FullName)));
        result.AddRange(records.CaravanCards.Select(r => new FlatRecord(r.FormId, "CCRD", r.EditorId, r.FullName)));
        result.AddRange(records.CaravanMoney.Select(r => new FlatRecord(r.FormId, "CMNY", r.EditorId, null)));
        result.AddRange(records.CaravanDecks.Select(r => new FlatRecord(r.FormId, "CDCK", r.EditorId, null)));
        result.AddRange(records.RadiationStages.Select(r => new FlatRecord(r.FormId, "RADS", r.EditorId, null)));
        result.AddRange(
            records.DehydrationStages.Select(r => new FlatRecord(r.FormId, "DEHY", r.EditorId, null)));
        result.AddRange(records.HungerStages.Select(r => new FlatRecord(r.FormId, "HUNG", r.EditorId, null)));
        result.AddRange(
            records.SleepDeprivationStages.Select(r => new FlatRecord(r.FormId, "SLPD", r.EditorId, null)));
        result.AddRange(records.FormLists.Select(r => new FlatRecord(r.FormId, "FLST", r.EditorId, null)));
        result.AddRange(records.Projectiles.Select(r => new FlatRecord(r.FormId, "PROJ", r.EditorId, r.FullName)));
        result.AddRange(records.Explosions.Select(r => new FlatRecord(r.FormId, "EXPL", r.EditorId, r.FullName)));
        result.AddRange(records.Debris.Select(r => new FlatRecord(r.FormId, "DEBR", r.EditorId, null)));

        // AI
        result.AddRange(records.Packages.Select(r => new FlatRecord(r.FormId, "PACK", r.EditorId, null)));
        result.AddRange(records.CombatStyles.Select(r => new FlatRecord(r.FormId, "CSTY", r.EditorId, null)));

        // Stats
        result.AddRange(
            records.ActorValueInfos.Select(r => new FlatRecord(r.FormId, "AVIF", r.EditorId, r.FullName)));

        // Graphics & Audio
        result.AddRange(records.MenuIcons.Select(r => new FlatRecord(r.FormId, "MICN", r.EditorId, null)));
        result.AddRange(records.ImpactData.Select(r => new FlatRecord(r.FormId, "IPCT", r.EditorId, null)));
        result.AddRange(
            records.AudioLocationControllers.Select(r => new FlatRecord(r.FormId, "ALOC", r.EditorId, r.FullName)));
        result.AddRange(records.Sounds.Select(r => new FlatRecord(r.FormId, "SOUN", r.EditorId, r.FileName)));
        result.AddRange(records.MusicTypes.Select(r => new FlatRecord(r.FormId, "MUSC", r.EditorId, r.FileName)));
        result.AddRange(records.TextureSets.Select(r => new FlatRecord(r.FormId, "TXST", r.EditorId, null)));
        result.AddRange(records.MaterialSwaps.Select(r => new FlatRecord(r.FormId, "MSWP", r.EditorId, null)));
        result.AddRange(records.LandTextures.Select(r => new FlatRecord(r.FormId, "LTEX", r.EditorId, null)));
        result.AddRange(records.Grasses.Select(r => new FlatRecord(r.FormId, "GRAS", r.EditorId, null)));
        result.AddRange(records.Water.Select(r => new FlatRecord(r.FormId, "WATR", r.EditorId, r.FullName)));
        result.AddRange(records.BodyPartData.Select(r => new FlatRecord(r.FormId, "BPTD", r.EditorId, null)));
        result.AddRange(
            records.LightingTemplates.Select(r => new FlatRecord(r.FormId, "LGTM", r.EditorId, null)));
        result.AddRange(records.Weather.Select(r => new FlatRecord(r.FormId, "WTHR", r.EditorId, null)));
        result.AddRange(records.Climate.Select(r => new FlatRecord(r.FormId, "CLMT", r.EditorId, null)));
        result.AddRange(records.ImageSpaces.Select(r => new FlatRecord(r.FormId, "IMGS", r.EditorId, null)));
        result.AddRange(
            records.ImageSpaceModifiers.Select(r => new FlatRecord(r.FormId, "IMAD", r.EditorId, null)));

        // Typed collections that used to live in GenericRecords. Without these rows
        // `btool list -t PWAT` / `-t TREE` silently return nothing.
        result.AddRange(
            records.PlaceableWaters.Select(r => new FlatRecord(r.FormId, "PWAT", r.EditorId, null)));
        result.AddRange(records.Trees.Select(r => new FlatRecord(r.FormId, "TREE", r.EditorId, null)));

        // Generic
        result.AddRange(
            records.GenericRecords.Select(r => new FlatRecord(r.FormId, r.RecordType, r.EditorId, r.FullName)));

        // On the schema-bridge path (non-FNV/FO3 games) world records exist BOTH as typed viewer
        // collections (Worldspaces/Cells, kept for the 3D viewer) and as schema GenericRecords, so the
        // same (Type, FormId) printed twice. Collapse duplicates, preferring the row that resolved a
        // display name / EditorID.
        return result
            .GroupBy(r => (r.Type, r.FormId))
            .Select(g => g
                .OrderByDescending(r => r.DisplayName is not null)
                .ThenByDescending(r => r.EditorId is not null)
                .First())
            .OrderBy(r => r.Type).ThenBy(r => r.FormId).ToList();
    }

    internal record FlatRecord(uint FormId, string Type, string? EditorId, string? DisplayName);
}
