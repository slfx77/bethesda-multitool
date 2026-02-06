using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports from semantic reconstruction results.
/// </summary>
public static partial class GeckReportGenerator
{
    private const int SeparatorWidth = 80;

    private const char SeparatorChar = '=';

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
            AppendCreaturesSection(sb, result.Creatures);
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
            AppendNotesSection(sb, result.Notes);
        }

        if (result.Books.Count > 0)
        {
            AppendBooksSection(sb, result.Books);
        }

        if (result.Terminals.Count > 0)
        {
            AppendTerminalsSection(sb, result.Terminals);
        }

        if (result.Dialogues.Count > 0)
        {
            AppendDialogueSection(sb, result.Dialogues, lookup);
        }

        // Scripts
        if (result.Scripts.Count > 0)
        {
            AppendScriptsSection(sb, result.Scripts, lookup);
        }

        // Items
        if (result.Weapons.Count > 0)
        {
            AppendWeaponsSection(sb, result.Weapons, lookup);
        }

        if (result.Armor.Count > 0)
        {
            AppendArmorSection(sb, result.Armor);
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
            AppendMiscItemsSection(sb, result.MiscItems);
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
            AppendCellsSection(sb, result.Cells);
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
    ///     Tree node for hierarchical path grouping in asset reports.
    /// </summary>
    private sealed class PathTreeNode
    {
        public Dictionary<string, PathTreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Files { get; } = [];
    }

    #region Helpers

    private static string FormatFormId(uint formId)
    {
        return Fmt.FIdAlways(formId);
    }

    private static string CsvEscape(string? value)
    {
        return Fmt.CsvEscape(value);
    }

    private static void AppendHeader(StringBuilder sb, string title)
    {
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
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

    private static string FormatFormIdWithName(uint formId, Dictionary<uint, string> lookup)
    {
        return Fmt.FIdWithName(formId, lookup);
    }

    /// <summary>
    ///     Format a FormID with both EditorID and display name: "EditorId - Display Name (0xFFFFFFFF)"
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

    private static string FormatModifier(sbyte value)
    {
        return value switch
        {
            > 0 => $"+{value}",
            < 0 => value.ToString(),
            _ => "+0"
        };
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

    private static string FormatPoolSize(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B"
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

    private static string ResolveEditorId(uint formId, Dictionary<uint, string> lookup)
    {
        return lookup.TryGetValue(formId, out var name) ? name : FormatFormId(formId);
    }

    private static string ResolveDisplayName(uint formId, Dictionary<uint, string> lookup)
    {
        return lookup.TryGetValue(formId, out var name) ? name : "(none)";
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
    ///     Strip junk prefixes from asset paths by finding the first known root directory segment.
    ///     Handles both exact matches ("meshes\...") and junk-prefixed segments where garbage
    ///     bytes are prepended to a known root ("ABASE Architecture\..." -> "Architecture\...").
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
            // e.g. "ABASE Architecture" -> strip to "Architecture"
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

    #endregion
}
