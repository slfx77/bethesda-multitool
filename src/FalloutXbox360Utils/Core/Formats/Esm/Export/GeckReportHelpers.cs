using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Static utility and formatting helpers shared by all Geck*Writer report classes.
///     Extracted from GeckReportGenerator to keep that class focused on report orchestration.
/// </summary>
internal static class GeckReportHelpers
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
    ///     Tree node for hierarchical path grouping in asset reports.
    /// </summary>
    internal sealed class PathTreeNode
    {
        public Dictionary<string, PathTreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Files { get; } = [];
    }

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

    /// <summary>
    ///     Appends the summary statistics section to a report.
    /// </summary>
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
}
