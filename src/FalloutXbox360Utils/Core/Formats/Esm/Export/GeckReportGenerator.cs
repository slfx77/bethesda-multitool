using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports from semantic reconstruction results.
///     Delegates domain-specific report generation to dedicated writer classes.
/// </summary>
public static class GeckReportGenerator
{
    internal const int SeparatorWidth = 80;

    internal const char SeparatorChar = '=';

    internal static readonly HashSet<string> KnownAssetRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "meshes", "textures", "sound", "music", "interface", "menus",
        "architecture", "landscape", "characters", "creatures",
        "armor", "weapons", "clutter", "furniture", "effects",
        "animobjects", "trees", "vehicles", "pipboy3000", "gore",
        "dungeons", "scol", "mps", "projectiles", "ammo",
        "dlc01", "dlc02", "dlc03", "dlc04", "dlc05", "dlcanch",
        "nvdlc01", "nvdlc02", "nvdlc03", "nvdlc04",
        "sky", "water", "rocks", "grass", "plants", "lights",
        "markers", "activators", "static", "misc", "fx"
    };

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
        AppendHeader(sb, "ESM Memory Dump Semantic Reconstruction Report");
        sb.AppendLine();
        AppendSummary(sb, result);
        sb.AppendLine();

        // Characters
        if (result.Npcs.Count > 0)
        {
            GeckActorWriter.AppendNpcsSection(sb, result.Npcs, resolver);
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
        AppendHeader(summarySb, "ESM Memory Dump - Summary");
        summarySb.AppendLine();
        AppendSummary(summarySb, result);

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
            files["npc_report.txt"] = GeckActorWriter.GenerateNpcReport(result.Npcs, resolver);
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
            files["dialog_topic_report.txt"] = GeckDialogueWriter.GenerateDialogTopicsReport(result.DialogTopics, resolver);
        }

        if (result.Notes.Count > 0)
        {
            files["notes.csv"] = CsvMiscWriter.GenerateNotesCsv(result.Notes);
            files["note_report.txt"] = GeckDialogueWriter.GenerateNotesReport(result.Notes);
        }

        if (result.Books.Count > 0)
        {
            files["books.csv"] = CsvItemWriter.GenerateBooksCsv(result.Books);
            files["book_report.txt"] = GeckDialogueWriter.GenerateBooksReport(result.Books);
        }

        if (result.Terminals.Count > 0)
        {
            files["terminals.csv"] = CsvMiscWriter.GenerateTerminalsCsv(result.Terminals, resolver);
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
            files["weapon_report.txt"] = GeckItemWriter.GenerateWeaponReport(result.Weapons, resolver);
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
            files["container_report.txt"] = GeckItemWriter.GenerateContainerReport(result.Containers, resolver);
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
            files["map_markers.csv"] = CsvMiscWriter.GenerateMapMarkersCsv(result.MapMarkers, resolver);
            files["map_marker_report.txt"] = GeckWorldWriter.GenerateMapMarkersReport(result.MapMarkers, resolver);
        }

        if (result.LeveledLists.Count > 0)
        {
            files["leveled_lists.csv"] = CsvMiscWriter.GenerateLeveledListsCsv(result.LeveledLists, resolver);
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
            files["globals.csv"] = CsvMiscWriter.GenerateGlobalsCsv(result.Globals);
            files["global_report.txt"] = GeckMiscWriter.GenerateGlobalsReport(result.Globals);
        }

        if (result.Enchantments.Count > 0)
        {
            files["enchantments.csv"] = CsvMiscWriter.GenerateEnchantmentsCsv(result.Enchantments, resolver);
            files["enchantment_report.txt"] = GeckEffectsWriter.GenerateEnchantmentsReport(result.Enchantments, resolver);
        }

        if (result.BaseEffects.Count > 0)
        {
            files["base_effects.csv"] = CsvMiscWriter.GenerateBaseEffectsCsv(result.BaseEffects);
            files["base_effect_report.txt"] = GeckEffectsWriter.GenerateBaseEffectsReport(result.BaseEffects, resolver);
        }

        if (result.WeaponMods.Count > 0)
        {
            files["weapon_mods.csv"] = CsvItemWriter.GenerateWeaponModsCsv(result.WeaponMods);
            files["weapon_mod_report.txt"] = GeckItemWriter.GenerateWeaponModsReport(result.WeaponMods);
        }

        if (result.Recipes.Count > 0)
        {
            files["recipes.csv"] = CsvItemWriter.GenerateRecipesCsv(result.Recipes, resolver);
            files["recipe_report.txt"] = GeckItemWriter.GenerateRecipesReport(result.Recipes, resolver);
        }

        if (result.Challenges.Count > 0)
        {
            files["challenges.csv"] = CsvMiscWriter.GenerateChallengesCsv(result.Challenges);
            files["challenge_report.txt"] = GeckFactionWriter.GenerateChallengesReport(result.Challenges, resolver);
        }

        if (result.Reputations.Count > 0)
        {
            files["reputations.csv"] = CsvMiscWriter.GenerateReputationsCsv(result.Reputations);
            files["reputation_report.txt"] = GeckFactionWriter.GenerateReputationsReport(result.Reputations);
        }

        if (result.Projectiles.Count > 0)
        {
            files["projectiles.csv"] = CsvMiscWriter.GenerateProjectilesCsv(result.Projectiles);
            files["projectile_report.txt"] = GeckWorldWriter.GenerateProjectilesReport(result.Projectiles, resolver);
        }

        if (result.Explosions.Count > 0)
        {
            files["explosions.csv"] = CsvMiscWriter.GenerateExplosionsCsv(result.Explosions);
            files["explosion_report.txt"] = GeckWorldWriter.GenerateExplosionsReport(result.Explosions, resolver);
        }

        if (result.Messages.Count > 0)
        {
            files["messages.csv"] = CsvMiscWriter.GenerateMessagesCsv(result.Messages);
            files["message_report.txt"] = GeckDialogueWriter.GenerateMessagesReport(result.Messages, resolver);
        }

        if (result.Classes.Count > 0)
        {
            files["classes.csv"] = CsvActorWriter.GenerateClassesCsv(result.Classes);
            files["class_report.txt"] = GeckActorWriter.GenerateClassesReport(result.Classes);
        }

        // Asset tree report (human-readable directory tree grouped by category)
        if (sources.AssetStrings is { Count: > 0 })
        {
            files["assets.txt"] = GeckMiscWriter.GenerateAssetListReport(sources.AssetStrings);
        }

        // Asset CSV: FormID-based model paths merged with runtime string pool detections
        if (result.ModelPathIndex.Count > 0 || sources.AssetStrings is { Count: > 0 })
        {
            files["assets.csv"] = CsvMiscWriter.GenerateEnrichedAssetsCsv(result, sources.AssetStrings);
        }

        // Runtime EditorIDs report (from pointer-following extraction)
        if (sources.RuntimeEditorIds is { Count: > 0 })
        {
            files["runtime_editorids.csv"] = GeckMiscWriter.GenerateRuntimeEditorIdsReport(sources.RuntimeEditorIds);
        }

        // String pool CSVs (dialogue, file paths, EditorIDs, game settings from runtime memory)
        if (sources.StringPool != null)
        {
            foreach (var (filename, content) in CsvMiscWriter.GenerateStringPoolCsvs(sources.StringPool))
            {
                files[filename] = content;
            }
        }

        return files;
    }

    /// <summary>
    ///     Generate a report of asset paths detected from runtime string pools.
    /// </summary>
    public static string GenerateAssetListReport(List<DetectedAssetString> assets)
        => GeckMiscWriter.GenerateAssetListReport(assets);

    /// <summary>
    ///     Generate a report for runtime Editor IDs.
    /// </summary>
    public static string GenerateRuntimeEditorIdsReport(List<RuntimeEditorIdEntry> entries)
        => GeckMiscWriter.GenerateRuntimeEditorIdsReport(entries);

    /// <summary>
    ///     Generate a dialogue tree report.
    /// </summary>
    public static string GenerateDialogueTreeReport(
        DialogueTreeResult dialogueTree,
        FormIdResolver resolver)
        => GeckDialogueWriter.GenerateDialogueTreeReport(dialogueTree, resolver);

    /// <summary>
    ///     Tree node for hierarchical path grouping in asset reports.
    /// </summary>
    internal sealed class PathTreeNode
    {
        public Dictionary<string, PathTreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Files { get; } = [];
    }

    #region Helpers

    internal static string FormatFormId(uint formId)
    {
        return Fmt.FIdAlways(formId);
    }

    internal static string CsvEscape(string? value)
    {
        return Fmt.CsvEscape(value);
    }

    internal static void AppendHeader(StringBuilder sb, string title)
    {
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
        var padding = (SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
    }

    internal static void AppendSummary(StringBuilder sb, RecordCollection result)
    {
        sb.AppendLine("Summary:");
        sb.AppendLine($"  Total Records Processed:     {result.TotalRecordsProcessed:N0}");
        sb.AppendLine($"  Total Records Reconstructed: {result.TotalRecordsReconstructed:N0}");
        sb.AppendLine();
        sb.AppendLine("  Characters:");
        sb.AppendLine($"    NPCs:         {result.Npcs.Count,6:N0}");
        sb.AppendLine($"    Creatures:    {result.Creatures.Count,6:N0}");
        sb.AppendLine($"    Races:        {result.Races.Count,6:N0}");
        sb.AppendLine($"    Factions:     {result.Factions.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  Quests & Dialogue:");
        sb.AppendLine($"    Quests:       {result.Quests.Count,6:N0}");
        sb.AppendLine($"    Dial Topics:  {result.DialogTopics.Count,6:N0}");
        sb.AppendLine($"    Dialogue:     {result.Dialogues.Count,6:N0}");
        sb.AppendLine($"    Notes:        {result.Notes.Count,6:N0}");
        sb.AppendLine($"    Books:        {result.Books.Count,6:N0}");
        sb.AppendLine($"    Terminals:    {result.Terminals.Count,6:N0}");
        sb.AppendLine($"    Scripts:      {result.Scripts.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  Items:");
        sb.AppendLine($"    Weapons:      {result.Weapons.Count,6:N0}");
        sb.AppendLine($"    Armor:        {result.Armor.Count,6:N0}");
        sb.AppendLine($"    Ammo:         {result.Ammo.Count,6:N0}");
        sb.AppendLine($"    Consumables:  {result.Consumables.Count,6:N0}");
        sb.AppendLine($"    Misc Items:   {result.MiscItems.Count,6:N0}");
        sb.AppendLine($"    Keys:         {result.Keys.Count,6:N0}");
        sb.AppendLine($"    Containers:   {result.Containers.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  Abilities:");
        sb.AppendLine($"    Perks:        {result.Perks.Count,6:N0}");
        sb.AppendLine($"    Spells:       {result.Spells.Count,6:N0}");
        sb.AppendLine($"    Enchantments: {result.Enchantments.Count,6:N0}");
        sb.AppendLine($"    Base Effects: {result.BaseEffects.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  World:");
        sb.AppendLine($"    Cells:        {result.Cells.Count,6:N0}");
        sb.AppendLine($"    Worldspaces:  {result.Worldspaces.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  Gameplay:");
        sb.AppendLine($"    Globals:      {result.Globals.Count,6:N0}");
        sb.AppendLine($"    Classes:      {result.Classes.Count,6:N0}");
        sb.AppendLine($"    Challenges:   {result.Challenges.Count,6:N0}");
        sb.AppendLine($"    Reputations:  {result.Reputations.Count,6:N0}");
        sb.AppendLine($"    Messages:     {result.Messages.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  Crafting & Mods:");
        sb.AppendLine($"    Weapon Mods:  {result.WeaponMods.Count,6:N0}");
        sb.AppendLine($"    Recipes:      {result.Recipes.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  Combat:");
        sb.AppendLine($"    Projectiles:  {result.Projectiles.Count,6:N0}");
        sb.AppendLine($"    Explosions:   {result.Explosions.Count,6:N0}");
    }

    internal static void AppendSectionHeader(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine(new string('-', SeparatorWidth));
        sb.AppendLine($"  {title}");
        sb.AppendLine(new string('-', SeparatorWidth));
    }

    internal static void AppendRecordHeader(StringBuilder sb, string recordType, string? editorId)
    {
        sb.AppendLine();
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
        var title = string.IsNullOrEmpty(editorId)
            ? $"{recordType}"
            : $"{recordType}: {editorId}";
        var padding = (SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
    }

    internal static string FormatModifier(sbyte value)
    {
        return value switch
        {
            > 0 => $"+{value}",
            < 0 => value.ToString(),
            _ => "+0"
        };
    }

    internal static string FormatKarmaLabel(float karma)
    {
        return karma switch
        {
            < -750 => " (Very Evil)",
            < -250 => " (Evil)",
            < 250 => " (Neutral)",
            < 750 => " (Good)",
            _ => " (Very Good)"
        };
    }

    internal static string FormatPoolSize(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }

    internal static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 1)] + "\u2026";
    }

    internal static void AppendSoundLine(
        StringBuilder sb,
        string label,
        uint? formId,
        FormIdResolver resolver)
    {
        if (!formId.HasValue)
        {
            return;
        }

        sb.AppendLine($"  {label,-17} {resolver.FormatFull(formId.Value)}");
    }

    internal static string CleanAssetPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        var segments = normalized.Split('\\');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (KnownAssetRoots.Contains(segments[i]))
            {
                return string.Join('\\', segments.Skip(i));
            }

            foreach (var root in KnownAssetRoots)
            {
                if (segments[i].Length > root.Length &&
                    segments[i].EndsWith(root, StringComparison.OrdinalIgnoreCase) &&
                    !char.IsLetterOrDigit(segments[i][segments[i].Length - root.Length - 1]))
                {
                    segments[i] = segments[i][^root.Length..];
                    return string.Join('\\', segments.Skip(i));
                }
            }
        }

        return path;
    }

    internal static void AppendPathTree(StringBuilder sb, List<string> paths, string baseIndent)
    {
        var root = new PathTreeNode();
        foreach (var path in paths)
        {
            var normalized = path.Replace('/', '\\');
            var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (i == segments.Length - 1)
                {
                    current.Files.Add(segment);
                }
                else
                {
                    if (!current.Children.TryGetValue(segment, out var child))
                    {
                        child = new PathTreeNode();
                        current.Children[segment] = child;
                    }

                    current = child;
                }
            }
        }

        RenderPathTreeNode(sb, root, baseIndent);
    }

    internal static void RenderPathTreeNode(StringBuilder sb, PathTreeNode node, string indent)
    {
        foreach (var (dirName, child) in node.Children.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var totalCount = CountDescendants(child);
            sb.AppendLine($"{indent}{dirName}\\ ({totalCount:N0})");
            RenderPathTreeNode(sb, child, indent + "  ");
        }

        foreach (var file in node.Files.Order(StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"{indent}{file}");
        }
    }

    internal static int CountDescendants(PathTreeNode node)
    {
        var count = node.Files.Count;
        foreach (var child in node.Children.Values)
        {
            count += CountDescendants(child);
        }

        return count;
    }

    #endregion
}
