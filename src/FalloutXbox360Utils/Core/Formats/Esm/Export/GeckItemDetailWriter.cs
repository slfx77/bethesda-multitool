using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Detailed item stat/effect formatting methods for weapon reports, container reports,
///     recipes, and weapon mods. Extracted from GeckItemWriter.
///     Weapon report building is delegated to <see cref="GeckWeaponReportWriter" />.
/// </summary>
internal static class GeckItemDetailWriter
{
    /// <summary>
    ///     Build a structured weapon report from a <see cref="WeaponRecord" />.
    ///     This is the canonical data source — text/JSON/CSV formatters consume this.
    /// </summary>
    internal static RecordReport BuildWeaponReport(WeaponRecord weapon, FormIdResolver resolver)
    {
        return GeckWeaponReportWriter.BuildWeaponReport(weapon, resolver);
    }

    internal static void AppendWeaponReportEntry(
        StringBuilder sb,
        WeaponRecord weapon,
        FormIdResolver resolver)
    {
        GeckWeaponReportWriter.AppendWeaponReportEntry(sb, weapon, resolver);
    }

    /// <summary>
    ///     Generate a structured, human-readable per-weapon report with aligned sections
    ///     and display names for all referenced records (ammo, projectile, sounds, criticals).
    /// </summary>
    public static string GenerateWeaponReport(
        List<WeaponRecord> weapons,
        FormIdResolver resolver)
    {
        return GeckWeaponReportWriter.GenerateWeaponReport(weapons, resolver);
    }

    /// <summary>
    ///     Build a structured container report from a <see cref="ContainerRecord" />.
    /// </summary>
    internal static RecordReport BuildContainerReport(ContainerRecord container, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        sections.Add(new ReportSection("Identity",
        [
            new ReportField("Respawns", ReportValue.Bool(container.Respawns))
        ]));

        // Contents
        if (container.Contents.Count > 0)
        {
            var items = container.Contents.OrderBy(i => i.ItemFormId)
                .Select(item =>
                {
                    var editorId = resolver.ResolveEditorId(item.ItemFormId);
                    var displayName = resolver.ResolveDisplayName(item.ItemFormId);
                    return (ReportValue)new ReportValue.CompositeVal(
                        [
                            new ReportField("EditorID", ReportValue.String(editorId)),
                            new ReportField("Name", ReportValue.String(displayName)),
                            new ReportField("Qty", ReportValue.Int(item.Count))
                        ], $"{editorId} x{item.Count}");
                })
                .ToList();

            sections.Add(new ReportSection($"Contents ({container.Contents.Count} items)",
            [
                new ReportField("Items", ReportValue.List(items))
            ]));
        }

        // References
        if (container.Script.HasValue)
        {
            sections.Add(new ReportSection("References",
            [
                new ReportField("Script", ReportValue.FormId(container.Script.Value, resolver),
                    $"0x{container.Script.Value:X8}")
            ]));
        }

        // Model
        if (!string.IsNullOrEmpty(container.ModelPath))
        {
            sections.Add(new ReportSection("Model",
            [
                new ReportField("Path", ReportValue.String(container.ModelPath))
            ]));
        }

        return new RecordReport("Container", container.FormId, container.EditorId, container.FullName,
            sections);
    }

