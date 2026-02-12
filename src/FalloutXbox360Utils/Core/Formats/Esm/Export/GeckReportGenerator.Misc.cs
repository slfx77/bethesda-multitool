using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

public static partial class GeckReportGenerator
{
    #region AssetList Methods

    /// <summary>
    ///     Generate a report of asset paths detected from runtime string pools.
    ///     Categorizes assets by type (models, textures, sounds, etc.).
    /// </summary>
    public static string GenerateAssetListReport(List<DetectedAssetString> assets)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, "Runtime Asset String Pool");
        sb.AppendLine();

        // Clean junk prefixes and filter invalid paths
        var validAssets = assets
            .Where(a => a.Path.Length > 0 && char.IsLetterOrDigit(a.Path[0]))
            .Select(a => a with { Path = CleanAssetPath(a.Path) })
            .ToList();

        sb.AppendLine($"Total Assets: {validAssets.Count:N0} (filtered from {assets.Count:N0})");
        sb.AppendLine();

        // Group by category, then build hierarchical path tree within each
        var byCategory = validAssets.GroupBy(a => a.Category)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key,
                g => g.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase).ToList());

        foreach (var (category, paths) in byCategory)
        {
            sb.AppendLine(new string('-', SeparatorWidth));
            sb.AppendLine($"  {category} ({paths.Count:N0})");
            sb.AppendLine(new string('-', SeparatorWidth));

            AppendPathTree(sb, paths, "  ");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion

    #region RuntimeEditorId Methods

    /// <summary>
    ///     Generate a report for runtime Editor IDs extracted via pointer following.
    ///     Lists Editor IDs with FormID associations obtained from TESForm objects.
    /// </summary>
    public static string GenerateRuntimeEditorIdsReport(List<RuntimeEditorIdEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("EditorID,FormID,FormType,DisplayName,DialogueLine,TesFormOffset");

        foreach (var entry in entries.OrderBy(e => e.EditorId))
        {
            var formId = entry.FormId != 0 ? $"{entry.FormId:X8}" : "";
            var formType = entry.FormId != 0 ? $"{entry.FormType:D3}" : "";
            var displayName = CsvEscape(entry.DisplayName);
            var dialogueLine = CsvEscape(entry.DialogueLine);
            var offset = entry.TesFormOffset?.ToString() ?? "";

            sb.AppendLine($"{CsvEscape(entry.EditorId)},{formId},{formType},{displayName},{dialogueLine},{offset}");
        }

        return sb.ToString();
    }

    #endregion

    #region StringPool Methods

    private static void AppendStringPoolSection(StringBuilder sb, StringPoolSummary sp)
    {
        sb.AppendLine();
        AppendHeader(sb, "String Pool Data (from Runtime Memory)");
        sb.AppendLine();
        sb.AppendLine($"  Total strings:     {sp.TotalStrings,10:N0} ({sp.UniqueStrings:N0} unique)");
        sb.AppendLine($"  Across:            {sp.RegionCount,10:N0} regions ({FormatPoolSize(sp.TotalBytes)})");
        sb.AppendLine();
        sb.AppendLine($"  File paths:        {sp.FilePaths,10:N0}");

        if (sp.MatchedToCarvedFiles > 0)
        {
            sb.AppendLine(
                $"    Matched to carved: {sp.MatchedToCarvedFiles:N0}  |  Unmatched: {sp.UnmatchedFilePaths:N0}");
        }

        sb.AppendLine($"  EditorIDs:         {sp.EditorIds,10:N0}");
        sb.AppendLine($"  Dialogue lines:    {sp.DialogueLines,10:N0}");
        sb.AppendLine($"  Game settings:     {sp.GameSettings,10:N0}");
        sb.AppendLine($"  Other:             {sp.Other,10:N0}");

        if (sp.SampleDialogue.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Sample dialogue/descriptions (from runtime memory, not ESM records):");
            foreach (var line in sp.SampleDialogue.Take(10))
            {
                var display = line.Length > 120 ? line[..117] + "..." : line;
                sb.AppendLine($"    \"{display}\"");
            }
        }

        sb.AppendLine();
        sb.AppendLine("  Note: These strings come from runtime memory pools, not ESM records.");
        sb.AppendLine("  Includes perk descriptions, skill descriptions, loading screen text,");
        sb.AppendLine("  and other game text not found in the dump's ESM data.");
        sb.AppendLine("  See string_pool_*.csv files for full datasets.");
    }

    #endregion

    #region Split Report Methods

    /// <summary>
    ///     Generate all reports from the given data sources.
    ///     Returns a dictionary mapping filename to content.
    ///     Produces identical output for CLI and GUI callers.
    /// </summary>
    public static Dictionary<string, string> GenerateAllReports(ReportDataSources sources)
    {
        var result = sources.Records;
        var files = new Dictionary<string, string>();
        var lookup = sources.FormIdMap ?? result.FormIdToEditorId;
        var displayNameLookup = result.FormIdToDisplayName;

        // Summary file
        var summarySb = new StringBuilder();
        AppendHeader(summarySb, "ESM Memory Dump - Summary");
        summarySb.AppendLine();
        AppendSummary(summarySb, result);

        // Add "Other Records" summary if there are unreconstructed record types
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

        // Individual record type CSV files - only create if there's data

        // Characters
        if (result.Npcs.Count > 0)
        {
            files["npcs.csv"] = CsvReportGenerator.GenerateNpcsCsv(result.Npcs, lookup);
            files["npc_report.txt"] = GenerateNpcReport(result.Npcs, lookup, displayNameLookup);
        }

        if (result.Creatures.Count > 0)
        {
            files["creatures.csv"] = CsvReportGenerator.GenerateCreaturesCsv(result.Creatures, lookup);
            files["creature_report.txt"] = GenerateCreaturesReport(result.Creatures, lookup);
        }

        if (result.Races.Count > 0)
        {
            files["races.csv"] = CsvReportGenerator.GenerateRacesCsv(result.Races, lookup);
            files["race_report.txt"] = GenerateRacesReport(result.Races, lookup);
        }

        if (result.Factions.Count > 0)
        {
            files["factions.csv"] = CsvReportGenerator.GenerateFactionsCsv(result.Factions, lookup);
            files["faction_report.txt"] = GenerateFactionsReport(result.Factions, lookup);
        }

        // Quests and Dialogue
        if (result.Quests.Count > 0)
        {
            files["quests.csv"] = CsvReportGenerator.GenerateQuestsCsv(result.Quests, lookup);
            files["quest_report.txt"] = GenerateQuestsReport(result.Quests, lookup);
        }

        if (result.DialogTopics.Count > 0)
        {
            files["dialog_topics.csv"] = CsvReportGenerator.GenerateDialogTopicsCsv(result.DialogTopics, lookup);
            files["dialog_topic_report.txt"] = GenerateDialogTopicsReport(result.DialogTopics, lookup);
        }

        if (result.Notes.Count > 0)
        {
            files["notes.csv"] = CsvReportGenerator.GenerateNotesCsv(result.Notes);
            files["note_report.txt"] = GenerateNotesReport(result.Notes);
        }

        if (result.Books.Count > 0)
        {
            files["books.csv"] = CsvReportGenerator.GenerateBooksCsv(result.Books);
            files["book_report.txt"] = GenerateBooksReport(result.Books);
        }

        if (result.Terminals.Count > 0)
        {
            files["terminals.csv"] = CsvReportGenerator.GenerateTerminalsCsv(result.Terminals, lookup);
            files["terminal_report.txt"] = GenerateTerminalsReport(result.Terminals, lookup);
        }

        if (result.Dialogues.Count > 0)
        {
            files["dialogue.csv"] = CsvReportGenerator.GenerateDialogueCsv(result.Dialogues, lookup);
            files["dialogue_report.txt"] = GenerateDialogueReport(result.Dialogues, lookup);
        }

        if (result.Scripts.Count > 0)
        {
            files["script_report.txt"] = GenerateScriptsReport(result.Scripts, lookup);
        }

        if (result.DialogueTree != null)
        {
            files["dialogue_tree.txt"] = GenerateDialogueTreeReport(result.DialogueTree, lookup, displayNameLookup);
        }

        // Items
        if (result.Weapons.Count > 0)
        {
            files["weapons.csv"] = CsvReportGenerator.GenerateWeaponsCsv(result.Weapons, lookup);
            files["weapon_report.txt"] = GenerateWeaponReport(result.Weapons, lookup, displayNameLookup);
        }

        if (result.Armor.Count > 0)
        {
            files["armor.csv"] = CsvReportGenerator.GenerateArmorCsv(result.Armor);
            files["armor_report.txt"] = GenerateArmorReport(result.Armor);
        }

        if (result.Ammo.Count > 0)
        {
            files["ammo.csv"] = CsvReportGenerator.GenerateAmmoCsv(result.Ammo, lookup);
            files["ammo_report.txt"] = GenerateAmmoReport(result.Ammo, lookup);
        }

        if (result.Consumables.Count > 0)
        {
            files["consumables.csv"] = CsvReportGenerator.GenerateConsumablesCsv(result.Consumables, lookup);
            files["consumable_report.txt"] = GenerateConsumablesReport(result.Consumables, lookup);
        }

        if (result.MiscItems.Count > 0)
        {
            files["misc_items.csv"] = CsvReportGenerator.GenerateMiscItemsCsv(result.MiscItems);
            files["misc_item_report.txt"] = GenerateMiscItemsReport(result.MiscItems);
        }

        if (result.Keys.Count > 0)
        {
            files["keys.csv"] = CsvReportGenerator.GenerateKeysCsv(result.Keys);
            files["key_report.txt"] = GenerateKeysReport(result.Keys);
        }

        if (result.Containers.Count > 0)
        {
            files["containers.csv"] = CsvReportGenerator.GenerateContainersCsv(result.Containers, lookup);
            files["container_report.txt"] = GenerateContainerReport(result.Containers, lookup, displayNameLookup);
        }

        // Abilities
        if (result.Perks.Count > 0)
        {
            files["perks.csv"] = CsvReportGenerator.GeneratePerksCsv(result.Perks, lookup);
            files["perk_report.txt"] = GeneratePerksReport(result.Perks, lookup);
        }

        if (result.Spells.Count > 0)
        {
            files["spells.csv"] = CsvReportGenerator.GenerateSpellsCsv(result.Spells, lookup);
            files["spell_report.txt"] = GenerateSpellsReport(result.Spells, lookup);
        }

        // World
        if (result.Cells.Count > 0)
        {
            files["cells.csv"] = CsvReportGenerator.GenerateCellsCsv(result.Cells, lookup);
            files["cell_report.txt"] = GenerateCellsReport(result.Cells, lookup);
        }

        if (result.Worldspaces.Count > 0)
        {
            files["worldspaces.csv"] = CsvReportGenerator.GenerateWorldspacesCsv(result.Worldspaces, lookup);
            files["worldspace_report.txt"] = GenerateWorldspacesReport(result.Worldspaces, lookup);
        }

        if (result.MapMarkers.Count > 0)
        {
            files["map_markers.csv"] = CsvReportGenerator.GenerateMapMarkersCsv(result.MapMarkers, lookup);
            files["map_marker_report.txt"] = GenerateMapMarkersReport(result.MapMarkers, lookup);
        }

        if (result.LeveledLists.Count > 0)
        {
            files["leveled_lists.csv"] = CsvReportGenerator.GenerateLeveledListsCsv(result.LeveledLists, lookup);
            files["leveled_list_report.txt"] = GenerateLeveledListsReport(result.LeveledLists, lookup);
        }

        // Game Data
        if (result.GameSettings.Count > 0)
        {
            files["gamesettings.csv"] = CsvReportGenerator.GenerateGameSettingsCsv(result.GameSettings);
            files["gamesetting_report.txt"] = GenerateGameSettingsReport(result.GameSettings);
        }

        if (result.Globals.Count > 0)
        {
            files["globals.csv"] = CsvReportGenerator.GenerateGlobalsCsv(result.Globals);
            files["global_report.txt"] = GenerateGlobalsReport(result.Globals);
        }

        if (result.Enchantments.Count > 0)
        {
            files["enchantments.csv"] = CsvReportGenerator.GenerateEnchantmentsCsv(result.Enchantments, lookup);
            files["enchantment_report.txt"] = GenerateEnchantmentsReport(result.Enchantments, lookup);
        }

        if (result.BaseEffects.Count > 0)
        {
            files["base_effects.csv"] = CsvReportGenerator.GenerateBaseEffectsCsv(result.BaseEffects);
            files["base_effect_report.txt"] = GenerateBaseEffectsReport(result.BaseEffects, lookup);
        }

        if (result.WeaponMods.Count > 0)
        {
            files["weapon_mods.csv"] = CsvReportGenerator.GenerateWeaponModsCsv(result.WeaponMods);
            files["weapon_mod_report.txt"] = GenerateWeaponModsReport(result.WeaponMods);
        }

        if (result.Recipes.Count > 0)
        {
            files["recipes.csv"] = CsvReportGenerator.GenerateRecipesCsv(result.Recipes, lookup);
            files["recipe_report.txt"] = GenerateRecipesReport(result.Recipes, lookup);
        }

        if (result.Challenges.Count > 0)
        {
            files["challenges.csv"] = CsvReportGenerator.GenerateChallengesCsv(result.Challenges);
            files["challenge_report.txt"] = GenerateChallengesReport(result.Challenges, lookup);
        }

        if (result.Reputations.Count > 0)
        {
            files["reputations.csv"] = CsvReportGenerator.GenerateReputationsCsv(result.Reputations);
            files["reputation_report.txt"] = GenerateReputationsReport(result.Reputations);
        }

        if (result.Projectiles.Count > 0)
        {
            files["projectiles.csv"] = CsvReportGenerator.GenerateProjectilesCsv(result.Projectiles);
            files["projectile_report.txt"] = GenerateProjectilesReport(result.Projectiles, lookup);
        }

        if (result.Explosions.Count > 0)
        {
            files["explosions.csv"] = CsvReportGenerator.GenerateExplosionsCsv(result.Explosions);
            files["explosion_report.txt"] = GenerateExplosionsReport(result.Explosions, lookup);
        }

        if (result.Messages.Count > 0)
        {
            files["messages.csv"] = CsvReportGenerator.GenerateMessagesCsv(result.Messages);
            files["message_report.txt"] = GenerateMessagesReport(result.Messages, lookup);
        }

        if (result.Classes.Count > 0)
        {
            files["classes.csv"] = CsvReportGenerator.GenerateClassesCsv(result.Classes);
            files["class_report.txt"] = GenerateClassesReport(result.Classes);
        }

        // Asset strings report (from runtime string pools)
        if (sources.AssetStrings is { Count: > 0 })
        {
            files["assets.txt"] = GenerateAssetListReport(sources.AssetStrings);
        }

        // Runtime EditorIDs report (from pointer-following extraction)
        if (sources.RuntimeEditorIds is { Count: > 0 })
        {
            files["runtime_editorids.csv"] = GenerateRuntimeEditorIdsReport(sources.RuntimeEditorIds);
        }

        // String pool CSVs (dialogue, file paths, EditorIDs, game settings from runtime memory)
        if (sources.StringPool != null)
        {
            foreach (var (filename, content) in CsvReportGenerator.GenerateStringPoolCsvs(sources.StringPool))
            {
                files[filename] = content;
            }
        }

        return files;
    }

    #endregion

    #region Global Methods

    private static void AppendGlobalsSection(StringBuilder sb, List<GlobalRecord> globals)
    {
        AppendSectionHeader(sb, $"Global Variables ({globals.Count})");
        sb.AppendLine();

        var byType = globals.GroupBy(g => g.TypeName).OrderBy(g => g.Key).ToList();
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key}: {group.Count():N0}");
        }

        sb.AppendLine();

        foreach (var group in byType)
        {
            sb.AppendLine($"--- {group.Key} Globals ---");
            foreach (var g in group.OrderBy(x => x.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  {g.EditorId ?? "(none)",-50} = {g.DisplayValue,12}  [{FormatFormId(g.FormId)}]");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateGlobalsReport(List<GlobalRecord> globals)
    {
        var sb = new StringBuilder();
        AppendGlobalsSection(sb, globals);
        return sb.ToString();
    }

    #endregion

    #region GameSetting Methods

    private static void AppendGameSettingsSection(StringBuilder sb, List<GameSettingRecord> settings)
    {
        AppendSectionHeader(sb, $"Game Settings ({settings.Count})");

        sb.AppendLine();
        sb.AppendLine($"Total Game Settings: {settings.Count:N0}");

        // Group by type
        var floatSettings = settings.Where(s => s.ValueType == GameSettingType.Float).ToList();
        var intSettings = settings.Where(s => s.ValueType == GameSettingType.Integer).ToList();
        var boolSettings = settings.Where(s => s.ValueType == GameSettingType.Boolean).ToList();
        var stringSettings = settings.Where(s => s.ValueType == GameSettingType.String).ToList();

        sb.AppendLine($"  Float:   {floatSettings.Count:N0}");
        sb.AppendLine($"  Integer: {intSettings.Count:N0}");
        sb.AppendLine($"  Boolean: {boolSettings.Count:N0}");
        sb.AppendLine($"  String:  {stringSettings.Count:N0}");
        sb.AppendLine();

        // Float settings
        if (floatSettings.Count > 0)
        {
            sb.AppendLine("--- Float Settings ---");
            foreach (var setting in floatSettings.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"  {setting.EditorId,-60} = {setting.DisplayValue,12}  [{FormatFormId(setting.FormId)}]");
            }

            sb.AppendLine();
        }

        // Integer settings
        if (intSettings.Count > 0)
        {
            sb.AppendLine("--- Integer Settings ---");
            foreach (var setting in intSettings.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"  {setting.EditorId,-60} = {setting.DisplayValue,12}  [{FormatFormId(setting.FormId)}]");
            }

            sb.AppendLine();
        }

        // Boolean settings
        if (boolSettings.Count > 0)
        {
            sb.AppendLine("--- Boolean Settings ---");
            foreach (var setting in boolSettings.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"  {setting.EditorId,-60} = {setting.DisplayValue,12}  [{FormatFormId(setting.FormId)}]");
            }

            sb.AppendLine();
        }

        // String settings
        if (stringSettings.Count > 0)
        {
            sb.AppendLine("--- String Settings ---");
            foreach (var setting in stringSettings.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                var displayValue = setting.StringValue?.Length > 50
                    ? setting.StringValue[..47] + "..."
                    : setting.StringValue;
                sb.AppendLine($"  {setting.EditorId,-60} = \"{displayValue}\"  [{FormatFormId(setting.FormId)}]");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateGameSettingsReport(List<GameSettingRecord> settings)
    {
        var sb = new StringBuilder();
        AppendGameSettingsSection(sb, settings);
        return sb.ToString();
    }

    #endregion

    #region LeveledList Methods

    private static void AppendLeveledListsSection(StringBuilder sb, List<LeveledListRecord> lists,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Leveled Lists ({lists.Count})");
        sb.AppendLine();

        var byType = lists.GroupBy(l => l.ListType).OrderBy(g => g.Key).ToList();
        sb.AppendLine($"Total Leveled Lists: {lists.Count:N0}");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key}: {group.Count():N0}");
        }

        var totalEntries = lists.Sum(l => l.Entries.Count);
        sb.AppendLine(
            $"  Total Entries: {totalEntries:N0} (avg {(lists.Count > 0 ? totalEntries / (double)lists.Count : 0):F1} per list)");
        sb.AppendLine();

        foreach (var list in lists.OrderBy(l => l.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  LIST: {list.EditorId ?? "(none)"}");
            sb.AppendLine($"  FormID:      {FormatFormId(list.FormId)}");
            sb.AppendLine($"  Type:        {list.ListType}");
            sb.AppendLine($"  Chance None: {list.ChanceNone}%");
            if (!string.IsNullOrEmpty(list.FlagsDescription))
            {
                sb.AppendLine($"  Flags:       {list.FlagsDescription}");
            }

            if (list.GlobalFormId is > 0)
            {
                sb.AppendLine($"  Global:      {FormatFormIdWithName(list.GlobalFormId.Value, lookup)}");
            }

            if (list.Entries.Count > 0)
            {
                sb.AppendLine(
                    $"  \u2500\u2500 Entries ({list.Entries.Count}) {new string('\u2500', 80 - 18 - list.Entries.Count.ToString().Length)}");
                sb.AppendLine($"  {"Level",7}  {"Item",-50} {"Count",6}");
                sb.AppendLine($"  {new string('\u2500', 67)}");
                foreach (var entry in list.Entries.OrderBy(e => e.Level))
                {
                    var itemName = entry.FormId != 0
                        ? FormatFormIdWithName(entry.FormId, lookup)
                        : "(none)";
                    sb.AppendLine($"  {entry.Level,7}  {Truncate(itemName, 50),-50} {entry.Count,6}");
                }
            }

            sb.AppendLine();
        }
    }

    public static string GenerateLeveledListsReport(List<LeveledListRecord> lists,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendLeveledListsSection(sb, lists, lookup ?? []);
        return sb.ToString();
    }

    #endregion
}
