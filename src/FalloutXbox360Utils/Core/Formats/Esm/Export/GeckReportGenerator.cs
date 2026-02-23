using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports from semantic reconstruction results.
///     Delegates domain-specific report generation to dedicated writer classes.
///     Utility/formatting helpers live in <see cref="GeckReportHelpers" />.
/// </summary>
public static class GeckReportGenerator
{
    /// <summary>
    ///     Generate a complete report from semantic reconstruction results.
    /// </summary>
    public static string Generate(RecordCollection result,
        StringPoolSummary? stringPool = null,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        resolver ??= result.CreateResolver();

        // Header
        GeckReportHelpers.AppendHeader(sb, "ESM Memory Dump Semantic Reconstruction Report");
        sb.AppendLine();
        GeckReportHelpers.AppendSummary(sb, result);
        sb.AppendLine();

        // Characters
        if (result.Npcs.Count > 0)
        {
            GeckActorWriter.AppendNpcsSection(sb, result.Npcs, resolver, result.Races);
        }

        if (result.Creatures.Count > 0)
        {
            GeckActorWriter.AppendCreaturesSection(sb, result.Creatures);
        }

        if (result.Races.Count > 0)
        {
            GeckActorWriter.AppendRacesSection(sb, result.Races, resolver);
        }

        if (result.Factions.Count > 0)
        {
            GeckFactionWriter.AppendFactionsSection(sb, result.Factions, resolver);
        }

        // Quests and Dialogue
        if (result.Quests.Count > 0)
        {
            GeckDialogueWriter.AppendQuestsSection(sb, result.Quests, resolver);
        }

        if (result.DialogTopics.Count > 0)
        {
            GeckDialogueWriter.AppendDialogTopicsSection(sb, result.DialogTopics, resolver);
        }

        if (result.Notes.Count > 0)
        {
            GeckDialogueWriter.AppendNotesSection(sb, result.Notes);
        }

        if (result.Books.Count > 0)
        {
            GeckDialogueWriter.AppendBooksSection(sb, result.Books);
        }

        if (result.Terminals.Count > 0)
        {
            GeckDialogueWriter.AppendTerminalsSection(sb, result.Terminals);
        }

        if (result.Dialogues.Count > 0)
        {
            GeckDialogueWriter.AppendDialogueSection(sb, result.Dialogues, resolver);
        }

        // Scripts
        if (result.Scripts.Count > 0)
        {
            GeckScriptWriter.AppendScriptsSection(sb, result.Scripts, resolver);
        }

        // Items
        if (result.Weapons.Count > 0)
        {
            GeckItemWriter.AppendWeaponsSection(sb, result.Weapons, resolver);
        }

        if (result.Armor.Count > 0)
        {
            GeckItemWriter.AppendArmorSection(sb, result.Armor);
        }

        if (result.Ammo.Count > 0)
        {
            GeckItemWriter.AppendAmmoSection(sb, result.Ammo, resolver);
        }

        if (result.Consumables.Count > 0)
        {
            GeckItemWriter.AppendConsumablesSection(sb, result.Consumables, resolver);
        }

        if (result.MiscItems.Count > 0)
        {
            GeckItemWriter.AppendMiscItemsSection(sb, result.MiscItems);
        }

        if (result.Keys.Count > 0)
        {
            GeckItemWriter.AppendKeysSection(sb, result.Keys);
        }

        if (result.Containers.Count > 0)
        {
            GeckItemWriter.AppendContainersSection(sb, result.Containers, resolver);
        }

        // Abilities
        if (result.Perks.Count > 0)
        {
            GeckEffectsWriter.AppendPerksSection(sb, result.Perks, resolver);
        }

        if (result.Spells.Count > 0)
        {
            GeckEffectsWriter.AppendSpellsSection(sb, result.Spells, resolver);
        }

        // World
        if (result.Cells.Count > 0)
        {
            GeckWorldWriter.AppendCellsSection(sb, result.Cells, resolver);
        }

        if (result.Worldspaces.Count > 0)
        {
            GeckWorldWriter.AppendWorldspacesSection(sb, result.Worldspaces, resolver);
        }

        // String pool data from runtime memory
        if (stringPool != null)
        {
            GeckMiscWriter.AppendStringPoolSection(sb, stringPool);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Generate all reports from the given data sources.
    ///     Returns a dictionary mapping filename to content.
    ///     Produces identical output for CLI and GUI callers.
    /// </summary>
    public static Dictionary<string, string> GenerateAllReports(ReportDataSources sources)
    {
        var result = sources.Records;
        var files = new Dictionary<string, string>();
        var resolver = sources.Resolver;

        // Summary file
        var summarySb = new StringBuilder();
        GeckReportHelpers.AppendHeader(summarySb, "ESM Memory Dump - Summary");
        summarySb.AppendLine();
        GeckReportHelpers.AppendSummary(summarySb, result);

        if (result.UnreconstructedTypeCounts.Count > 0)
        {
            summarySb.AppendLine();
            summarySb.AppendLine("Other Detected Records (not fully reconstructed):");
            foreach (var (recordType, count) in result.UnreconstructedTypeCounts.OrderByDescending(x => x.Value))
            {
                summarySb.AppendLine($"  {recordType,-8} {count,6:N0}");
            }
        }

        files["summary.txt"] = summarySb.ToString();

        // Characters
        if (result.Npcs.Count > 0)
        {
            files["npcs.csv"] = CsvActorWriter.GenerateNpcsCsv(result.Npcs, resolver);
            files["npc_report.txt"] = GeckActorDetailWriter.GenerateNpcReport(result.Npcs, resolver, result.Races);
        }

        if (result.Creatures.Count > 0)
        {
            files["creatures.csv"] = CsvActorWriter.GenerateCreaturesCsv(result.Creatures, resolver);
            files["creature_report.txt"] = GeckActorWriter.GenerateCreaturesReport(result.Creatures, resolver);
        }

        if (result.Races.Count > 0)
        {
            files["races.csv"] = CsvActorWriter.GenerateRacesCsv(result.Races, resolver);
            files["race_report.txt"] = GeckActorWriter.GenerateRacesReport(result.Races, resolver);
        }

        if (result.Factions.Count > 0)
        {
            files["factions.csv"] = CsvActorWriter.GenerateFactionsCsv(result.Factions, resolver);
            files["faction_report.txt"] = GeckFactionWriter.GenerateFactionsReport(result.Factions, resolver);
        }

        // Quests and Dialogue
        if (result.Quests.Count > 0)
        {
            files["quests.csv"] = CsvMiscWriter.GenerateQuestsCsv(result.Quests, resolver);
            files["quest_report.txt"] = GeckDialogueWriter.GenerateQuestsReport(result.Quests, resolver);
        }

        if (result.DialogTopics.Count > 0)
        {
            files["dialog_topics.csv"] = CsvMiscWriter.GenerateDialogTopicsCsv(result.DialogTopics, resolver);
            files["dialog_topic_report.txt"] =
                GeckDialogueWriter.GenerateDialogTopicsReport(result.DialogTopics, resolver);
        }

        if (result.Notes.Count > 0)
        {
            files["notes.csv"] = CsvSupplementalWriter.GenerateNotesCsv(result.Notes);
            files["note_report.txt"] = GeckDialogueWriter.GenerateNotesReport(result.Notes);
        }

        if (result.Books.Count > 0)
        {
            files["books.csv"] = CsvItemWriter.GenerateBooksCsv(result.Books);
            files["book_report.txt"] = GeckDialogueWriter.GenerateBooksReport(result.Books);
        }

        if (result.Terminals.Count > 0)
        {
            files["terminals.csv"] = CsvSupplementalWriter.GenerateTerminalsCsv(result.Terminals, resolver);
            files["terminal_report.txt"] = GeckDialogueWriter.GenerateTerminalsReport(result.Terminals);
        }

        if (result.Dialogues.Count > 0)
        {
            files["dialogue.csv"] = CsvMiscWriter.GenerateDialogueCsv(result.Dialogues, resolver);
            files["dialogue_report.txt"] = GeckDialogueWriter.GenerateDialogueReport(result.Dialogues, resolver);
        }

        if (result.Scripts.Count > 0)
        {
            files["script_report.txt"] = GeckScriptWriter.GenerateScriptsReport(result.Scripts, resolver);
        }

        if (result.DialogueTree != null)
        {
            files["dialogue_tree.txt"] = GeckDialogueWriter.GenerateDialogueTreeReport(result.DialogueTree, resolver);
        }

        // Items
        if (result.Weapons.Count > 0)
        {
            files["weapons.csv"] = CsvItemWriter.GenerateWeaponsCsv(result.Weapons, resolver);
            files["weapon_report.txt"] = GeckItemDetailWriter.GenerateWeaponReport(result.Weapons, resolver);
        }

        if (result.Armor.Count > 0)
        {
            files["armor.csv"] = CsvItemWriter.GenerateArmorCsv(result.Armor);
            files["armor_report.txt"] = GeckItemWriter.GenerateArmorReport(result.Armor);
        }

        if (result.Ammo.Count > 0)
        {
            files["ammo.csv"] = CsvItemWriter.GenerateAmmoCsv(result.Ammo, resolver);
            files["ammo_report.txt"] = GeckItemWriter.GenerateAmmoReport(result.Ammo, resolver);
        }

        if (result.Consumables.Count > 0)
        {
            files["consumables.csv"] = CsvItemWriter.GenerateConsumablesCsv(result.Consumables, resolver);
            files["consumable_report.txt"] = GeckItemWriter.GenerateConsumablesReport(result.Consumables, resolver);
        }

        if (result.MiscItems.Count > 0)
        {
            files["misc_items.csv"] = CsvItemWriter.GenerateMiscItemsCsv(result.MiscItems);
            files["misc_item_report.txt"] = GeckItemWriter.GenerateMiscItemsReport(result.MiscItems);
        }

        if (result.Keys.Count > 0)
        {
            files["keys.csv"] = CsvItemWriter.GenerateKeysCsv(result.Keys);
            files["key_report.txt"] = GeckItemWriter.GenerateKeysReport(result.Keys);
        }

        if (result.Containers.Count > 0)
        {
            files["containers.csv"] = CsvItemWriter.GenerateContainersCsv(result.Containers, resolver);
            files["container_report.txt"] = GeckItemDetailWriter.GenerateContainerReport(result.Containers, resolver);
        }

        // Abilities
        if (result.Perks.Count > 0)
        {
            files["perks.csv"] = CsvMiscWriter.GeneratePerksCsv(result.Perks, resolver);
            files["perk_report.txt"] = GeckEffectsWriter.GeneratePerksReport(result.Perks, resolver);
        }

        if (result.Spells.Count > 0)
        {
            files["spells.csv"] = CsvMiscWriter.GenerateSpellsCsv(result.Spells, resolver);
            files["spell_report.txt"] = GeckEffectsWriter.GenerateSpellsReport(result.Spells, resolver);
        }

        // World
        if (result.Cells.Count > 0)
        {
            files["cells.csv"] = CsvMiscWriter.GenerateCellsCsv(result.Cells, resolver);
            files["cell_report.txt"] = GeckWorldWriter.GenerateCellsReport(result.Cells, resolver);
        }

        if (result.Worldspaces.Count > 0)
        {
            files["worldspaces.csv"] = CsvMiscWriter.GenerateWorldspacesCsv(result.Worldspaces, resolver);
            files["worldspace_report.txt"] = GeckWorldWriter.GenerateWorldspacesReport(result.Worldspaces, resolver);
        }

        if (result.MapMarkers.Count > 0)
        {
            files["map_markers.csv"] = CsvSupplementalWriter.GenerateMapMarkersCsv(result.MapMarkers, resolver);
            files["map_marker_report.txt"] = GeckWorldWriter.GenerateMapMarkersReport(result.MapMarkers, resolver);
        }

        // Persistent objects
        if (result.Cells.Any(c => c.PlacedObjects.Any(o => o.IsPersistent)))
        {
            files["persistent_objects.csv"] =
                CsvSupplementalWriter.GeneratePersistentObjectsCsv(result.Cells, resolver);
            files["persistent_object_report.txt"] =
                GeckWorldWriter.GeneratePersistentObjectsReport(result.Cells, resolver);
        }

        if (result.LeveledLists.Count > 0)
        {
            files["leveled_lists.csv"] = CsvSupplementalWriter.GenerateLeveledListsCsv(result.LeveledLists, resolver);
            files["leveled_list_report.txt"] = GeckMiscWriter.GenerateLeveledListsReport(result.LeveledLists, resolver);
        }

        // Game Data
        if (result.GameSettings.Count > 0)
        {
            files["gamesettings.csv"] = CsvMiscWriter.GenerateGameSettingsCsv(result.GameSettings);
            files["gamesetting_report.txt"] = GeckMiscWriter.GenerateGameSettingsReport(result.GameSettings);
        }

        if (result.Globals.Count > 0)
        {
            files["globals.csv"] = CsvSupplementalWriter.GenerateGlobalsCsv(result.Globals);
            files["global_report.txt"] = GeckMiscWriter.GenerateGlobalsReport(result.Globals);
        }

        if (result.Enchantments.Count > 0)
        {
            files["enchantments.csv"] = CsvMiscWriter.GenerateEnchantmentsCsv(result.Enchantments, resolver);
            files["enchantment_report.txt"] =
                GeckEffectsWriter.GenerateEnchantmentsReport(result.Enchantments, resolver);
        }

        if (result.BaseEffects.Count > 0)
        {
            files["base_effects.csv"] = CsvMiscWriter.GenerateBaseEffectsCsv(result.BaseEffects);
            files["base_effect_report.txt"] = GeckEffectsWriter.GenerateBaseEffectsReport(result.BaseEffects, resolver);
        }

        if (result.WeaponMods.Count > 0)
        {
            files["weapon_mods.csv"] = CsvItemWriter.GenerateWeaponModsCsv(result.WeaponMods);
            files["weapon_mod_report.txt"] = GeckItemDetailWriter.GenerateWeaponModsReport(result.WeaponMods);
        }

        if (result.Recipes.Count > 0)
        {
            files["recipes.csv"] = CsvItemWriter.GenerateRecipesCsv(result.Recipes, resolver);
            files["recipe_report.txt"] = GeckItemDetailWriter.GenerateRecipesReport(result.Recipes, resolver);
        }

        if (result.Challenges.Count > 0)
        {
            files["challenges.csv"] = CsvMiscWriter.GenerateChallengesCsv(result.Challenges);
            files["challenge_report.txt"] = GeckFactionWriter.GenerateChallengesReport(result.Challenges, resolver);
        }

        if (result.Reputations.Count > 0)
        {
            files["reputations.csv"] = CsvSupplementalWriter.GenerateReputationsCsv(result.Reputations);
            files["reputation_report.txt"] = GeckFactionWriter.GenerateReputationsReport(result.Reputations);
        }

        if (result.Projectiles.Count > 0)
        {
            files["projectiles.csv"] = CsvSupplementalWriter.GenerateProjectilesCsv(result.Projectiles);
            files["projectile_report.txt"] = GeckWorldWriter.GenerateProjectilesReport(result.Projectiles, resolver);
        }

        if (result.Explosions.Count > 0)
        {
            files["explosions.csv"] = CsvMiscWriter.GenerateExplosionsCsv(result.Explosions);
            files["explosion_report.txt"] = GeckWorldWriter.GenerateExplosionsReport(result.Explosions, resolver);
        }

        if (result.Messages.Count > 0)
        {
            files["messages.csv"] = CsvSupplementalWriter.GenerateMessagesCsv(result.Messages);
            files["message_report.txt"] = GeckDialogueWriter.GenerateMessagesReport(result.Messages, resolver);
        }

        if (result.Classes.Count > 0)
        {
            files["classes.csv"] = CsvActorWriter.GenerateClassesCsv(result.Classes);
            files["class_report.txt"] = GeckActorWriter.GenerateClassesReport(result.Classes);
        }

        // Asset tree report
        if (sources.AssetStrings is { Count: > 0 })
        {
            files["assets.txt"] = GeckMiscWriter.GenerateAssetListReport(sources.AssetStrings);
        }

        // Asset CSV
        if (result.ModelPathIndex.Count > 0 || sources.AssetStrings is { Count: > 0 })
        {
            files["assets.csv"] = CsvSupplementalWriter.GenerateEnrichedAssetsCsv(result, sources.AssetStrings);
        }

        // Runtime EditorIDs report
        if (sources.RuntimeEditorIds is { Count: > 0 })
        {
            files["runtime_editorids.csv"] = GeckMiscWriter.GenerateRuntimeEditorIdsReport(sources.RuntimeEditorIds);
        }

        // String pool CSVs
        if (sources.StringPool != null)
        {
            foreach (var (filename, content) in CsvSupplementalWriter.GenerateStringPoolCsvs(sources.StringPool))
            {
                files[filename] = content;
            }
        }

        return files;
    }

}