    internal static void AppendContainerReportEntry(
        StringBuilder sb,
        ContainerRecord container,
        FormIdResolver resolver)
    {
        sb.AppendLine();
        var report = BuildContainerReport(container, resolver);
        sb.Append(ReportTextFormatter.Format(report));
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

    /// <summary>
    ///     Build a structured recipe report from a <see cref="RecipeRecord" />.
    /// </summary>
    internal static RecordReport BuildRecipeReport(RecipeRecord recipe, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        var identityFields = new List<ReportField>();
        if (recipe.RequiredSkill >= 0)
        {
            var skillName = resolver.GetActorValueName(recipe.RequiredSkill) ?? $"AV#{recipe.RequiredSkill}";
            identityFields.Add(new ReportField("Required Skill",
                ReportValue.Int(recipe.RequiredSkill, $"{skillName} (level {recipe.RequiredSkillLevel})")));
        }

        if (recipe.CategoryFormId != 0)
            identityFields.Add(new ReportField("Category",
                ReportValue.FormId(recipe.CategoryFormId, resolver),
                $"0x{recipe.CategoryFormId:X8}"));
        if (recipe.SubcategoryFormId != 0)
            identityFields.Add(new ReportField("Subcategory",
                ReportValue.FormId(recipe.SubcategoryFormId, resolver),
                $"0x{recipe.SubcategoryFormId:X8}"));
        if (identityFields.Count > 0)
            sections.Add(new ReportSection("Identity", identityFields));

        // Ingredients
        if (recipe.Ingredients.Count > 0)
        {
            var items = recipe.Ingredients.OrderBy(i => i.ItemFormId)
                .Select(ing =>
                {
                    var itemName = ing.ItemFormId != 0
                        ? resolver.FormatFull(ing.ItemFormId)
                        : "(none)";
                    return (ReportValue)new ReportValue.CompositeVal(
                        [
                            new ReportField("Item", ReportValue.String(itemName)),
                            new ReportField("Count", ReportValue.Int((int)ing.Count))
                        ], $"{itemName} x{ing.Count}");
                })
                .ToList();

            sections.Add(new ReportSection($"Ingredients ({recipe.Ingredients.Count})",
            [
                new ReportField("Items", ReportValue.List(items))
            ]));
        }

        // Outputs
        if (recipe.Outputs.Count > 0)
        {
            var items = recipe.Outputs.OrderBy(o => o.ItemFormId)
                .Select(output =>
                {
                    var itemName = output.ItemFormId != 0
                        ? resolver.FormatFull(output.ItemFormId)
                        : "(none)";
                    return (ReportValue)new ReportValue.CompositeVal(
                        [
                            new ReportField("Item", ReportValue.String(itemName)),
                            new ReportField("Count", ReportValue.Int((int)output.Count))
                        ], $"{itemName} x{output.Count}");
                })
                .ToList();

            sections.Add(new ReportSection($"Outputs ({recipe.Outputs.Count})",
            [
                new ReportField("Items", ReportValue.List(items))
            ]));
        }

        return new RecordReport("Recipe", recipe.FormId, recipe.EditorId, recipe.FullName, sections);
    }

    /// <summary>
    ///     Build a structured weapon mod report from a <see cref="WeaponModRecord" />.
    /// </summary>
    internal static RecordReport BuildWeaponModReport(
        WeaponModRecord mod, FormIdResolver resolver,
        IReadOnlyList<(WeaponRecord Weapon, WeaponModSlot Slot)>? weaponEffects = null)
    {
        var sections = new List<ReportSection>();

        var valueFields = new List<ReportField>
        {
            new("Value", ReportValue.Int(mod.Value)),
            new("Weight", ReportValue.Float(mod.Weight, "F2"))
        };
        if (!string.IsNullOrEmpty(mod.Description))
            valueFields.Add(new ReportField("Description", ReportValue.String(mod.Description)));
        if (!string.IsNullOrEmpty(mod.ModelPath))
            valueFields.Add(new ReportField("Model", ReportValue.String(mod.ModelPath)));
        if (!string.IsNullOrEmpty(mod.Icon))
            valueFields.Add(new ReportField("Icon", ReportValue.String(mod.Icon)));

        sections.Add(new ReportSection("Properties", valueFields));

        // Effects (from weapon definitions — what this mod does when attached)
        if (weaponEffects is { Count: > 0 })
        {
            var effectItems = weaponEffects
                .OrderBy(e => e.Weapon.FullName ?? e.Weapon.EditorId ?? "")
                .Select(e =>
                {
                    var fields = new List<ReportField>
                    {
                        new("Weapon", ReportValue.FormId(e.Weapon.FormId, resolver),
                            $"0x{e.Weapon.FormId:X8}"),
                        new("Effect", ReportValue.String(e.Slot.ActionName)),
                        new("Value", ReportValue.Float(e.Slot.Value))
                    };
                    if (MathF.Abs(e.Slot.ValueTwo) > 0.001f)
                        fields.Add(new ReportField("Value 2", ReportValue.Float(e.Slot.ValueTwo)));

                    var weaponName = e.Weapon.FullName ?? e.Weapon.EditorId ?? $"0x{e.Weapon.FormId:X8}";
                    return (ReportValue)new ReportValue.CompositeVal(fields,
                        $"{weaponName}: {e.Slot.ActionName} {e.Slot.Value:G}");
                })
                .ToList();

            sections.Add(new ReportSection($"Effects ({weaponEffects.Count})",
            [
                new ReportField("Effects", ReportValue.List(effectItems))
            ]));
        }

        return new RecordReport("Weapon Mod", mod.FormId, mod.EditorId, mod.FullName, sections);
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
                foreach (var ing in recipe.Ingredients.OrderBy(i => i.ItemFormId))
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
                foreach (var output in recipe.Outputs.OrderBy(o => o.ItemFormId))
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

    internal static void AppendWeaponModsSection(StringBuilder sb, List<WeaponModRecord> mods)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Weapon Mods ({mods.Count})");
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
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(mod.FormId)}");
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

    public static string GenerateWeaponModsReport(List<WeaponModRecord> mods)
    {
        var sb = new StringBuilder();
        AppendWeaponModsSection(sb, mods);
        return sb.ToString();
    }
}
