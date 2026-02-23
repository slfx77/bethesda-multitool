using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports for Container and Recipe records.
/// </summary>
internal static class GeckContainerWriter
{
    internal static void AppendContainersSection(StringBuilder sb, List<ContainerRecord> containers,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Containers ({containers.Count})");

        foreach (var container in containers.OrderBy(c => c.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "CONT", container.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(container.FormId)}");
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
                    sb.AppendLine($"  - {resolver.FormatFull(item.ItemFormId)} x{item.Count}");
                }
            }
        }
    }

    internal static void AppendContainerReportEntry(
        StringBuilder sb,
        ContainerRecord container,
        FormIdResolver resolver)
    {
        sb.AppendLine();

        // Header with both EditorID and display name
        var title = !string.IsNullOrEmpty(container.FullName)
            ? $"CONTAINER: {container.EditorId ?? "(unknown)"} \u2014 {container.FullName}"
            : $"CONTAINER: {container.EditorId ?? "(unknown)"}";
        sb.AppendLine(new string(GeckReportHelpers.SeparatorChar, GeckReportHelpers.SeparatorWidth));
        var padding = (GeckReportHelpers.SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(GeckReportHelpers.SeparatorChar, GeckReportHelpers.SeparatorWidth));

        // Basic info
        sb.AppendLine($"  FormID:         {GeckReportHelpers.FormatFormId(container.FormId)}");
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
                var editorId = resolver.ResolveEditorId(item.ItemFormId);
                var displayName = resolver.ResolveDisplayName(item.ItemFormId);
                sb.AppendLine(
                    $"    {GeckReportHelpers.Truncate(editorId, 32),-32} {GeckReportHelpers.Truncate(displayName, 32),-32} {item.Count,5}");
            }
        }

        // Script reference
        if (container.Script.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 References {new string('\u2500', 67)}");
            sb.AppendLine(
                $"  Script:         {resolver.FormatFull(container.Script.Value)}");
        }

        // Model path
        if (!string.IsNullOrEmpty(container.ModelPath))
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Model {new string('\u2500', 73)}");
            sb.AppendLine($"  Path:           {container.ModelPath}");
        }
    }

    /// <summary>
    ///     Generate a report for Containers only.
    /// </summary>
    public static string GenerateContainersReport(List<ContainerRecord> containers,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendContainersSection(sb, containers, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a structured, human-readable per-container report with aligned tables
    ///     and display names for all referenced records (contents, scripts).
    /// </summary>
    public static string GenerateContainerReport(
        List<ContainerRecord> containers,
        FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        GeckReportHelpers.AppendHeader(sb, $"Container Report ({containers.Count:N0} Containers)");
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
            AppendContainerReportEntry(sb, container, resolver);
        }

        return sb.ToString();
    }

    internal static void AppendRecipesSection(StringBuilder sb, List<RecipeRecord> recipes,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Recipes ({recipes.Count})");
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
            sb.AppendLine($"  FormID:         {GeckReportHelpers.FormatFormId(recipe.FormId)}");
            if (recipe.RequiredSkill >= 0)
            {
                sb.AppendLine($"  Required Skill: {recipe.RequiredSkill} (level {recipe.RequiredSkillLevel})");
            }

            if (recipe.CategoryFormId != 0)
            {
                sb.AppendLine($"  Category:       {resolver.FormatFull(recipe.CategoryFormId)}");
            }

            if (recipe.SubcategoryFormId != 0)
            {
                sb.AppendLine($"  Subcategory:    {resolver.FormatFull(recipe.SubcategoryFormId)}");
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
                        ? resolver.FormatFull(ing.ItemFormId)
                        : "(none)";
                    sb.AppendLine($"  {GeckReportHelpers.Truncate(itemName, 50),-50} {ing.Count,6}");
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
                        ? resolver.FormatFull(output.ItemFormId)
                        : "(none)";
                    sb.AppendLine($"  {GeckReportHelpers.Truncate(itemName, 50),-50} {output.Count,6}");
                }
            }

            sb.AppendLine();
        }
    }

    public static string GenerateRecipesReport(List<RecipeRecord> recipes,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendRecipesSection(sb, recipes, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }
}
