using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.Scda;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Export;

/// <summary>
///     Generates human-readable GECK-style reports from reconstructed ESM data.
///     Output format is designed to be similar to how data appears in the GECK editor.
/// </summary>
public static partial class GeckReportGenerator
{
    private const int SeparatorWidth = 80;
    private const char SeparatorChar = '=';

    /// <summary>
    ///     Known Bethesda asset root directories used to strip junk prefixes from detected paths.
    /// </summary>
    private static readonly HashSet<string> KnownAssetRoots = new(StringComparer.OrdinalIgnoreCase)
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
    public static string Generate(SemanticReconstructionResult result,
        StringPoolSummary? stringPool = null,
        Dictionary<uint, string>? formIdToEditorId = null)
    {
        var sb = new StringBuilder();
        var lookup = formIdToEditorId ?? result.FormIdToEditorId;

        // Header
        AppendHeader(sb, "ESM Memory Dump Semantic Reconstruction Report");
        sb.AppendLine();
        AppendSummary(sb, result);
        sb.AppendLine();

        // Characters
        if (result.Npcs.Count > 0)
        {
            AppendNpcsSection(sb, result.Npcs, lookup);
        }

        if (result.Creatures.Count > 0)
        {
            AppendCreaturesSection(sb, result.Creatures, lookup);
        }

        if (result.Races.Count > 0)
        {
            AppendRacesSection(sb, result.Races, lookup);
        }

        if (result.Factions.Count > 0)
        {
            AppendFactionsSection(sb, result.Factions, lookup);
        }

        // Quests and Dialogue
        if (result.Quests.Count > 0)
        {
            AppendQuestsSection(sb, result.Quests, lookup);
        }

        if (result.DialogTopics.Count > 0)
        {
            AppendDialogTopicsSection(sb, result.DialogTopics, lookup);
        }

        if (result.Notes.Count > 0)
        {
            AppendNotesSection(sb, result.Notes, lookup);
        }

        if (result.Books.Count > 0)
        {
            AppendBooksSection(sb, result.Books, lookup);
        }

        if (result.Terminals.Count > 0)
        {
            AppendTerminalsSection(sb, result.Terminals);
        }

        if (result.Dialogues.Count > 0)
        {
            AppendDialogueSection(sb, result.Dialogues, lookup);
        }

        // Items
        if (result.Weapons.Count > 0)
        {
            AppendWeaponsSection(sb, result.Weapons, lookup);
        }

        if (result.Armor.Count > 0)
        {
            AppendArmorSection(sb, result.Armor, lookup);
        }

        if (result.Ammo.Count > 0)
        {
            AppendAmmoSection(sb, result.Ammo, lookup);
        }

        if (result.Consumables.Count > 0)
        {
            AppendConsumablesSection(sb, result.Consumables, lookup);
        }

        if (result.MiscItems.Count > 0)
        {
            AppendMiscItemsSection(sb, result.MiscItems, lookup);
        }

        if (result.Keys.Count > 0)
        {
            AppendKeysSection(sb, result.Keys);
        }

        if (result.Containers.Count > 0)
        {
            AppendContainersSection(sb, result.Containers, lookup);
        }

        // Abilities
        if (result.Perks.Count > 0)
        {
            AppendPerksSection(sb, result.Perks, lookup);
        }

        if (result.Spells.Count > 0)
        {
            AppendSpellsSection(sb, result.Spells, lookup);
        }

        // World
        if (result.Cells.Count > 0)
        {
            AppendCellsSection(sb, result.Cells, lookup);
        }

        if (result.Worldspaces.Count > 0)
        {
            AppendWorldspacesSection(sb, result.Worldspaces, lookup);
        }

        // String pool data from runtime memory
        if (stringPool != null)
        {
            AppendStringPoolSection(sb, stringPool);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for NPCs only.
    /// </summary>
    public static string GenerateNpcsReport(List<ReconstructedNpc> npcs, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendNpcsSection(sb, npcs, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Quests only.
    /// </summary>
    public static string GenerateQuestsReport(List<ReconstructedQuest> quests, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendQuestsSection(sb, quests, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Notes only.
    /// </summary>
    public static string GenerateNotesReport(List<ReconstructedNote> notes, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendNotesSection(sb, notes, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Dialogue only.
    /// </summary>
    public static string GenerateDialogueReport(List<ReconstructedDialogue> dialogues,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendDialogueSection(sb, dialogues, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Cells only.
    /// </summary>
    public static string GenerateCellsReport(List<ReconstructedCell> cells, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendCellsSection(sb, cells, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Weapons only.
    /// </summary>
    public static string GenerateWeaponsReport(List<ReconstructedWeapon> weapons,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendWeaponsSection(sb, weapons, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Armor only.
    /// </summary>
    public static string GenerateArmorReport(List<ReconstructedArmor> armor, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendArmorSection(sb, armor, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Ammo only.
    /// </summary>
    public static string GenerateAmmoReport(List<ReconstructedAmmo> ammo, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendAmmoSection(sb, ammo, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Consumables only.
    /// </summary>
    public static string GenerateConsumablesReport(List<ReconstructedConsumable> consumables,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendConsumablesSection(sb, consumables, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Misc Items only.
    /// </summary>
    public static string GenerateMiscItemsReport(List<ReconstructedMiscItem> miscItems,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendMiscItemsSection(sb, miscItems, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Perks only.
    /// </summary>
    public static string GeneratePerksReport(List<ReconstructedPerk> perks, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendPerksSection(sb, perks, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Spells only.
    /// </summary>
    public static string GenerateSpellsReport(List<ReconstructedSpell> spells, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendSpellsSection(sb, spells, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Races only.
    /// </summary>
    public static string GenerateRacesReport(List<ReconstructedRace> races, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendRacesSection(sb, races, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Worldspaces only.
    /// </summary>
    public static string GenerateWorldspacesReport(List<ReconstructedWorldspace> worldspaces,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendWorldspacesSection(sb, worldspaces, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Creatures only.
    /// </summary>
    public static string GenerateCreaturesReport(List<ReconstructedCreature> creatures,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendCreaturesSection(sb, creatures, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Factions only.
    /// </summary>
    public static string GenerateFactionsReport(List<ReconstructedFaction> factions,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendFactionsSection(sb, factions, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Books only.
    /// </summary>
    public static string GenerateBooksReport(List<ReconstructedBook> books, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendBooksSection(sb, books, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Keys only.
    /// </summary>
    public static string GenerateKeysReport(List<ReconstructedKey> keys, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendKeysSection(sb, keys);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Containers only.
    /// </summary>
    public static string GenerateContainersReport(List<ReconstructedContainer> containers,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendContainersSection(sb, containers, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Terminals only.
    /// </summary>
    public static string GenerateTerminalsReport(List<ReconstructedTerminal> terminals,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendTerminalsSection(sb, terminals);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Dialog Topics only.
    /// </summary>
    public static string GenerateDialogTopicsReport(List<ReconstructedDialogTopic> topics,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendDialogTopicsSection(sb, topics, lookup ?? []);
        return sb.ToString();
    }

    public static string GenerateGameSettingsReport(List<ReconstructedGameSetting> settings)
    {
        var sb = new StringBuilder();
        AppendGameSettingsSection(sb, settings);
        return sb.ToString();
    }

    public static string GenerateGlobalsReport(List<ReconstructedGlobal> globals)
    {
        var sb = new StringBuilder();
        AppendGlobalsSection(sb, globals);
        return sb.ToString();
    }

    public static string GenerateEnchantmentsReport(List<ReconstructedEnchantment> enchantments,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendEnchantmentsSection(sb, enchantments, lookup ?? []);
        return sb.ToString();
    }

    public static string GenerateBaseEffectsReport(List<ReconstructedBaseEffect> effects,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendBaseEffectsSection(sb, effects, lookup ?? []);
        return sb.ToString();
    }

    public static string GenerateWeaponModsReport(List<ReconstructedWeaponMod> mods)
    {
        var sb = new StringBuilder();
        AppendWeaponModsSection(sb, mods);
        return sb.ToString();
    }

    public static string GenerateRecipesReport(List<ReconstructedRecipe> recipes,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendRecipesSection(sb, recipes, lookup ?? []);
        return sb.ToString();
    }

    public static string GenerateChallengesReport(List<ReconstructedChallenge> challenges,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendChallengesSection(sb, challenges, lookup ?? []);
        return sb.ToString();
    }

    public static string GenerateReputationsReport(List<ReconstructedReputation> reputations)
    {
        var sb = new StringBuilder();
        AppendReputationsSection(sb, reputations);
        return sb.ToString();
    }

    public static string GenerateProjectilesReport(List<ReconstructedProjectile> projectiles,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendProjectilesSection(sb, projectiles, lookup ?? []);
        return sb.ToString();
    }

    public static string GenerateExplosionsReport(List<ReconstructedExplosion> explosions,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendExplosionsSection(sb, explosions, lookup ?? []);
        return sb.ToString();
    }

    public static string GenerateMessagesReport(List<ReconstructedMessage> messages,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendMessagesSection(sb, messages, lookup ?? []);
        return sb.ToString();
    }

    public static string GenerateClassesReport(List<ReconstructedClass> classes)
    {
        var sb = new StringBuilder();
        AppendClassesSection(sb, classes);
        return sb.ToString();
    }

    public static string GenerateMapMarkersReport(List<PlacedReference> markers,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendMapMarkersSection(sb, markers);
        return sb.ToString();
    }

    public static string GenerateLeveledListsReport(List<ReconstructedLeveledList> lists,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendLeveledListsSection(sb, lists, lookup ?? []);
        return sb.ToString();
    }

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

    private static string CsvEscape(string? value)
    {
        return Fmt.CsvEscape(value);
    }

    /// <summary>
    ///     Strip junk prefixes from asset paths by finding the first known root directory segment.
    ///     Handles both exact matches ("meshes\...") and junk-prefixed segments where garbage
    ///     bytes are prepended to a known root ("ABASE Architecture\..." → "Architecture\...").
    /// </summary>
    private static string CleanAssetPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        var segments = normalized.Split('\\');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            // Exact match on segment
            if (KnownAssetRoots.Contains(segments[i]))
            {
                return string.Join('\\', segments.Skip(i));
            }

            // Check if segment ends with a known root (junk prefix case)
            // e.g. "ABASE Architecture" → strip to "Architecture"
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

    /// <summary>
    ///     Append a hierarchical tree of file paths grouped by directory segments.
    /// </summary>
    private static void AppendPathTree(StringBuilder sb, List<string> paths, string baseIndent)
    {
        // Build tree structure: directory -> (subdirectories, files)
        var root = new PathTreeNode();
        foreach (var path in paths)
        {
            // Normalize separators
            var normalized = path.Replace('/', '\\');
            var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (i == segments.Length - 1)
                {
                    // Leaf file
                    current.Files.Add(segment);
                }
                else
                {
                    // Directory segment (case-insensitive lookup)
                    if (!current.Children.TryGetValue(segment, out var child))
                    {
                        child = new PathTreeNode();
                        current.Children[segment] = child;
                    }

                    current = child;
                }
            }
        }

        // Render tree recursively
        RenderPathTreeNode(sb, root, baseIndent);
    }

    private static void RenderPathTreeNode(StringBuilder sb, PathTreeNode node, string indent)
    {
        // Sort directories first, then files
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

    private static int CountDescendants(PathTreeNode node)
    {
        var count = node.Files.Count;
        foreach (var child in node.Children.Values)
        {
            count += CountDescendants(child);
        }

        return count;
    }

    /// <summary>
    ///     Generate split reports - one file per record type.
    ///     Returns a dictionary mapping filename to content.
    /// </summary>
    public static Dictionary<string, string> GenerateSplit(
        SemanticReconstructionResult result,
        Dictionary<uint, string>? formIdToEditorId = null)
    {
        var files = new Dictionary<string, string>();
        var lookup = formIdToEditorId ?? result.FormIdToEditorId;
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

        return files;
    }

    #region Script Summary

    /// <summary>
    ///     Generate a summary report of SCDA/SCTX script extraction results.
    /// </summary>
    public static string GenerateScriptsSummaryReport(ScdaExtractionResult scriptResult)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, "Compiled Script Summary (SCDA/SCTX/SCRO)");
        sb.AppendLine();
        sb.AppendLine($"Total Records:     {scriptResult.TotalRecords:N0}");
        sb.AppendLine($"Grouped Quests:    {scriptResult.GroupedQuests:N0}");
        sb.AppendLine($"Ungrouped Scripts: {scriptResult.UngroupedScripts:N0}");
        sb.AppendLine($"With Source (SCTX): {scriptResult.RecordsWithSource:N0}");
        sb.AppendLine($"Total Bytecode:    {scriptResult.TotalBytecodeBytes:N0} bytes");
        sb.AppendLine();

        if (scriptResult.Scripts.Count > 0)
        {
            sb.AppendLine(new string('-', SeparatorWidth));
            sb.AppendLine("  Extracted Scripts");
            sb.AppendLine(new string('-', SeparatorWidth));

            foreach (var script in scriptResult.Scripts.OrderBy(s => s.QuestName ?? s.ScriptName ?? ""))
            {
                var label = script.QuestName != null
                    ? $"{script.QuestName} / {script.ScriptName ?? "unknown"}"
                    : script.ScriptName ?? $"0x{script.Offset:X8}";
                var source = script.HasSource ? " [SCTX]" : "";
                sb.AppendLine($"  {label} ({script.BytecodeSize:N0} bytes){source}");
            }
        }

        return sb.ToString();
    }

    #endregion

    /// <summary>
    ///     Tree node for hierarchical path grouping in asset reports.
    /// </summary>
    private sealed class PathTreeNode
    {
        public Dictionary<string, PathTreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Files { get; } = [];
    }

    #region Section Generators

    private static void AppendHeader(StringBuilder sb, string title)
    {
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
        var padding = (SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
    }

    private static void AppendSectionHeader(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine(new string('-', SeparatorWidth));
        sb.AppendLine($"  {title}");
        sb.AppendLine(new string('-', SeparatorWidth));
    }

    private static void AppendRecordHeader(StringBuilder sb, string recordType, string? editorId)
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

    private static void AppendSummary(StringBuilder sb, SemanticReconstructionResult result)
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

    private static void AppendNpcsSection(StringBuilder sb, List<ReconstructedNpc> npcs,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"NPCs ({npcs.Count})");

        foreach (var npc in npcs.OrderBy(n => n.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "NPC", npc.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(npc.FormId)}");
            sb.AppendLine($"Editor ID:      {npc.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {npc.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(npc.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{npc.Offset:X8}");

            if (npc.Stats != null)
            {
                sb.AppendLine();
                sb.AppendLine("Stats (ACBS):");
                sb.AppendLine($"  Level:          {npc.Stats.Level}");
                sb.AppendLine($"  Fatigue Base:   {npc.Stats.FatigueBase}");
                sb.AppendLine($"  Barter Gold:    {npc.Stats.BarterGold}");
                sb.AppendLine($"  Speed Mult:     {npc.Stats.SpeedMultiplier}");
                sb.AppendLine($"  Karma:          {npc.Stats.KarmaAlignment:F2}");
                sb.AppendLine($"  Disposition:    {npc.Stats.DispositionBase}");
                sb.AppendLine($"  Calc Range:     {npc.Stats.CalcMin} - {npc.Stats.CalcMax}");
                sb.AppendLine($"  Flags:          0x{npc.Stats.Flags:X8}");
            }

            if (npc.Race.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine($"Race:           {FormatFormIdWithName(npc.Race.Value, lookup)}");
            }

            if (npc.Class.HasValue)
            {
                sb.AppendLine($"Class:          {FormatFormIdWithName(npc.Class.Value, lookup)}");
            }

            if (npc.Script.HasValue)
            {
                sb.AppendLine($"Script:         {FormatFormIdWithName(npc.Script.Value, lookup)}");
            }

            if (npc.VoiceType.HasValue)
            {
                sb.AppendLine($"Voice Type:     {FormatFormIdWithName(npc.VoiceType.Value, lookup)}");
            }

            if (npc.Template.HasValue)
            {
                sb.AppendLine($"Template:       {FormatFormIdWithName(npc.Template.Value, lookup)}");
            }

            if (npc.Factions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Factions:");
                foreach (var faction in npc.Factions)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(faction.FactionFormId, lookup)} (Rank: {faction.Rank})");
                }
            }

            if (npc.Spells.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Spells/Abilities:");
                foreach (var spell in npc.Spells)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(spell, lookup)}");
                }
            }

            if (npc.Inventory.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Inventory:");
                foreach (var item in npc.Inventory)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(item.ItemFormId, lookup)} x{item.Count}");
                }
            }

            if (npc.Packages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("AI Packages:");
                foreach (var package in npc.Packages)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(package, lookup)}");
                }
            }
        }
    }

    private static void AppendQuestsSection(StringBuilder sb, List<ReconstructedQuest> quests,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Quests ({quests.Count})");

        foreach (var quest in quests.OrderBy(q => q.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "QUEST", quest.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(quest.FormId)}");
            sb.AppendLine($"Editor ID:      {quest.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {quest.FullName ?? "(none)"}");
            sb.AppendLine($"Flags:          0x{quest.Flags:X2}");
            sb.AppendLine($"Priority:       {quest.Priority}");
            sb.AppendLine($"Endianness:     {(quest.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{quest.Offset:X8}");

            if (quest.Script.HasValue)
            {
                sb.AppendLine($"Script:         {FormatFormIdWithName(quest.Script.Value, lookup)}");
            }

            if (quest.Stages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Stages:");
                foreach (var stage in quest.Stages)
                {
                    var flagsStr = stage.Flags != 0 ? $" [Flags: 0x{stage.Flags:X2}]" : "";
                    var logStr = !string.IsNullOrEmpty(stage.LogEntry)
                        ? $" {stage.LogEntry}"
                        : "";
                    sb.AppendLine($"  [{stage.Index,3}]{flagsStr}{logStr}");
                }
            }

            if (quest.Objectives.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Objectives:");
                foreach (var obj in quest.Objectives)
                {
                    var text = !string.IsNullOrEmpty(obj.DisplayText)
                        ? obj.DisplayText
                        : "(no text)";
                    sb.AppendLine($"  [{obj.Index,3}] {text}");
                }
            }
        }
    }

    private static void AppendNotesSection(StringBuilder sb, List<ReconstructedNote> notes,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Notes ({notes.Count})");

        foreach (var note in notes.OrderBy(n => n.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "NOTE", note.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(note.FormId)}");
            sb.AppendLine($"Editor ID:      {note.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {note.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {note.NoteTypeName}");
            sb.AppendLine($"Endianness:     {(note.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{note.Offset:X8}");

            if (!string.IsNullOrEmpty(note.Text))
            {
                sb.AppendLine();
                sb.AppendLine("Text:");
                // Indent each line of the note text
                foreach (var line in note.Text.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }
        }
    }

    private static void AppendDialogueSection(StringBuilder sb, List<ReconstructedDialogue> dialogues,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Dialogue Responses ({dialogues.Count})");

        // Group by quest if possible
        var grouped = dialogues
            .GroupBy(d => d.QuestFormId ?? 0)
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            if (group.Key != 0)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Quest: {FormatFormIdWithName(group.Key, lookup)} ---");
            }

            foreach (var dialogue in group.OrderBy(d => d.EditorId ?? ""))
            {
                AppendRecordHeader(sb, "INFO", dialogue.EditorId);

                sb.AppendLine($"FormID:         {FormatFormId(dialogue.FormId)}");
                sb.AppendLine($"Editor ID:      {dialogue.EditorId ?? "(none)"}");

                if (dialogue.TopicFormId.HasValue)
                {
                    sb.AppendLine($"Topic:          {FormatFormIdWithName(dialogue.TopicFormId.Value, lookup)}");
                }

                if (dialogue.QuestFormId.HasValue)
                {
                    sb.AppendLine($"Quest:          {FormatFormIdWithName(dialogue.QuestFormId.Value, lookup)}");
                }

                if (dialogue.SpeakerFormId.HasValue)
                {
                    sb.AppendLine($"Speaker:        {FormatFormIdWithName(dialogue.SpeakerFormId.Value, lookup)}");
                }

                if (dialogue.PreviousInfo.HasValue)
                {
                    sb.AppendLine($"Previous INFO:  {FormatFormIdWithName(dialogue.PreviousInfo.Value, lookup)}");
                }

                if (!string.IsNullOrEmpty(dialogue.PromptText))
                {
                    sb.AppendLine($"Prompt:         \"{dialogue.PromptText}\"");
                }

                // Flags
                var flags = new List<string>();
                if (dialogue.IsGoodbye) flags.Add("Goodbye");
                if (dialogue.IsSayOnce) flags.Add("Say Once");
                if (dialogue.IsSpeechChallenge) flags.Add($"Speech Challenge: {dialogue.DifficultyName}");
                if (flags.Count > 0)
                {
                    sb.AppendLine($"Flags:          {string.Join(", ", flags)}");
                }

                sb.AppendLine(
                    $"Endianness:     {(dialogue.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
                sb.AppendLine($"Offset:         0x{dialogue.Offset:X8}");

                if (dialogue.Responses.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Responses:");
                    foreach (var response in dialogue.Responses.OrderBy(r => r.ResponseNumber))
                    {
                        var emotionStr = response.EmotionType != 0 || response.EmotionValue != 0
                            ? $" [{response.EmotionName}: {response.EmotionValue}]"
                            : "";
                        sb.AppendLine($"  [{response.ResponseNumber}]{emotionStr}");
                        if (!string.IsNullOrEmpty(response.Text))
                        {
                            sb.AppendLine($"    \"{response.Text}\"");
                        }
                    }
                }
            }
        }
    }

    private static void AppendCellsSection(StringBuilder sb, List<ReconstructedCell> cells,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Cells ({cells.Count})");

        // Separate interior and exterior cells
        var exteriorCells = cells.Where(c => !c.IsInterior && c.GridX.HasValue).ToList();
        var interiorCells = cells.Where(c => c.IsInterior || !c.GridX.HasValue).ToList();

        if (exteriorCells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Exterior Cells ({exteriorCells.Count}):");

            foreach (var cell in exteriorCells.OrderBy(c => c.GridX).ThenBy(c => c.GridY))
            {
                var gridStr = $"({cell.GridX}, {cell.GridY})";
                AppendRecordHeader(sb, $"CELL {gridStr}", cell.EditorId);

                sb.AppendLine($"FormID:         {FormatFormId(cell.FormId)}");
                sb.AppendLine($"Editor ID:      {cell.EditorId ?? "(none)"}");
                sb.AppendLine($"Display Name:   {cell.FullName ?? "(none)"}");
                sb.AppendLine($"Grid:           {cell.GridX}, {cell.GridY}");
                sb.AppendLine($"Flags:          0x{cell.Flags:X2}");
                sb.AppendLine($"Has Water:      {cell.HasWater}");
                sb.AppendLine($"Endianness:     {(cell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
                sb.AppendLine($"Offset:         0x{cell.Offset:X8}");

                if (cell.Heightmap != null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"Heightmap:      Found (offset: {cell.Heightmap.HeightOffset:F1})");
                }

                AppendPlacedObjects(sb, cell.PlacedObjects, lookup);
            }
        }

        if (interiorCells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Interior Cells ({interiorCells.Count}):");

            foreach (var cell in interiorCells.OrderBy(c => c.EditorId ?? ""))
            {
                AppendRecordHeader(sb, "CELL (Interior)", cell.EditorId);

                sb.AppendLine($"FormID:         {FormatFormId(cell.FormId)}");
                sb.AppendLine($"Editor ID:      {cell.EditorId ?? "(none)"}");
                sb.AppendLine($"Display Name:   {cell.FullName ?? "(none)"}");
                sb.AppendLine($"Flags:          0x{cell.Flags:X2}");
                sb.AppendLine($"Endianness:     {(cell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
                sb.AppendLine($"Offset:         0x{cell.Offset:X8}");

                AppendPlacedObjects(sb, cell.PlacedObjects, lookup);
            }
        }
    }

    private static void AppendPlacedObjects(StringBuilder sb, List<PlacedReference> placedObjects,
        Dictionary<uint, string> lookup)
    {
        if (placedObjects.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"Placed Objects ({placedObjects.Count}):");

        foreach (var obj in placedObjects)
        {
            var baseStr = !string.IsNullOrEmpty(obj.BaseEditorId)
                ? obj.BaseEditorId
                : FormatFormId(obj.BaseFormId);
            var scaleStr = Math.Abs(obj.Scale - 1.0f) > 0.01f ? $" scale={obj.Scale:F2}" : "";
            sb.AppendLine($"  - {baseStr} ({obj.RecordType})");
            sb.AppendLine($"      at ({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1}){scaleStr}");
        }
    }

    private static void AppendWorldspacesSection(StringBuilder sb, List<ReconstructedWorldspace> worldspaces,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Worldspaces ({worldspaces.Count})");

        foreach (var wrld in worldspaces.OrderBy(w => w.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "WRLD", wrld.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(wrld.FormId)}");
            sb.AppendLine($"Editor ID:      {wrld.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {wrld.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(wrld.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{wrld.Offset:X8}");

            if (wrld.ParentWorldspaceFormId.HasValue)
            {
                sb.AppendLine($"Parent:         {FormatFormIdWithName(wrld.ParentWorldspaceFormId.Value, lookup)}");
            }

            if (wrld.ClimateFormId.HasValue)
            {
                sb.AppendLine($"Climate:        {FormatFormIdWithName(wrld.ClimateFormId.Value, lookup)}");
            }

            if (wrld.WaterFormId.HasValue)
            {
                sb.AppendLine($"Water:          {FormatFormIdWithName(wrld.WaterFormId.Value, lookup)}");
            }

            if (wrld.Cells.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Cells:          {wrld.Cells.Count}");
            }
        }
    }

    private static void AppendWeaponsSection(StringBuilder sb, List<ReconstructedWeapon> weapons,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Weapons ({weapons.Count})");

        foreach (var weapon in weapons.OrderBy(w => w.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "WEAP", weapon.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(weapon.FormId)}");
            sb.AppendLine($"Editor ID:      {weapon.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {weapon.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {weapon.WeaponTypeName}");
            sb.AppendLine($"Endianness:     {(weapon.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{weapon.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Combat Stats:");
            sb.AppendLine($"  Damage:         {weapon.Damage}");
            sb.AppendLine($"  DPS:            {weapon.DamagePerSecond:F1}");
            sb.AppendLine($"  Fire Rate:      {weapon.ShotsPerSec:F1}/sec");
            sb.AppendLine($"  Clip Size:      {weapon.ClipSize}");
            sb.AppendLine($"  Range:          {weapon.MinRange:F0} - {weapon.MaxRange:F0}");

            sb.AppendLine();
            sb.AppendLine("Accuracy:");
            sb.AppendLine($"  Spread:         {weapon.Spread:F2}");
            sb.AppendLine($"  Min Spread:     {weapon.MinSpread:F2}");
            sb.AppendLine($"  Drift:          {weapon.Drift:F2}");

            if (weapon.StrengthRequirement > 0 || weapon.SkillRequirement > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Requirements:");
                if (weapon.StrengthRequirement > 0)
                {
                    sb.AppendLine($"  Strength:       {weapon.StrengthRequirement}");
                }

                if (weapon.SkillRequirement > 0)
                {
                    sb.AppendLine($"  Skill:          {weapon.SkillRequirement}");
                }
            }

            if (weapon.CriticalDamage != 0 || Math.Abs(weapon.CriticalChance - 1.0f) > 0.01f)
            {
                sb.AppendLine();
                sb.AppendLine("Critical:");
                sb.AppendLine($"  Damage:         {weapon.CriticalDamage}");
                sb.AppendLine($"  Chance:         x{weapon.CriticalChance:F1}");
                if (weapon.CriticalEffectFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Effect:         {FormatFormIdWithName(weapon.CriticalEffectFormId.Value, lookup)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Value/Weight:");
            sb.AppendLine($"  Value:          {weapon.Value} caps");
            sb.AppendLine($"  Weight:         {weapon.Weight:F1}");
            sb.AppendLine($"  Health:         {weapon.Health}");

            if (weapon.AmmoFormId.HasValue)
            {
                sb.AppendLine($"  Ammo:           {FormatFormIdWithName(weapon.AmmoFormId.Value, lookup)}");
            }

            if (weapon.ProjectileFormId.HasValue)
            {
                sb.AppendLine($"  Projectile:     {FormatFormIdWithName(weapon.ProjectileFormId.Value, lookup)}");
            }

            if (weapon.ImpactDataSetFormId.HasValue)
            {
                sb.AppendLine($"  Impact Data:    {FormatFormIdWithName(weapon.ImpactDataSetFormId.Value, lookup)}");
            }

            sb.AppendLine($"  AP Cost:        {weapon.ActionPoints:F0}");

            if (!string.IsNullOrEmpty(weapon.ModelPath))
            {
                sb.AppendLine($"  Model:          {weapon.ModelPath}");
            }

            // Sound effects
            var hasSounds = weapon.PickupSoundFormId.HasValue || weapon.PutdownSoundFormId.HasValue ||
                            weapon.FireSound3DFormId.HasValue || weapon.FireSoundDistFormId.HasValue ||
                            weapon.FireSound2DFormId.HasValue || weapon.DryFireSoundFormId.HasValue ||
                            weapon.IdleSoundFormId.HasValue || weapon.EquipSoundFormId.HasValue ||
                            weapon.UnequipSoundFormId.HasValue;
            if (hasSounds)
            {
                sb.AppendLine();
                sb.AppendLine("Sound Effects:");

                if (weapon.FireSound3DFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Fire (3D):      {FormatFormIdWithName(weapon.FireSound3DFormId.Value, lookup)}");
                }

                if (weapon.FireSoundDistFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Fire (Distant): {FormatFormIdWithName(weapon.FireSoundDistFormId.Value, lookup)}");
                }

                if (weapon.FireSound2DFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Fire (2D):      {FormatFormIdWithName(weapon.FireSound2DFormId.Value, lookup)}");
                }

                if (weapon.DryFireSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Dry Fire:       {FormatFormIdWithName(weapon.DryFireSoundFormId.Value, lookup)}");
                }

                if (weapon.IdleSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Idle:           {FormatFormIdWithName(weapon.IdleSoundFormId.Value, lookup)}");
                }

                if (weapon.EquipSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Equip:          {FormatFormIdWithName(weapon.EquipSoundFormId.Value, lookup)}");
                }

                if (weapon.UnequipSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Unequip:        {FormatFormIdWithName(weapon.UnequipSoundFormId.Value, lookup)}");
                }

                if (weapon.PickupSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Pickup:         {FormatFormIdWithName(weapon.PickupSoundFormId.Value, lookup)}");
                }

                if (weapon.PutdownSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Putdown:        {FormatFormIdWithName(weapon.PutdownSoundFormId.Value, lookup)}");
                }
            }
        }
    }

    private static void AppendArmorSection(StringBuilder sb, List<ReconstructedArmor> armor,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Armor ({armor.Count})");

        foreach (var item in armor.OrderBy(a => a.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "ARMO", item.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(item.FormId)}");
            sb.AppendLine($"Editor ID:      {item.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {item.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(item.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{item.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  DT:             {item.DamageThreshold:F1}");
            sb.AppendLine($"  Value:          {item.Value} caps");
            sb.AppendLine($"  Weight:         {item.Weight:F1}");
            sb.AppendLine($"  Health:         {item.Health}");

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
    }

    private static void AppendAmmoSection(StringBuilder sb, List<ReconstructedAmmo> ammo,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Ammunition ({ammo.Count})");

        foreach (var item in ammo.OrderBy(a => a.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "AMMO", item.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(item.FormId)}");
            sb.AppendLine($"Editor ID:      {item.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {item.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(item.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{item.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  Speed:          {item.Speed:F1}");
            sb.AppendLine($"  Value:          {item.Value} caps");
            sb.AppendLine($"  Clip Rounds:    {item.ClipRounds}");
            sb.AppendLine($"  Flags:          0x{item.Flags:X2}");

            if (item.ProjectileFormId.HasValue)
            {
                sb.AppendLine($"  Projectile:     {FormatFormIdWithName(item.ProjectileFormId.Value, lookup)}");
            }

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
    }

    private static void AppendConsumablesSection(StringBuilder sb, List<ReconstructedConsumable> consumables,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Consumables ({consumables.Count})");

        foreach (var item in consumables.OrderBy(c => c.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "ALCH", item.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(item.FormId)}");
            sb.AppendLine($"Editor ID:      {item.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {item.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(item.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{item.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  Value:          {item.Value} caps");
            sb.AppendLine($"  Weight:         {item.Weight:F1}");

            if (item.AddictionFormId.HasValue)
            {
                sb.AppendLine($"  Addiction:      {FormatFormIdWithName(item.AddictionFormId.Value, lookup)}");
                sb.AppendLine($"  Addict. Chance: {item.AddictionChance * 100:F0}%");
            }

            if (item.EffectFormIds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Effects:");
                foreach (var effect in item.EffectFormIds)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(effect, lookup)}");
                }
            }

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
    }

    private static void AppendMiscItemsSection(StringBuilder sb, List<ReconstructedMiscItem> miscItems,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Miscellaneous Items ({miscItems.Count})");

        foreach (var item in miscItems.OrderBy(m => m.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "MISC", item.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(item.FormId)}");
            sb.AppendLine($"Editor ID:      {item.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {item.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(item.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{item.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  Value:          {item.Value} caps");
            sb.AppendLine($"  Weight:         {item.Weight:F1}");

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
    }

    private static void AppendPerksSection(StringBuilder sb, List<ReconstructedPerk> perks,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Perks ({perks.Count})");

        foreach (var perk in perks.OrderBy(p => p.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "PERK", perk.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(perk.FormId)}");
            sb.AppendLine($"Editor ID:      {perk.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {perk.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(perk.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{perk.Offset:X8}");

            if (!string.IsNullOrEmpty(perk.Description))
            {
                sb.AppendLine();
                sb.AppendLine("Description:");
                foreach (var line in perk.Description.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Requirements:");
            sb.AppendLine($"  Ranks:          {perk.Ranks}");
            sb.AppendLine($"  Min Level:      {perk.MinLevel}");
            sb.AppendLine($"  Playable:       {(perk.IsPlayable ? "Yes" : "No")}");
            sb.AppendLine($"  Is Trait:       {(perk.IsTrait ? "Yes" : "No")}");

            if (!string.IsNullOrEmpty(perk.IconPath))
            {
                sb.AppendLine($"  Icon:           {perk.IconPath}");
            }

            if (perk.Entries.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Entries:");
                foreach (var entry in perk.Entries)
                {
                    var abilityStr = entry.AbilityFormId.HasValue
                        ? $" Ability: {FormatFormIdWithName(entry.AbilityFormId.Value, lookup)}"
                        : "";
                    sb.AppendLine($"  [Rank {entry.Rank}] {entry.TypeName}{abilityStr}");
                }
            }
        }
    }

    private static void AppendSpellsSection(StringBuilder sb, List<ReconstructedSpell> spells,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Spells/Abilities ({spells.Count})");

        foreach (var spell in spells.OrderBy(s => s.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "SPEL", spell.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(spell.FormId)}");
            sb.AppendLine($"Editor ID:      {spell.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {spell.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {spell.TypeName}");
            sb.AppendLine($"Endianness:     {(spell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{spell.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  Cost:           {spell.Cost}");
            sb.AppendLine($"  Level:          {spell.Level}");
            sb.AppendLine($"  Flags:          0x{spell.Flags:X2}");

            if (spell.EffectFormIds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Effects:");
                foreach (var effect in spell.EffectFormIds)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(effect, lookup)}");
                }
            }
        }
    }

    private static void AppendRacesSection(StringBuilder sb, List<ReconstructedRace> races,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Races ({races.Count})");

        foreach (var race in races.OrderBy(r => r.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "RACE", race.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(race.FormId)}");
            sb.AppendLine($"Editor ID:      {race.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {race.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(race.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{race.Offset:X8}");

            if (!string.IsNullOrEmpty(race.Description))
            {
                sb.AppendLine();
                sb.AppendLine("Description:");
                foreach (var line in race.Description.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("S.P.E.C.I.A.L. Modifiers:");
            sb.AppendLine($"  Strength:     {FormatModifier(race.Strength)}");
            sb.AppendLine($"  Perception:   {FormatModifier(race.Perception)}");
            sb.AppendLine($"  Endurance:    {FormatModifier(race.Endurance)}");
            sb.AppendLine($"  Charisma:     {FormatModifier(race.Charisma)}");
            sb.AppendLine($"  Intelligence: {FormatModifier(race.Intelligence)}");
            sb.AppendLine($"  Agility:      {FormatModifier(race.Agility)}");
            sb.AppendLine($"  Luck:         {FormatModifier(race.Luck)}");

            sb.AppendLine();
            sb.AppendLine("Height:");
            sb.AppendLine($"  Male:         {race.MaleHeight:F2}");
            sb.AppendLine($"  Female:       {race.FemaleHeight:F2}");

            if (race.MaleVoiceFormId.HasValue || race.FemaleVoiceFormId.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine("Voice Types:");
                if (race.MaleVoiceFormId.HasValue)
                {
                    sb.AppendLine($"  Male:         {FormatFormIdWithName(race.MaleVoiceFormId.Value, lookup)}");
                }

                if (race.FemaleVoiceFormId.HasValue)
                {
                    sb.AppendLine($"  Female:       {FormatFormIdWithName(race.FemaleVoiceFormId.Value, lookup)}");
                }
            }

            if (race.OlderRaceFormId.HasValue || race.YoungerRaceFormId.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine("Related Races:");
                if (race.OlderRaceFormId.HasValue)
                {
                    sb.AppendLine($"  Older:        {FormatFormIdWithName(race.OlderRaceFormId.Value, lookup)}");
                }

                if (race.YoungerRaceFormId.HasValue)
                {
                    sb.AppendLine($"  Younger:      {FormatFormIdWithName(race.YoungerRaceFormId.Value, lookup)}");
                }
            }

            if (race.AbilityFormIds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Racial Abilities:");
                foreach (var ability in race.AbilityFormIds)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(ability, lookup)}");
                }
            }
        }
    }

    private static void AppendCreaturesSection(StringBuilder sb, List<ReconstructedCreature> creatures,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Creatures ({creatures.Count})");

        foreach (var creature in creatures.OrderBy(c => c.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "CREA", creature.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(creature.FormId)}");
            sb.AppendLine($"Editor ID:      {creature.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {creature.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {creature.CreatureTypeName}");
            sb.AppendLine($"Endianness:     {(creature.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{creature.Offset:X8}");

            if (creature.Stats != null)
            {
                sb.AppendLine();
                sb.AppendLine("Stats (ACBS):");
                sb.AppendLine($"  Level:          {creature.Stats.Level}");
                sb.AppendLine($"  Fatigue Base:   {creature.Stats.FatigueBase}");
            }

            if (creature.AttackDamage > 0)
            {
                sb.AppendLine($"  Attack Damage:  {creature.AttackDamage}");
            }
        }
    }

    private static void AppendFactionsSection(StringBuilder sb, List<ReconstructedFaction> factions,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Factions ({factions.Count})");

        foreach (var faction in factions.OrderBy(f => f.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "FACT", faction.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(faction.FormId)}");
            sb.AppendLine($"Editor ID:      {faction.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {faction.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(faction.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{faction.Offset:X8}");

            if (faction.IsHiddenFromPlayer || faction.AllowsEvil || faction.AllowsSpecialCombat)
            {
                sb.AppendLine();
                sb.AppendLine("Flags:");
                if (faction.IsHiddenFromPlayer) sb.AppendLine("  - Hidden From Player");
                if (faction.AllowsEvil) sb.AppendLine("  - Allows Evil");
                if (faction.AllowsSpecialCombat) sb.AppendLine("  - Allows Special Combat");
            }

            if (faction.RankNames.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Ranks:");
                for (var i = 0; i < faction.RankNames.Count; i++)
                {
                    sb.AppendLine($"  [{i}] {faction.RankNames[i]}");
                }
            }

            if (faction.Relations.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Relations:");
                foreach (var rel in faction.Relations)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(rel.FactionFormId, lookup)}: {rel.Modifier:+0;-0}");
                }
            }
        }
    }

    private static void AppendBooksSection(StringBuilder sb, List<ReconstructedBook> books,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Books ({books.Count})");

        foreach (var book in books.OrderBy(b => b.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "BOOK", book.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(book.FormId)}");
            sb.AppendLine($"Editor ID:      {book.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {book.FullName ?? "(none)"}");
            sb.AppendLine($"Value:          {book.Value} caps");
            sb.AppendLine($"Weight:         {book.Weight:F1}");
            sb.AppendLine($"Endianness:     {(book.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{book.Offset:X8}");

            if (book.TeachesSkill)
            {
                sb.AppendLine($"Teaches Skill:  {book.SkillTaught}");
            }

            if (!string.IsNullOrEmpty(book.Text))
            {
                sb.AppendLine();
                sb.AppendLine("Text:");
                foreach (var line in book.Text.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }
        }
    }

    private static void AppendKeysSection(StringBuilder sb, List<ReconstructedKey> keys)
    {
        AppendSectionHeader(sb, $"Keys ({keys.Count})");

        foreach (var key in keys.OrderBy(k => k.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "KEYM", key.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(key.FormId)}");
            sb.AppendLine($"Editor ID:      {key.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {key.FullName ?? "(none)"}");
            sb.AppendLine($"Value:          {key.Value} caps");
            sb.AppendLine($"Weight:         {key.Weight:F1}");
            sb.AppendLine($"Endianness:     {(key.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{key.Offset:X8}");
        }
    }

    private static void AppendContainersSection(StringBuilder sb, List<ReconstructedContainer> containers,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Containers ({containers.Count})");

        foreach (var container in containers.OrderBy(c => c.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "CONT", container.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(container.FormId)}");
            sb.AppendLine($"Editor ID:      {container.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {container.FullName ?? "(none)"}");
            sb.AppendLine($"Respawns:       {(container.Respawns ? "Yes" : "No")}");
            sb.AppendLine(
                $"Endianness:     {(container.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{container.Offset:X8}");

            if (container.Contents.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Contents ({container.Contents.Count}):");
                foreach (var item in container.Contents)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(item.ItemFormId, lookup)} x{item.Count}");
                }
            }
        }
    }

    private static void AppendTerminalsSection(StringBuilder sb, List<ReconstructedTerminal> terminals)
    {
        AppendSectionHeader(sb, $"Terminals ({terminals.Count})");

        foreach (var terminal in terminals.OrderBy(t => t.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "TERM", terminal.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(terminal.FormId)}");
            sb.AppendLine($"Editor ID:      {terminal.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {terminal.FullName ?? "(none)"}");
            sb.AppendLine($"Difficulty:     {terminal.DifficultyName}");
            sb.AppendLine($"Endianness:     {(terminal.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{terminal.Offset:X8}");

            if (!string.IsNullOrEmpty(terminal.HeaderText))
            {
                sb.AppendLine();
                sb.AppendLine("Header:");
                foreach (var line in terminal.HeaderText.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }

            if (terminal.MenuItems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Menu Items ({terminal.MenuItems.Count}):");
                foreach (var item in terminal.MenuItems)
                {
                    sb.AppendLine($"  - {item.Text ?? "(no text)"}");
                }
            }
        }
    }

    private static void AppendDialogTopicsSection(StringBuilder sb, List<ReconstructedDialogTopic> topics,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Dialog Topics ({topics.Count})");

        foreach (var topic in topics.OrderBy(t => t.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "DIAL", topic.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(topic.FormId)}");
            sb.AppendLine($"Editor ID:      {topic.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {topic.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {topic.TopicTypeName}");
            sb.AppendLine($"Endianness:     {(topic.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{topic.Offset:X8}");

            if (topic.QuestFormId.HasValue)
            {
                sb.AppendLine($"Quest:          {FormatFormIdWithName(topic.QuestFormId.Value, lookup)}");
            }

            if (topic.ResponseCount > 0)
            {
                sb.AppendLine($"Responses:      {topic.ResponseCount}");
            }
        }
    }

    // ─────────────────── Dialogue Tree Report ───────────────────

    /// <summary>
    ///     Generate a standalone GECK-style dialogue tree report from the hierarchical tree result.
    /// </summary>
    public static string GenerateDialogueTreeReport(
        DialogueTreeResult tree,
        Dictionary<uint, string> lookup,
        Dictionary<uint, string>? displayNameLookup = null)
    {
        var sb = new StringBuilder();
        var combined = CombineLookups(lookup, displayNameLookup ?? []);

        var totalQuests = tree.QuestTrees.Count;
        var totalTopics = tree.QuestTrees.Values.Sum(q => q.Topics.Count) + tree.OrphanTopics.Count;
        var totalInfos = tree.QuestTrees.Values
            .SelectMany(q => q.Topics)
            .Sum(t => t.InfoChain.Count) + tree.OrphanTopics.Sum(t => t.InfoChain.Count);

        AppendHeader(sb, "Dialogue Tree");
        sb.AppendLine();
        sb.AppendLine($"  Quests:     {totalQuests:N0}");
        sb.AppendLine($"  Topics:     {totalTopics:N0}");
        sb.AppendLine($"  Responses:  {totalInfos:N0}");
        sb.AppendLine();

        // Render quest trees
        foreach (var (_, questNode) in tree.QuestTrees.OrderBy(q => q.Value.QuestName ?? ""))
        {
            RenderQuestTree(sb, questNode, combined);
        }

        // Render orphan topics
        if (tree.OrphanTopics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', SeparatorWidth));
            sb.AppendLine("  Orphan Topics (no quest link)");
            sb.AppendLine(new string('=', SeparatorWidth));

            var visited = new HashSet<uint>();
            foreach (var topic in tree.OrphanTopics)
            {
                RenderTopicTree(sb, topic, visited, "  ", combined);
            }
        }

        return sb.ToString();
    }

    private static void RenderQuestTree(StringBuilder sb, QuestDialogueNode questNode,
        Dictionary<uint, string> lookup)
    {
        sb.AppendLine();
        var questLabel = questNode.QuestName ?? FormatFormId(questNode.QuestFormId);
        sb.AppendLine($"{"",3}{new string('=', SeparatorWidth - 6)}");
        sb.AppendLine($"{"",3}Quest: {questLabel} ({FormatFormId(questNode.QuestFormId)})");
        sb.AppendLine($"{"",3}{new string('=', SeparatorWidth - 6)}");

        var visited = new HashSet<uint>();
        for (var i = 0; i < questNode.Topics.Count; i++)
        {
            var isLast = i == questNode.Topics.Count - 1;
            var connector = isLast ? "  +-- " : "  |-- ";
            var continuation = isLast ? "      " : "  |   ";

            RenderTopicTree(sb, questNode.Topics[i], visited, connector, lookup, continuation);
        }
    }

    private static void RenderTopicTree(StringBuilder sb, TopicDialogueNode topic,
        HashSet<uint> visited, string indent, Dictionary<uint, string> lookup,
        string? continuationIndent = null)
    {
        continuationIndent ??= indent;

        // Deduplication: prevent infinite recursion from circular cross-topic links
        if (topic.TopicFormId != 0 && !visited.Add(topic.TopicFormId))
        {
            sb.AppendLine();
            sb.AppendLine(
                $"{indent}Topic: {topic.TopicName ?? FormatFormId(topic.TopicFormId)} (see above)");
            return;
        }

        sb.AppendLine();

        // Topic header
        var topicLabel = topic.TopicName ?? "(unnamed topic)";
        var topicTypeStr = topic.Topic != null ? $" [{topic.Topic.TopicTypeName}]" : "";
        var formIdStr = topic.TopicFormId != 0 ? $" ({FormatFormId(topic.TopicFormId)})" : "";
        sb.AppendLine($"{indent}Topic: {topicLabel}{formIdStr}{topicTypeStr}");

        // Topic metadata
        if (topic.Topic is { DummyPrompt: not null })
        {
            sb.AppendLine($"{continuationIndent}  Prompt: \"{topic.Topic.DummyPrompt}\"");
        }

        if (topic.Topic is { Priority: not 0f })
        {
            sb.AppendLine($"{continuationIndent}  Priority: {topic.Topic.Priority:F1}");
        }

        // INFO chain
        for (var i = 0; i < topic.InfoChain.Count; i++)
        {
            var infoNode = topic.InfoChain[i];

            sb.AppendLine();
            RenderInfoNode(sb, infoNode, i + 1, continuationIndent, lookup);

            // Render linked topics recursively
            for (var j = 0; j < infoNode.LinkedTopics.Count; j++)
            {
                var linkedTopic = infoNode.LinkedTopics[j];
                var linkIndent = continuationIndent + "        ";
                var linkCont = continuationIndent + "        ";
                sb.AppendLine($"{continuationIndent}      -> Links to:");
                RenderTopicTree(sb, linkedTopic, visited, linkIndent, lookup, linkCont);
            }
        }
    }

    private static void RenderInfoNode(StringBuilder sb, InfoDialogueNode infoNode, int index,
        string indent, Dictionary<uint, string> lookup)
    {
        var info = infoNode.Info;

        // Speaker name resolution
        var speakerStr = "";
        if (info.SpeakerFormId.HasValue && info.SpeakerFormId.Value != 0)
        {
            speakerStr = FormatFormIdWithName(info.SpeakerFormId.Value, lookup) + ": ";
        }

        // Prompt text (player's line)
        if (!string.IsNullOrEmpty(info.PromptText))
        {
            sb.AppendLine($"{indent}  [{index}] Player: \"{info.PromptText}\"");
        }

        // Response text (NPC's lines)
        if (info.Responses.Count > 0)
        {
            foreach (var response in info.Responses.OrderBy(r => r.ResponseNumber))
            {
                var emotionStr = response.EmotionType != 0 || response.EmotionValue != 0
                    ? $" [{response.EmotionName}: {response.EmotionValue}]"
                    : "";
                if (!string.IsNullOrEmpty(response.Text))
                {
                    sb.AppendLine($"{indent}      {speakerStr}\"{response.Text}\"{emotionStr}");
                }
            }
        }
        else if (string.IsNullOrEmpty(info.PromptText))
        {
            // No prompt and no responses — show FormID reference
            sb.AppendLine($"{indent}  [{index}] {FormatFormId(info.FormId)} (no text recovered)");
        }

        // Flags line
        var flags = new List<string>();
        if (info.IsGoodbye)
        {
            flags.Add("Goodbye");
        }

        if (info.IsSayOnce)
        {
            flags.Add("Say Once");
        }

        if (info.IsSpeechChallenge)
        {
            flags.Add($"Speech Challenge: {info.DifficultyName}");
        }

        if (flags.Count > 0)
        {
            sb.AppendLine($"{indent}      [{string.Join("] [", flags)}]");
        }
    }

    /// <summary>
    ///     Combine editor ID and display name lookups into a single dictionary,
    ///     preferring display names where available.
    /// </summary>
    private static Dictionary<uint, string> CombineLookups(
        Dictionary<uint, string> editorIdLookup,
        Dictionary<uint, string> displayNameLookup)
    {
        var combined = new Dictionary<uint, string>(editorIdLookup);
        foreach (var (formId, name) in displayNameLookup)
        {
            combined[formId] = name; // Display name takes priority
        }

        return combined;
    }

    // ─────────────────── End Dialogue Tree ───────────────────

    private static void AppendGameSettingsSection(StringBuilder sb, List<ReconstructedGameSetting> settings)
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

    private static void AppendGlobalsSection(StringBuilder sb, List<ReconstructedGlobal> globals)
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

    private static void AppendEnchantmentsSection(StringBuilder sb, List<ReconstructedEnchantment> enchantments,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Enchantments ({enchantments.Count})");
        sb.AppendLine();

        var byType = enchantments.GroupBy(e => e.TypeName).OrderBy(g => g.Key).ToList();
        sb.AppendLine($"Total Enchantments: {enchantments.Count:N0}");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key}: {group.Count():N0}");
        }

        var withEffects = enchantments.Count(e => e.Effects.Count > 0);
        sb.AppendLine($"  With Effects: {withEffects:N0} ({enchantments.Sum(e => e.Effects.Count):N0} total effects)");
        sb.AppendLine();

        foreach (var ench in enchantments.OrderBy(e => e.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  ENCHANTMENT: {ench.EditorId ?? "(none)"} \u2014 {ench.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {FormatFormId(ench.FormId)}");
            sb.AppendLine($"  Type:        {ench.TypeName}");
            sb.AppendLine($"  Charge:      {ench.ChargeAmount}");
            sb.AppendLine($"  Cost:        {ench.EnchantCost}");
            if (ench.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{ench.Flags:X2}");
            }

            if (ench.Effects.Count > 0)
            {
                sb.AppendLine(
                    $"  \u2500\u2500 Effects ({ench.Effects.Count}) {new string('\u2500', 80 - 18 - ench.Effects.Count.ToString().Length)}");
                sb.AppendLine($"  {"Effect",-32} {"Magnitude",10} {"Area",6} {"Duration",10} {"Type",-8}");
                sb.AppendLine($"  {new string('\u2500', 70)}");
                foreach (var effect in ench.Effects)
                {
                    var effectName = effect.EffectFormId != 0
                        ? FormatFormIdWithName(effect.EffectFormId, lookup)
                        : "(none)";
                    if (effectName.Length > 32)
                    {
                        effectName = effectName[..29] + "\u2026";
                    }

                    var typeName = effect.Type switch
                    {
                        0 => "Self",
                        1 => "Touch",
                        2 => "Target",
                        _ => $"#{effect.Type}"
                    };
                    sb.AppendLine(
                        $"  {effectName,-32} {effect.Magnitude,10:F1} {effect.Area,6} {effect.Duration,10} {typeName,-8}");
                }
            }

            sb.AppendLine();
        }
    }

    private static void AppendBaseEffectsSection(StringBuilder sb, List<ReconstructedBaseEffect> effects,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Base Effects ({effects.Count})");
        sb.AppendLine();

        var byArchetype = effects.GroupBy(e => e.ArchetypeName).OrderByDescending(g => g.Count()).ToList();
        sb.AppendLine($"Total Base Effects: {effects.Count:N0}");
        sb.AppendLine("By Archetype:");
        foreach (var group in byArchetype)
        {
            sb.AppendLine($"  {group.Key,-30} {group.Count(),5:N0}");
        }

        sb.AppendLine();

        foreach (var effect in effects.OrderBy(e => e.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  EFFECT: {effect.EditorId ?? "(none)"} \u2014 {effect.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {FormatFormId(effect.FormId)}");
            sb.AppendLine($"  Archetype:   {effect.ArchetypeName}");
            sb.AppendLine($"  Base Cost:   {effect.BaseCost:F2}");
            if (!string.IsNullOrEmpty(effect.EffectCode))
            {
                sb.AppendLine($"  Effect Code: {effect.EffectCode}");
            }

            if (effect.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{effect.Flags:X8}");
            }

            if (effect.ActorValue >= 0)
            {
                sb.AppendLine($"  Actor Value: {effect.ActorValue}");
            }

            if (effect.ResistValue >= 0)
            {
                sb.AppendLine($"  Resist Value: {effect.ResistValue}");
            }

            if (effect.MagicSchool >= 0)
            {
                sb.AppendLine($"  Magic School: {effect.MagicSchool}");
            }

            if (!string.IsNullOrEmpty(effect.Description))
            {
                sb.AppendLine($"  Description: {effect.Description}");
            }

            if (effect.Projectile != 0)
            {
                sb.AppendLine($"  Projectile:  {FormatFormIdWithName(effect.Projectile, lookup)}");
            }

            if (effect.Explosion != 0)
            {
                sb.AppendLine($"  Explosion:   {FormatFormIdWithName(effect.Explosion, lookup)}");
            }

            if (effect.AssociatedItem != 0)
            {
                sb.AppendLine($"  Assoc. Item: {FormatFormIdWithName(effect.AssociatedItem, lookup)}");
            }

            if (!string.IsNullOrEmpty(effect.ModelPath))
            {
                sb.AppendLine($"  Model:       {effect.ModelPath}");
            }

            if (!string.IsNullOrEmpty(effect.Icon))
            {
                sb.AppendLine($"  Icon:        {effect.Icon}");
            }

            sb.AppendLine();
        }
    }

    private static void AppendWeaponModsSection(StringBuilder sb, List<ReconstructedWeaponMod> mods)
    {
        AppendSectionHeader(sb, $"Weapon Mods ({mods.Count})");
        sb.AppendLine();

        sb.AppendLine($"Total Weapon Mods: {mods.Count:N0}");
        var withDesc = mods.Count(m => !string.IsNullOrEmpty(m.Description));
        var withModel = mods.Count(m => !string.IsNullOrEmpty(m.ModelPath));
        sb.AppendLine($"  With Description: {withDesc:N0}");
        sb.AppendLine($"  With Model: {withModel:N0}");
        sb.AppendLine();

        foreach (var mod in mods.OrderBy(m => m.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  MOD: {mod.EditorId ?? "(none)"} \u2014 {mod.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {FormatFormId(mod.FormId)}");
            sb.AppendLine($"  Value:       {mod.Value}");
            sb.AppendLine($"  Weight:      {mod.Weight:F2}");
            if (!string.IsNullOrEmpty(mod.Description))
            {
                sb.AppendLine($"  Description: {mod.Description}");
            }

            if (!string.IsNullOrEmpty(mod.ModelPath))
            {
                sb.AppendLine($"  Model:       {mod.ModelPath}");
            }

            if (!string.IsNullOrEmpty(mod.Icon))
            {
                sb.AppendLine($"  Icon:        {mod.Icon}");
            }

            sb.AppendLine();
        }
    }

    private static void AppendRecipesSection(StringBuilder sb, List<ReconstructedRecipe> recipes,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Recipes ({recipes.Count})");
        sb.AppendLine();

        sb.AppendLine($"Total Recipes: {recipes.Count:N0}");
        var withIngredients = recipes.Count(r => r.Ingredients.Count > 0);
        var withOutputs = recipes.Count(r => r.Outputs.Count > 0);
        sb.AppendLine($"  With Ingredients: {withIngredients:N0} ({recipes.Sum(r => r.Ingredients.Count):N0} total)");
        sb.AppendLine($"  With Outputs: {withOutputs:N0} ({recipes.Sum(r => r.Outputs.Count):N0} total)");
        sb.AppendLine();

        foreach (var recipe in recipes.OrderBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  RECIPE: {recipe.EditorId ?? "(none)"} \u2014 {recipe.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:         {FormatFormId(recipe.FormId)}");
            if (recipe.RequiredSkill >= 0)
            {
                sb.AppendLine($"  Required Skill: {recipe.RequiredSkill} (level {recipe.RequiredSkillLevel})");
            }

            if (recipe.CategoryFormId != 0)
            {
                sb.AppendLine($"  Category:       {FormatFormIdWithName(recipe.CategoryFormId, lookup)}");
            }

            if (recipe.SubcategoryFormId != 0)
            {
                sb.AppendLine($"  Subcategory:    {FormatFormIdWithName(recipe.SubcategoryFormId, lookup)}");
            }

            if (recipe.Ingredients.Count > 0)
            {
                sb.AppendLine(
                    $"  \u2500\u2500 Ingredients ({recipe.Ingredients.Count}) {new string('\u2500', 80 - 22 - recipe.Ingredients.Count.ToString().Length)}");
                sb.AppendLine($"  {"Item",-50} {"Count",6}");
                sb.AppendLine($"  {new string('\u2500', 58)}");
                foreach (var ing in recipe.Ingredients)
                {
                    var itemName = ing.ItemFormId != 0
                        ? FormatFormIdWithName(ing.ItemFormId, lookup)
                        : "(none)";
                    sb.AppendLine($"  {Truncate(itemName, 50),-50} {ing.Count,6}");
                }
            }

            if (recipe.Outputs.Count > 0)
            {
                sb.AppendLine(
                    $"  \u2500\u2500 Outputs ({recipe.Outputs.Count}) {new string('\u2500', 80 - 19 - recipe.Outputs.Count.ToString().Length)}");
                sb.AppendLine($"  {"Item",-50} {"Count",6}");
                sb.AppendLine($"  {new string('\u2500', 58)}");
                foreach (var output in recipe.Outputs)
                {
                    var itemName = output.ItemFormId != 0
                        ? FormatFormIdWithName(output.ItemFormId, lookup)
                        : "(none)";
                    sb.AppendLine($"  {Truncate(itemName, 50),-50} {output.Count,6}");
                }
            }

            sb.AppendLine();
        }
    }

    private static void AppendChallengesSection(StringBuilder sb, List<ReconstructedChallenge> challenges,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Challenges ({challenges.Count})");
        sb.AppendLine();

        var byType = challenges.GroupBy(c => c.TypeName).OrderByDescending(g => g.Count()).ToList();
        sb.AppendLine($"Total Challenges: {challenges.Count:N0}");
        sb.AppendLine("By Type:");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key,-30} {group.Count(),5:N0}");
        }

        sb.AppendLine();

        foreach (var chal in challenges.OrderBy(c => c.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  CHALLENGE: {chal.EditorId ?? "(none)"} \u2014 {chal.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {FormatFormId(chal.FormId)}");
            sb.AppendLine($"  Type:        {chal.TypeName}");
            sb.AppendLine($"  Threshold:   {chal.Threshold}");
            if (chal.Interval != 0)
            {
                sb.AppendLine($"  Interval:    {chal.Interval}");
            }

            if (chal.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{chal.Flags:X8}");
            }

            if (!string.IsNullOrEmpty(chal.Description))
            {
                sb.AppendLine($"  Description: {chal.Description}");
            }

            if (chal.Value1 != 0)
            {
                sb.AppendLine($"  Value1:      {chal.Value1}");
            }

            if (chal.Value2 != 0)
            {
                sb.AppendLine($"  Value2:      {chal.Value2}");
            }

            if (chal.Value3 != 0)
            {
                sb.AppendLine($"  Value3:      {chal.Value3}");
            }

            if (chal.Script != 0)
            {
                sb.AppendLine($"  Script:      {FormatFormIdWithName(chal.Script, lookup)}");
            }

            sb.AppendLine();
        }
    }

    private static void AppendReputationsSection(StringBuilder sb, List<ReconstructedReputation> reputations)
    {
        AppendSectionHeader(sb, $"Reputations ({reputations.Count})");
        sb.AppendLine();

        sb.AppendLine($"Total Reputations: {reputations.Count:N0}");
        sb.AppendLine();
        sb.AppendLine($"  {"Name",-40} {"Positive",10} {"Negative",10}  {"FormID"}");
        sb.AppendLine($"  {new string('\u2500', 76)}");

        foreach (var rep in reputations.OrderBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            var name = rep.FullName ?? rep.EditorId ?? FormatFormId(rep.FormId);
            sb.AppendLine(
                $"  {Truncate(name, 40),-40} {rep.PositiveValue,10:F2} {rep.NegativeValue,10:F2}  [{FormatFormId(rep.FormId)}]");
        }

        sb.AppendLine();
    }

    private static void AppendProjectilesSection(StringBuilder sb, List<ReconstructedProjectile> projectiles,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Projectiles ({projectiles.Count})");
        sb.AppendLine();

        var byType = projectiles.GroupBy(p => p.TypeName).OrderByDescending(g => g.Count()).ToList();
        sb.AppendLine($"Total Projectiles: {projectiles.Count:N0}");
        sb.AppendLine("By Type:");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key,-20} {group.Count(),5:N0}");
        }

        if (projectiles.Count > 0)
        {
            sb.AppendLine(
                $"  Speed Range: {projectiles.Min(p => p.Speed):F0} \u2013 {projectiles.Max(p => p.Speed):F0}");
        }

        sb.AppendLine();

        foreach (var proj in projectiles.OrderBy(p => p.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  PROJECTILE: {proj.EditorId ?? "(none)"} \u2014 {proj.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:       {FormatFormId(proj.FormId)}");
            sb.AppendLine($"  Type:         {proj.TypeName}");
            sb.AppendLine($"  \u2500\u2500 Physics {new string('\u2500', 68)}");
            sb.AppendLine($"  Speed:        {proj.Speed:F1}");
            sb.AppendLine($"  Gravity:      {proj.Gravity:F4}");
            sb.AppendLine($"  Range:        {proj.Range:F1}");
            sb.AppendLine($"  Impact Force: {proj.ImpactForce:F1}");
            if (proj.FadeDuration != 0)
            {
                sb.AppendLine($"  Fade Duration: {proj.FadeDuration:F2}");
            }

            if (proj.Timer != 0)
            {
                sb.AppendLine($"  Timer:        {proj.Timer:F2}");
            }

            if (proj.MuzzleFlashDuration != 0 || proj.MuzzleFlashLight != 0)
            {
                sb.AppendLine($"  \u2500\u2500 Muzzle Flash {new string('\u2500', 63)}");
                if (proj.MuzzleFlashDuration != 0)
                {
                    sb.AppendLine($"  Flash Duration: {proj.MuzzleFlashDuration:F2}");
                }

                if (proj.MuzzleFlashLight != 0)
                {
                    sb.AppendLine($"  Flash Light:  {FormatFormIdWithName(proj.MuzzleFlashLight, lookup)}");
                }
            }

            if (proj.Light != 0)
            {
                sb.AppendLine($"  Light:        {FormatFormIdWithName(proj.Light, lookup)}");
            }

            if (proj.Explosion != 0)
            {
                sb.AppendLine($"  Explosion:    {FormatFormIdWithName(proj.Explosion, lookup)}");
            }

            if (proj.Sound != 0)
            {
                sb.AppendLine($"  Sound:        {FormatFormIdWithName(proj.Sound, lookup)}");
            }

            if (!string.IsNullOrEmpty(proj.ModelPath))
            {
                sb.AppendLine($"  Model:        {proj.ModelPath}");
            }

            if (proj.Flags != 0)
            {
                sb.AppendLine($"  Flags:        0x{proj.Flags:X4}");
            }

            sb.AppendLine();
        }
    }

    private static void AppendExplosionsSection(StringBuilder sb, List<ReconstructedExplosion> explosions,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Explosions ({explosions.Count})");
        sb.AppendLine();

        sb.AppendLine($"Total Explosions: {explosions.Count:N0}");
        var withEnchantment = explosions.Count(e => e.Enchantment != 0);
        sb.AppendLine($"  With Enchantment: {withEnchantment:N0}");
        if (explosions.Count > 0)
        {
            sb.AppendLine(
                $"  Damage Range: {explosions.Min(e => e.Damage):F0} \u2013 {explosions.Max(e => e.Damage):F0}");
            sb.AppendLine(
                $"  Radius Range: {explosions.Min(e => e.Radius):F0} \u2013 {explosions.Max(e => e.Radius):F0}");
        }

        sb.AppendLine();

        foreach (var expl in explosions.OrderBy(e => e.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  EXPLOSION: {expl.EditorId ?? "(none)"} \u2014 {expl.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {FormatFormId(expl.FormId)}");
            sb.AppendLine($"  \u2500\u2500 Stats {new string('\u2500', 70)}");
            sb.AppendLine($"  Force:       {expl.Force:F1}");
            sb.AppendLine($"  Damage:      {expl.Damage:F1}");
            sb.AppendLine($"  Radius:      {expl.Radius:F1}");
            sb.AppendLine($"  IS Radius:   {expl.ISRadius:F1}");
            if (expl.Light != 0)
            {
                sb.AppendLine($"  Light:       {FormatFormIdWithName(expl.Light, lookup)}");
            }

            if (expl.Sound1 != 0)
            {
                sb.AppendLine($"  Sound 1:     {FormatFormIdWithName(expl.Sound1, lookup)}");
            }

            if (expl.Sound2 != 0)
            {
                sb.AppendLine($"  Sound 2:     {FormatFormIdWithName(expl.Sound2, lookup)}");
            }

            if (expl.ImpactDataSet != 0)
            {
                sb.AppendLine($"  Impact Data: {FormatFormIdWithName(expl.ImpactDataSet, lookup)}");
            }

            if (expl.Enchantment != 0)
            {
                sb.AppendLine($"  Enchantment: {FormatFormIdWithName(expl.Enchantment, lookup)}");
            }

            if (!string.IsNullOrEmpty(expl.ModelPath))
            {
                sb.AppendLine($"  Model:       {expl.ModelPath}");
            }

            if (expl.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{expl.Flags:X8}");
            }

            sb.AppendLine();
        }
    }

    private static void AppendMessagesSection(StringBuilder sb, List<ReconstructedMessage> messages,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Messages ({messages.Count})");
        sb.AppendLine();

        var messageBoxes = messages.Count(m => m.IsMessageBox);
        var autoDisplay = messages.Count(m => m.IsAutoDisplay);
        var withButtons = messages.Count(m => m.Buttons.Count > 0);
        var withQuest = messages.Count(m => m.QuestFormId != 0);
        sb.AppendLine($"Total Messages: {messages.Count:N0}");
        sb.AppendLine($"  Message Boxes:  {messageBoxes:N0}");
        sb.AppendLine($"  Auto-Display:   {autoDisplay:N0}");
        sb.AppendLine($"  With Buttons:   {withButtons:N0}");
        sb.AppendLine($"  With Quest Link: {withQuest:N0}");
        sb.AppendLine();

        foreach (var msg in messages.OrderBy(m => m.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  MESSAGE: {msg.EditorId ?? "(none)"} \u2014 {msg.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {FormatFormId(msg.FormId)}");
            var flags = new List<string>();
            if (msg.IsMessageBox)
            {
                flags.Add("MessageBox");
            }

            if (msg.IsAutoDisplay)
            {
                flags.Add("AutoDisplay");
            }

            if (flags.Count > 0)
            {
                sb.AppendLine($"  Flags:       {string.Join(", ", flags)}");
            }

            if (msg.DisplayTime != 0)
            {
                sb.AppendLine($"  Display Time: {msg.DisplayTime}");
            }

            if (msg.QuestFormId != 0)
            {
                sb.AppendLine($"  Quest:       {FormatFormIdWithName(msg.QuestFormId, lookup)}");
            }

            if (!string.IsNullOrEmpty(msg.Description))
            {
                sb.AppendLine($"  Text:        {msg.Description}");
            }

            if (msg.Buttons.Count > 0)
            {
                sb.AppendLine(
                    $"  \u2500\u2500 Buttons ({msg.Buttons.Count}) {new string('\u2500', 80 - 18 - msg.Buttons.Count.ToString().Length)}");
                for (var i = 0; i < msg.Buttons.Count; i++)
                {
                    sb.AppendLine($"    [{i + 1}] {msg.Buttons[i]}");
                }
            }

            if (!string.IsNullOrEmpty(msg.Icon))
            {
                sb.AppendLine($"  Icon:        {msg.Icon}");
            }

            sb.AppendLine();
        }
    }

    private static void AppendClassesSection(StringBuilder sb, List<ReconstructedClass> classes)
    {
        AppendSectionHeader(sb, $"Classes ({classes.Count})");
        sb.AppendLine();

        var playable = classes.Count(c => c.IsPlayable);
        var guards = classes.Count(c => c.IsGuard);
        sb.AppendLine($"Total Classes: {classes.Count:N0}");
        sb.AppendLine($"  Playable: {playable:N0}");
        sb.AppendLine($"  Guard:    {guards:N0}");
        sb.AppendLine();

        string[] specialNames = ["Strength", "Perception", "Endurance", "Charisma", "Intelligence", "Agility", "Luck"];
        string[] skillNames =
        [
            "Barter", "Big Guns", "Energy Weapons", "Explosives", "Lockpick", "Medicine", "Melee Weapons",
            "Repair", "Science", "Guns", "Sneak", "Speech", "Survival", "Unarmed"
        ];

        foreach (var cls in classes.OrderBy(c => c.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  CLASS: {cls.EditorId ?? "(none)"} \u2014 {cls.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {FormatFormId(cls.FormId)}");
            var flags = new List<string>();
            if (cls.IsPlayable)
            {
                flags.Add("Playable");
            }

            if (cls.IsGuard)
            {
                flags.Add("Guard");
            }

            if (flags.Count > 0)
            {
                sb.AppendLine($"  Flags:       {string.Join(", ", flags)}");
            }

            if (!string.IsNullOrEmpty(cls.Description))
            {
                sb.AppendLine($"  Description: {cls.Description}");
            }

            if (cls.TagSkills.Length > 0)
            {
                sb.AppendLine($"  \u2500\u2500 Tag Skills {new string('\u2500', 65)}");
                foreach (var skillIdx in cls.TagSkills)
                {
                    var skillName = skillIdx >= 0 && skillIdx < skillNames.Length
                        ? skillNames[skillIdx]
                        : $"#{skillIdx}";
                    sb.AppendLine($"    {skillName}");
                }
            }

            if (cls.AttributeWeights.Length > 0)
            {
                sb.AppendLine($"  \u2500\u2500 Attribute Weights {new string('\u2500', 58)}");
                for (var i = 0; i < cls.AttributeWeights.Length && i < specialNames.Length; i++)
                {
                    sb.AppendLine($"    {specialNames[i],-15} {cls.AttributeWeights[i],3}");
                }
            }

            if (cls.TrainingSkill != 0 || cls.TrainingLevel != 0)
            {
                sb.AppendLine($"  Training:    Skill {cls.TrainingSkill}, Level {cls.TrainingLevel}");
            }

            if (cls.BarterFlags != 0)
            {
                sb.AppendLine($"  Barter Flags: 0x{cls.BarterFlags:X8}");
            }

            sb.AppendLine();
        }
    }

    private static void AppendMapMarkersSection(StringBuilder sb, List<PlacedReference> markers)
    {
        AppendSectionHeader(sb, $"Map Markers ({markers.Count})");
        sb.AppendLine();

        var byType = markers.Where(m => m.MarkerType != null)
            .GroupBy(m => m.MarkerType!.Value)
            .OrderByDescending(g => g.Count())
            .ToList();
        sb.AppendLine($"Total Map Markers: {markers.Count:N0}");
        if (byType.Count > 0)
        {
            sb.AppendLine("By Type:");
            foreach (var group in byType)
            {
                sb.AppendLine($"  {group.Key,-20} {group.Count(),5:N0}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(
            $"  {"Name",-32} {"Type",-18} {"X",10} {"Y",10} {"Z",8}  {"FormID"}");
        sb.AppendLine($"  {new string('\u2500', 76)}");

        foreach (var marker in markers
                     .OrderBy(m => m.MarkerType?.ToString() ?? "")
                     .ThenBy(m => m.MarkerName, StringComparer.OrdinalIgnoreCase))
        {
            var name = marker.MarkerName ?? marker.BaseEditorId ?? FormatFormId(marker.FormId);
            var typeName = marker.MarkerType?.ToString() ?? "(unknown)";
            sb.AppendLine(
                $"  {Truncate(name, 32),-32} {typeName,-18} {marker.X,10:F1} {marker.Y,10:F1} {marker.Z,8:F1}  [{FormatFormId(marker.FormId)}]");
        }

        sb.AppendLine();
    }

    private static void AppendLeveledListsSection(StringBuilder sb, List<ReconstructedLeveledList> lists,
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

    #endregion

    #region Formatting Helpers

    private static string FormatFormId(uint formId)
    {
        return Fmt.FIdAlways(formId);
    }

    private static string FormatFormIdWithName(uint formId, Dictionary<uint, string> lookup)
    {
        return Fmt.FIdWithName(formId, lookup);
    }

    private static string FormatModifier(sbyte value)
    {
        return value switch
        {
            > 0 => $"+{value}",
            < 0 => value.ToString(),
            _ => "+0"
        };
    }

    #endregion

    #region Structured NPC Report

    /// <summary>
    ///     Generate a structured, human-readable per-NPC report with aligned tables
    ///     and display names for all referenced records (factions, inventory, spells, packages).
    /// </summary>
    public static string GenerateNpcReport(
        List<ReconstructedNpc> npcs,
        Dictionary<uint, string> editorIdLookup,
        Dictionary<uint, string> displayNameLookup)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, $"NPC Report ({npcs.Count:N0} NPCs)");
        sb.AppendLine();
        sb.AppendLine($"Total NPCs: {npcs.Count:N0}");

        var withFactions = npcs.Count(n => n.Factions.Count > 0);
        var withInventory = npcs.Count(n => n.Inventory.Count > 0);
        var withSpecial = npcs.Count(n => n.SpecialStats != null);
        var withSkills = npcs.Count(n => n.Skills != null);
        var withAiData = npcs.Count(n => n.AiData != null);
        var withFaceGen = npcs.Count(n => n.FaceGenGeometrySymmetric != null);
        var totalFactionRows = npcs.Sum(n => n.Factions.Count);
        var totalInventoryRows = npcs.Sum(n => n.Inventory.Count);
        sb.AppendLine($"NPCs with S.P.E.C.I.A.L.: {withSpecial:N0}");
        sb.AppendLine($"NPCs with Skills: {withSkills:N0}");
        sb.AppendLine($"NPCs with AI data: {withAiData:N0}");
        sb.AppendLine($"NPCs with FaceGen: {withFaceGen:N0}");
        sb.AppendLine($"NPCs with factions: {withFactions:N0} ({totalFactionRows:N0} total assignments)");
        sb.AppendLine($"NPCs with inventory: {withInventory:N0} ({totalInventoryRows:N0} total items)");

        foreach (var npc in npcs.OrderBy(n => n.EditorId ?? ""))
        {
            AppendNpcReportEntry(sb, npc, editorIdLookup, displayNameLookup);
        }

        return sb.ToString();
    }

    private static void AppendNpcReportEntry(
        StringBuilder sb,
        ReconstructedNpc npc,
        Dictionary<uint, string> editorIdLookup,
        Dictionary<uint, string> displayNameLookup)
    {
        sb.AppendLine();

        // Header with both EditorID and display name
        var title = !string.IsNullOrEmpty(npc.FullName)
            ? $"NPC: {npc.EditorId ?? "(unknown)"} \u2014 {npc.FullName}"
            : $"NPC: {npc.EditorId ?? "(unknown)"}";
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
        var padding = (SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));

        // Basic info
        sb.AppendLine($"  FormID:         {FormatFormId(npc.FormId)}");
        sb.AppendLine($"  Editor ID:      {npc.EditorId ?? "(none)"}");
        sb.AppendLine($"  Display Name:   {npc.FullName ?? "(none)"}");

        if (npc.Stats != null)
        {
            var gender = (npc.Stats.Flags & 1) == 1 ? "Female" : "Male";
            sb.AppendLine($"  Gender:         {gender}");
        }

        // ── Stats ──
        if (npc.Stats != null || npc.SpecialStats != null)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Stats {new string('\u2500', 73)}");

            if (npc.Stats != null)
            {
                sb.AppendLine($"  Level:          {npc.Stats.Level}");
            }

            if (npc.SpecialStats is { Length: 7 })
            {
                var s = npc.SpecialStats;
                var total = s[0] + s[1] + s[2] + s[3] + s[4] + s[5] + s[6];
                sb.AppendLine(
                    $"  S.P.E.C.I.A.L.: {s[0]} ST, {s[1]} PE, {s[2]} EN, {s[3]} CH, {s[4]} IN, {s[5]} AG, {s[6]} LK  (Total: {total})");
            }

            // Skills (14 bytes, skip BigGuns index 1 for FNV)
            if (npc.Skills is { Length: 14 })
            {
                var sk = npc.Skills;
                sb.AppendLine("  Skills:");
                sb.AppendLine(
                    $"    {"Barter",-18}{sk[0],3}    {"Energy Weapons",-18}{sk[2],3}    {"Explosives",-18}{sk[3],3}");
                sb.AppendLine($"    {"Guns",-18}{sk[9],3}    {"Lockpick",-18}{sk[4],3}    {"Medicine",-18}{sk[5],3}");
                sb.AppendLine(
                    $"    {"Melee Weapons",-18}{sk[6],3}    {"Repair",-18}{sk[7],3}    {"Science",-18}{sk[8],3}");
                sb.AppendLine($"    {"Sneak",-18}{sk[10],3}    {"Speech",-18}{sk[11],3}    {"Survival",-18}{sk[12],3}");
                sb.AppendLine($"    {"Unarmed",-18}{sk[13],3}");
            }
        }

        // ── Derived Stats ── (computed from SPECIAL + Level + Fatigue, plus ACBS stats)
        if (npc.Stats != null)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Derived Stats {new string('\u2500', 65)}");

            if (npc.SpecialStats is { Length: 7 } sp2)
            {
                var str = sp2[0];
                var end = sp2[2];
                var lck = sp2[6];
                var baseHealth = end * 5 + 50;
                var calcHealth = baseHealth + npc.Stats.Level * 10;
                var calcFatigue = npc.Stats.FatigueBase + (str + end) * 10;
                var critChance = (float)lck;
                var meleeDamage = str * 0.5f;
                var unarmedDamage = 0.5f + str * 0.1f;
                var poisonResist = (end - 1) * 5f;
                var radResist = (end - 1) * 2f;

                sb.AppendLine($"  {"Base Health:",-18}{baseHealth,-10}{"Calculated Health:",-22}{calcHealth}");
                sb.AppendLine($"  {"Fatigue:",-18}{npc.Stats.FatigueBase,-10}{"Calc Fatigue:",-22}{calcFatigue}");
                sb.AppendLine(
                    $"  {"Critical Chance:",-18}{critChance,-10:F0}{"Speed Mult:",-22}{npc.Stats.SpeedMultiplier}%");
                sb.AppendLine($"  {"Melee Damage:",-18}{meleeDamage,-10:F2}{"Unarmed Damage:",-22}{unarmedDamage:F2}");
                sb.AppendLine($"  {"Poison Resist:",-18}{poisonResist,-10:F2}{"Rad Resist:",-22}{radResist:F2}");
            }
            else
            {
                sb.AppendLine(
                    $"  {"Fatigue:",-18}{npc.Stats.FatigueBase,-10}{"Speed Mult:",-22}{npc.Stats.SpeedMultiplier}%");
            }

            sb.AppendLine($"  {"Karma:",-18}{npc.Stats.KarmaAlignment:F2}{FormatKarmaLabel(npc.Stats.KarmaAlignment)}");
            sb.AppendLine(
                $"  {"Disposition:",-18}{npc.Stats.DispositionBase,-10}{"Barter Gold:",-22}{npc.Stats.BarterGold}");
        }

        // ── Combat ──
        if (npc.Race.HasValue || npc.Class.HasValue || npc.CombatStyleFormId.HasValue || npc.Factions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Combat {new string('\u2500', 72)}");

            if (npc.Race.HasValue)
            {
                sb.AppendLine(
                    $"  Race:           {FormatWithDisplayName(npc.Race.Value, editorIdLookup, displayNameLookup)}");
            }

            if (npc.Class.HasValue)
            {
                sb.AppendLine(
                    $"  Class:          {FormatWithDisplayName(npc.Class.Value, editorIdLookup, displayNameLookup)}");
            }

            if (npc.CombatStyleFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Combat Style:   {FormatWithDisplayName(npc.CombatStyleFormId.Value, editorIdLookup, displayNameLookup)}");
            }
        }

        // ── Physical Traits ──
        if (npc.HairFormId.HasValue || npc.EyesFormId.HasValue || npc.HairLength.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Physical Traits {new string('\u2500', 63)}");

            if (npc.HairFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Hairstyle:      {FormatWithDisplayName(npc.HairFormId.Value, editorIdLookup, displayNameLookup)}");
            }

            if (npc.HairLength.HasValue)
            {
                sb.AppendLine($"  Hair Length:    {npc.HairLength.Value:F2}");
            }

            if (npc.EyesFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Eyes:           {FormatWithDisplayName(npc.EyesFormId.Value, editorIdLookup, displayNameLookup)}");
            }
        }

        // ── AI Data ──
        if (npc.AiData != null)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 AI Data {new string('\u2500', 71)}");
            sb.AppendLine($"  Aggression:     {npc.AiData.AggressionName} ({npc.AiData.Aggression})");
            sb.AppendLine($"  Confidence:     {npc.AiData.ConfidenceName} ({npc.AiData.Confidence})");
            sb.AppendLine($"  Mood:           {npc.AiData.MoodName} ({npc.AiData.Mood})");
            sb.AppendLine($"  Assistance:     {npc.AiData.AssistanceName} ({npc.AiData.Assistance})");
            sb.AppendLine($"  Energy Level:   {npc.AiData.EnergyLevel}");
            sb.AppendLine($"  Responsibility: {npc.AiData.ResponsibilityName} ({npc.AiData.Responsibility})");
        }

        // ── References ──
        if (npc.Script.HasValue || npc.VoiceType.HasValue || npc.Template.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 References {new string('\u2500', 68)}");

            if (npc.Script.HasValue)
            {
                sb.AppendLine($"  Script:         {FormatFormIdWithName(npc.Script.Value, editorIdLookup)}");
            }

            if (npc.VoiceType.HasValue)
            {
                sb.AppendLine($"  Voice Type:     {FormatFormIdWithName(npc.VoiceType.Value, editorIdLookup)}");
            }

            if (npc.Template.HasValue)
            {
                sb.AppendLine(
                    $"  Template:       {FormatWithDisplayName(npc.Template.Value, editorIdLookup, displayNameLookup)}");
            }
        }

        // Factions table
        if (npc.Factions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Factions ({npc.Factions.Count}):");
            sb.AppendLine($"    {"EditorID",-32} {"Name",-32} {"Rank",4}");
            sb.AppendLine($"    {new string('\u2500', 32)} {new string('\u2500', 32)} {new string('\u2500', 4)}");

            foreach (var faction in npc.Factions)
            {
                var editorId = ResolveEditorId(faction.FactionFormId, editorIdLookup);
                var displayName = ResolveDisplayName(faction.FactionFormId, displayNameLookup);
                sb.AppendLine($"    {Truncate(editorId, 32),-32} {Truncate(displayName, 32),-32} {faction.Rank,4}");
            }
        }

        // Inventory table
        if (npc.Inventory.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Inventory ({npc.Inventory.Count}):");
            sb.AppendLine($"    {"EditorID",-32} {"Name",-32} {"Qty",5}");
            sb.AppendLine($"    {new string('\u2500', 32)} {new string('\u2500', 32)} {new string('\u2500', 5)}");

            foreach (var item in npc.Inventory)
            {
                var editorId = ResolveEditorId(item.ItemFormId, editorIdLookup);
                var displayName = ResolveDisplayName(item.ItemFormId, displayNameLookup);
                sb.AppendLine($"    {Truncate(editorId, 32),-32} {Truncate(displayName, 32),-32} {item.Count,5}");
            }
        }

        // Spells table
        if (npc.Spells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Spells/Abilities ({npc.Spells.Count}):");
            sb.AppendLine($"    {"EditorID",-32} {"Name",-32}");
            sb.AppendLine($"    {new string('\u2500', 32)} {new string('\u2500', 32)}");

            foreach (var spellId in npc.Spells)
            {
                var editorId = ResolveEditorId(spellId, editorIdLookup);
                var displayName = ResolveDisplayName(spellId, displayNameLookup);
                sb.AppendLine($"    {Truncate(editorId, 32),-32} {Truncate(displayName, 32),-32}");
            }
        }

        // AI Packages table
        if (npc.Packages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  AI Packages ({npc.Packages.Count}):");
            sb.AppendLine($"    {"EditorID",-32} {"Name",-32}");
            sb.AppendLine($"    {new string('\u2500', 32)} {new string('\u2500', 32)}");

            foreach (var pkgId in npc.Packages)
            {
                var editorId = ResolveEditorId(pkgId, editorIdLookup);
                var displayName = ResolveDisplayName(pkgId, displayNameLookup);
                sb.AppendLine($"    {Truncate(editorId, 32),-32} {Truncate(displayName, 32),-32}");
            }
        }

        // ── FaceGen Morph Data ──
        var hasFaceGen = npc.FaceGenGeometrySymmetric != null ||
                         npc.FaceGenGeometryAsymmetric != null ||
                         npc.FaceGenTextureSymmetric != null;
        if (hasFaceGen)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 FaceGen Morph Data {new string('\u2500', 60)}");

            AppendFaceGenControlSection(sb, "Geometry-Symmetric",
                npc.FaceGenGeometrySymmetric,
                FaceGenControls.ComputeGeometrySymmetric);
            AppendFaceGenRawHex(sb, "FGGS", npc.FaceGenGeometrySymmetric);

            AppendFaceGenControlSection(sb, "Geometry-Asymmetric",
                npc.FaceGenGeometryAsymmetric,
                FaceGenControls.ComputeGeometryAsymmetric);
            AppendFaceGenRawHex(sb, "FGGA", npc.FaceGenGeometryAsymmetric);

            AppendFaceGenControlSection(sb, "Texture-Symmetric",
                npc.FaceGenTextureSymmetric,
                FaceGenControls.ComputeTextureSymmetric);
            AppendFaceGenRawHex(sb, "FGTS", npc.FaceGenTextureSymmetric);
        }
    }

    /// <summary>
    ///     Append a FaceGen control section using CTL-based projections.
    ///     Computes named slider values by projecting basis coefficients (FGGS/FGGA/FGTS)
    ///     through the si.ctl linear control direction vectors.
    ///     Controls are sorted alphabetically and grouped by facial region.
    /// </summary>
    private static void AppendFaceGenControlSection(
        StringBuilder sb,
        string sectionLabel,
        float[]? basisValues,
        Func<float[], (string Name, float Value)[]> computeControls)
    {
        if (basisValues == null || basisValues.Length == 0)
        {
            return;
        }

        // Check if all basis values are zero
        var basisActive = 0;
        foreach (var v in basisValues)
        {
            if (Math.Abs(v) > 0.0001f)
            {
                basisActive++;
            }
        }

        if (basisActive == 0)
        {
            sb.AppendLine($"  {sectionLabel} ({basisValues.Length} basis values): all zero");
            return;
        }

        // Compute named control projections
        var controls = computeControls(basisValues);
        var activeControls = controls.Where(c => Math.Abs(c.Value) > 0.01f).ToList();
        activeControls.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        sb.AppendLine($"  {sectionLabel} ({controls.Length} controls, {activeControls.Count} active):");

        if (activeControls.Count == 0)
        {
            sb.AppendLine("    (all controls near zero)");
            return;
        }

        foreach (var (name, value) in activeControls)
        {
            sb.AppendLine($"    {name,-45} {value,+8:F4}");
        }
    }

    /// <summary>
    ///     Append raw little-endian hex bytes for a FaceGen float array.
    ///     Each float is converted to its IEEE 754 little-endian 4-byte representation
    ///     (PC-compatible format for GECK import/ESM editing).
    ///     This allows exact reproduction without floating-point rounding.
    /// </summary>
    private static void AppendFaceGenRawHex(StringBuilder sb, string label, float[]? values)
    {
        if (values == null || values.Length == 0)
        {
            return;
        }

        // Check if all zero — skip hex if so
        var allZero = true;
        foreach (var v in values)
        {
            if (Math.Abs(v) > 0.0001f)
            {
                allZero = false;
                break;
            }
        }

        if (allZero)
        {
            return;
        }

        sb.AppendLine($"  {label} Raw Hex ({values.Length * 4} bytes, little-endian / PC):");

        // Convert each float to little-endian bytes and format as hex
        var hexLine = new StringBuilder("    ");
        for (var i = 0; i < values.Length; i++)
        {
            var bytes = BitConverter.GetBytes(values[i]);
            // BitConverter gives native endian (LE on x86); reverse if running on BE
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            hexLine.Append($"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}");

            if (i < values.Length - 1)
            {
                hexLine.Append(' ');
            }

            // Line break every 10 floats (40 bytes) for readability
            if ((i + 1) % 10 == 0 && i < values.Length - 1)
            {
                sb.AppendLine(hexLine.ToString().TrimEnd());
                hexLine.Clear();
                hexLine.Append("    ");
            }
        }

        if (hexLine.Length > 4)
        {
            sb.AppendLine(hexLine.ToString().TrimEnd());
        }
    }

    // FaceGen morph names are now computed via CTL-based linear projections.
    // See FaceGenControls.cs (auto-generated from si.ctl by parse_ctl_to_csharp.py)
    // for the authoritative control names and coefficient matrices.

    /// <summary>
    ///     Format a FormID with both EditorID and display name: "EditorId — Display Name (0xFFFFFFFF)"
    /// </summary>
    private static string FormatWithDisplayName(
        uint formId,
        Dictionary<uint, string> editorIdLookup,
        Dictionary<uint, string> displayNameLookup)
    {
        var editorId = editorIdLookup.GetValueOrDefault(formId);
        var displayName = displayNameLookup.GetValueOrDefault(formId);

        if (editorId != null && displayName != null)
        {
            return $"{editorId} \u2014 {displayName} ({FormatFormId(formId)})";
        }

        if (editorId != null)
        {
            return $"{editorId} ({FormatFormId(formId)})";
        }

        if (displayName != null)
        {
            return $"{displayName} ({FormatFormId(formId)})";
        }

        return FormatFormId(formId);
    }

    private static string ResolveEditorId(uint formId, Dictionary<uint, string> lookup)
    {
        return lookup.TryGetValue(formId, out var name) ? name : FormatFormId(formId);
    }

    private static string ResolveDisplayName(uint formId, Dictionary<uint, string> lookup)
    {
        return lookup.TryGetValue(formId, out var name) ? name : "(none)";
    }

    private static string FormatKarmaLabel(float karma)
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

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 1)] + "\u2026";
    }

    #endregion

    #region Structured Weapon Report

    /// <summary>
    ///     Generate a structured, human-readable per-weapon report with aligned sections
    ///     and display names for all referenced records (ammo, projectile, sounds, criticals).
    /// </summary>
    public static string GenerateWeaponReport(
        List<ReconstructedWeapon> weapons,
        Dictionary<uint, string> editorIdLookup,
        Dictionary<uint, string> displayNameLookup)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, $"Weapon Report ({weapons.Count:N0} Weapons)");
        sb.AppendLine();
        sb.AppendLine($"Total Weapons: {weapons.Count:N0}");

        // Summary statistics
        var byType = weapons.GroupBy(w => w.WeaponTypeName).OrderByDescending(g => g.Count());
        sb.AppendLine();
        sb.AppendLine("By Type:");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key,-20} {group.Count(),5:N0}");
        }

        var withAmmo = weapons.Count(w => w.AmmoFormId.HasValue);
        var withProjectile = weapons.Count(w => w.ProjectileFormId.HasValue);
        var withSounds = weapons.Count(w =>
            w.FireSound3DFormId.HasValue || w.DryFireSoundFormId.HasValue ||
            w.EquipSoundFormId.HasValue);
        var withModel = weapons.Count(w => !string.IsNullOrEmpty(w.ModelPath));
        var withProjPhysics = weapons.Count(w => w.ProjectileData != null);
        sb.AppendLine();
        sb.AppendLine($"With Ammo Type:   {withAmmo:N0}");
        sb.AppendLine($"With Projectile:  {withProjectile:N0}");
        sb.AppendLine($"With Proj. Data:  {withProjPhysics:N0}");
        sb.AppendLine($"With Sound FX:    {withSounds:N0}");
        sb.AppendLine($"With Model Path:  {withModel:N0}");

        foreach (var weapon in weapons.OrderBy(w => w.EditorId ?? ""))
        {
            AppendWeaponReportEntry(sb, weapon, editorIdLookup, displayNameLookup);
        }

        return sb.ToString();
    }

    private static void AppendWeaponReportEntry(
        StringBuilder sb,
        ReconstructedWeapon weapon,
        Dictionary<uint, string> editorIdLookup,
        Dictionary<uint, string> displayNameLookup)
    {
        sb.AppendLine();

        // Header
        var title = !string.IsNullOrEmpty(weapon.FullName)
            ? $"WEAPON: {weapon.EditorId ?? "(unknown)"} \u2014 {weapon.FullName}"
            : $"WEAPON: {weapon.EditorId ?? "(unknown)"}";
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
        var padding = (SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));

        // Identity
        sb.AppendLine($"  FormID:         {FormatFormId(weapon.FormId)}");
        sb.AppendLine($"  Editor ID:      {weapon.EditorId ?? "(none)"}");
        sb.AppendLine($"  Display Name:   {weapon.FullName ?? "(none)"}");
        sb.AppendLine($"  Type:           {weapon.WeaponTypeName}");

        // ── Combat Stats ──
        sb.AppendLine();
        sb.AppendLine($"  \u2500\u2500 Combat Stats {new string('\u2500', 66)}");
        sb.AppendLine($"  Damage:         {weapon.Damage}");
        sb.AppendLine($"  DPS:            {weapon.DamagePerSecond:F1}");
        sb.AppendLine($"  Fire Rate:      {weapon.ShotsPerSec:F2}/sec");
        sb.AppendLine($"  Clip Size:      {weapon.ClipSize}");
        sb.AppendLine($"  Range:          {weapon.MinRange:F0} \u2013 {weapon.MaxRange:F0}");
        sb.AppendLine($"  Speed:          {weapon.Speed:F2}");
        sb.AppendLine($"  Reach:          {weapon.Reach:F2}");
        sb.AppendLine($"  Ammo Per Shot:  {weapon.AmmoPerShot}");
        sb.AppendLine($"  Projectiles:    {weapon.NumProjectiles}");

        // ── Accuracy ──
        sb.AppendLine();
        sb.AppendLine($"  \u2500\u2500 Accuracy {new string('\u2500', 70)}");
        sb.AppendLine($"  Spread:         {weapon.Spread:F2}");
        sb.AppendLine($"  Min Spread:     {weapon.MinSpread:F2}");
        sb.AppendLine($"  Drift:          {weapon.Drift:F2}");

        // ── VATS ──
        sb.AppendLine();
        sb.AppendLine($"  \u2500\u2500 VATS {new string('\u2500', 74)}");
        sb.AppendLine($"  AP Cost:        {weapon.ActionPoints:F0}");
        sb.AppendLine($"  Hit Chance:     {weapon.VatsToHitChance}");

        // ── Requirements ──
        if (weapon.StrengthRequirement > 0 || weapon.SkillRequirement > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Requirements {new string('\u2500', 65)}");
            if (weapon.StrengthRequirement > 0)
            {
                sb.AppendLine($"  Strength:       {weapon.StrengthRequirement}");
            }

            if (weapon.SkillRequirement > 0)
            {
                sb.AppendLine($"  Skill:          {weapon.SkillRequirement}");
            }
        }

        // ── Critical ──
        if (weapon.CriticalDamage != 0 || Math.Abs(weapon.CriticalChance - 1.0f) > 0.01f ||
            weapon.CriticalEffectFormId.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Critical {new string('\u2500', 70)}");
            sb.AppendLine($"  Damage:         {weapon.CriticalDamage}");
            sb.AppendLine($"  Chance:         x{weapon.CriticalChance:F1}");
            if (weapon.CriticalEffectFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Effect:         {FormatWithDisplayName(weapon.CriticalEffectFormId.Value, editorIdLookup, displayNameLookup)}");
            }
        }

        // ── Value / Weight ──
        sb.AppendLine();
        sb.AppendLine($"  \u2500\u2500 Value / Weight {new string('\u2500', 64)}");
        sb.AppendLine($"  Value:          {weapon.Value} caps");
        sb.AppendLine($"  Weight:         {weapon.Weight:F1}");
        sb.AppendLine($"  Health:         {weapon.Health}");

        // ── Ammo & Projectile ──
        if (weapon.AmmoFormId.HasValue || weapon.ProjectileFormId.HasValue ||
            weapon.ImpactDataSetFormId.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Ammo & Projectile {new string('\u2500', 61)}");
            if (weapon.AmmoFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Ammo:           {FormatWithDisplayName(weapon.AmmoFormId.Value, editorIdLookup, displayNameLookup)}");
            }

            if (weapon.ProjectileFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Projectile:     {FormatWithDisplayName(weapon.ProjectileFormId.Value, editorIdLookup, displayNameLookup)}");
            }

            if (weapon.ImpactDataSetFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Impact Data:    {FormatWithDisplayName(weapon.ImpactDataSetFormId.Value, editorIdLookup, displayNameLookup)}");
            }
        }

        // ── Projectile Physics ──
        if (weapon.ProjectileData != null)
        {
            var proj = weapon.ProjectileData;
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Projectile Physics {new string('\u2500', 60)}");
            sb.AppendLine($"  Speed:          {proj.Speed:F1} units/sec");
            sb.AppendLine($"  Gravity:        {proj.Gravity:F4}");
            sb.AppendLine($"  Range:          {proj.Range:F0}");
            sb.AppendLine($"  Force:          {proj.Force:F1}");

            if (proj.MuzzleFlashDuration > 0)
            {
                sb.AppendLine($"  Muzzle Flash:   {proj.MuzzleFlashDuration:F3}s");
            }

            if (proj.ExplosionFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Explosion:      {FormatWithDisplayName(proj.ExplosionFormId.Value, editorIdLookup, displayNameLookup)}");
            }

            if (proj.ActiveSoundLoopFormId.HasValue)
            {
                sb.AppendLine(
                    $"  In-Flight Snd:  {FormatFormIdWithName(proj.ActiveSoundLoopFormId.Value, editorIdLookup)}");
            }

            if (proj.CountdownSoundFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Countdown Snd:  {FormatFormIdWithName(proj.CountdownSoundFormId.Value, editorIdLookup)}");
            }

            if (proj.DeactivateSoundFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Deactivate Snd: {FormatFormIdWithName(proj.DeactivateSoundFormId.Value, editorIdLookup)}");
            }

            if (!string.IsNullOrEmpty(proj.ModelPath))
            {
                sb.AppendLine($"  Proj. Model:    {proj.ModelPath}");
            }
        }

        // ── Sound Effects ──
        var hasSounds = weapon.FireSound3DFormId.HasValue || weapon.FireSoundDistFormId.HasValue ||
                        weapon.FireSound2DFormId.HasValue || weapon.DryFireSoundFormId.HasValue ||
                        weapon.IdleSoundFormId.HasValue || weapon.EquipSoundFormId.HasValue ||
                        weapon.UnequipSoundFormId.HasValue || weapon.PickupSoundFormId.HasValue ||
                        weapon.PutdownSoundFormId.HasValue;
        if (hasSounds)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Sound Effects {new string('\u2500', 65)}");

            AppendSoundLine(sb, "Fire (3D):", weapon.FireSound3DFormId, editorIdLookup);
            AppendSoundLine(sb, "Fire (Distant):", weapon.FireSoundDistFormId, editorIdLookup);
            AppendSoundLine(sb, "Fire (2D):", weapon.FireSound2DFormId, editorIdLookup);
            AppendSoundLine(sb, "Dry Fire:", weapon.DryFireSoundFormId, editorIdLookup);
            AppendSoundLine(sb, "Idle:", weapon.IdleSoundFormId, editorIdLookup);
            AppendSoundLine(sb, "Equip:", weapon.EquipSoundFormId, editorIdLookup);
            AppendSoundLine(sb, "Unequip:", weapon.UnequipSoundFormId, editorIdLookup);
            AppendSoundLine(sb, "Pickup:", weapon.PickupSoundFormId, editorIdLookup);
            AppendSoundLine(sb, "Putdown:", weapon.PutdownSoundFormId, editorIdLookup);
        }

        // ── Model ──
        if (!string.IsNullOrEmpty(weapon.ModelPath))
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Model {new string('\u2500', 73)}");
            sb.AppendLine($"  Path:           {weapon.ModelPath}");
        }
    }

    private static void AppendSoundLine(
        StringBuilder sb,
        string label,
        uint? formId,
        Dictionary<uint, string> editorIdLookup)
    {
        if (!formId.HasValue)
        {
            return;
        }

        // Sounds use EditorID only (TESSound has no TESFullName)
        sb.AppendLine($"  {label,-17} {FormatFormIdWithName(formId.Value, editorIdLookup)}");
    }

    /// <summary>
    ///     Generate a structured, human-readable per-container report with aligned tables
    ///     and display names for all referenced records (contents, scripts).
    /// </summary>
    public static string GenerateContainerReport(
        List<ReconstructedContainer> containers,
        Dictionary<uint, string> editorIdLookup,
        Dictionary<uint, string> displayNameLookup)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, $"Container Report ({containers.Count:N0} Containers)");
        sb.AppendLine();
        sb.AppendLine($"Total Containers: {containers.Count:N0}");
        sb.AppendLine();

        var withContents = containers.Count(c => c.Contents.Count > 0);
        var totalItems = containers.Sum(c => c.Contents.Sum(i => i.Count));
        var respawning = containers.Count(c => c.Respawns);
        var withScript = containers.Count(c => c.Script.HasValue);
        var withModel = containers.Count(c => !string.IsNullOrEmpty(c.ModelPath));

        sb.AppendLine($"With Contents:    {withContents:N0} ({totalItems:N0} total items)");
        sb.AppendLine($"Respawning:       {respawning:N0}");
        sb.AppendLine($"With Script:      {withScript:N0}");
        sb.AppendLine($"With Model Path:  {withModel:N0}");

        foreach (var container in containers.OrderBy(c => c.EditorId ?? ""))
        {
            AppendContainerReportEntry(sb, container, editorIdLookup, displayNameLookup);
        }

        return sb.ToString();
    }

    private static void AppendContainerReportEntry(
        StringBuilder sb,
        ReconstructedContainer container,
        Dictionary<uint, string> editorIdLookup,
        Dictionary<uint, string> displayNameLookup)
    {
        sb.AppendLine();

        // Header with both EditorID and display name
        var title = !string.IsNullOrEmpty(container.FullName)
            ? $"CONTAINER: {container.EditorId ?? "(unknown)"} \u2014 {container.FullName}"
            : $"CONTAINER: {container.EditorId ?? "(unknown)"}";
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
        var padding = (SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));

        // Basic info
        sb.AppendLine($"  FormID:         {FormatFormId(container.FormId)}");
        sb.AppendLine($"  Editor ID:      {container.EditorId ?? "(none)"}");
        sb.AppendLine($"  Display Name:   {container.FullName ?? "(none)"}");
        sb.AppendLine($"  Respawns:       {(container.Respawns ? "Yes" : "No")}");

        // Contents table
        if (container.Contents.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                $"  \u2500\u2500 Contents ({container.Contents.Count} items) {new string('\u2500', 56 - container.Contents.Count.ToString().Length)}");
            sb.AppendLine($"    {"EditorID",-32} {"Name",-32} {"Qty",5}");
            sb.AppendLine($"    {new string('\u2500', 32)} {new string('\u2500', 32)} {new string('\u2500', 5)}");

            foreach (var item in container.Contents)
            {
                var editorId = ResolveEditorId(item.ItemFormId, editorIdLookup);
                var displayName = ResolveDisplayName(item.ItemFormId, displayNameLookup);
                sb.AppendLine($"    {Truncate(editorId, 32),-32} {Truncate(displayName, 32),-32} {item.Count,5}");
            }
        }

        // Script reference
        if (container.Script.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 References {new string('\u2500', 67)}");
            sb.AppendLine(
                $"  Script:         {FormatWithDisplayName(container.Script.Value, editorIdLookup, displayNameLookup)}");
        }

        // Model path
        if (!string.IsNullOrEmpty(container.ModelPath))
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Model {new string('\u2500', 73)}");
            sb.AppendLine($"  Path:           {container.ModelPath}");
        }
    }

    #endregion

    #region String Pool Section

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

    private static string FormatPoolSize(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }

    #endregion
}
