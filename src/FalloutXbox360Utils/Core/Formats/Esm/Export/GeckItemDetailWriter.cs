using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Detailed item stat/effect formatting methods for weapon reports, container reports,
///     recipes, and weapon mods. Extracted from GeckItemWriter.
/// </summary>
internal static class GeckItemDetailWriter
{
    /// <summary>
    ///     Build a structured weapon report from a <see cref="WeaponRecord" />.
    ///     This is the canonical data source — text/JSON/CSV formatters consume this.
    /// </summary>
    internal static RecordReport BuildWeaponReport(WeaponRecord weapon, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity (type field only — FormID/EditorID/DisplayName are on RecordReport itself)
        sections.Add(new ReportSection("Identity",
        [
            new ReportField("Type", ReportValue.String(weapon.WeaponTypeName))
        ]));

        // Combat Stats
        sections.Add(new ReportSection("Combat Stats",
        [
            new ReportField("Damage", ReportValue.Int(weapon.Damage)),
            new ReportField("DPS", ReportValue.FloatDisplay(weapon.DamagePerSecond, $"{weapon.DamagePerSecond:F1}")),
            new ReportField("Fire Rate", ReportValue.FloatDisplay(weapon.ShotsPerSec, $"{weapon.ShotsPerSec:F2}/sec")),
            new ReportField("Clip Size", ReportValue.Int(weapon.ClipSize)),
            new ReportField("Range", ReportValue.String($"{weapon.MinRange:F0} \u2013 {weapon.MaxRange:F0}")),
            new ReportField("Speed", ReportValue.Float(weapon.Speed, "F2")),
            new ReportField("Reach", ReportValue.Float(weapon.Reach, "F2")),
            new ReportField("Ammo Per Shot", ReportValue.Int(weapon.AmmoPerShot)),
            new ReportField("Projectiles", ReportValue.Int(weapon.NumProjectiles))
        ]));

        // Accuracy
        sections.Add(new ReportSection("Accuracy",
        [
            new ReportField("Spread", ReportValue.Float(weapon.Spread, "F2")),
            new ReportField("Min Spread", ReportValue.Float(weapon.MinSpread, "F2")),
            new ReportField("Drift", ReportValue.Float(weapon.Drift, "F2"))
        ]));

        // VATS
        sections.Add(new ReportSection("VATS",
        [
            new ReportField("AP Cost", ReportValue.FloatDisplay(weapon.ActionPoints, $"{weapon.ActionPoints:F0}")),
            new ReportField("Hit Chance", ReportValue.Int(weapon.VatsToHitChance))
        ]));

        // Requirements (conditional)
        if (weapon.StrengthRequirement > 0 || weapon.SkillRequirement > 0)
        {
            var reqFields = new List<ReportField>();
            if (weapon.StrengthRequirement > 0)
                reqFields.Add(new ReportField("Strength", ReportValue.Int((int)weapon.StrengthRequirement)));
            if (weapon.SkillRequirement > 0)
                reqFields.Add(new ReportField("Skill", ReportValue.Int((int)weapon.SkillRequirement)));
            sections.Add(new ReportSection("Requirements", reqFields));
        }

        // Critical (conditional)
        if (weapon.CriticalDamage != 0 || Math.Abs(weapon.CriticalChance - 1.0f) > 0.01f ||
            weapon.CriticalEffectFormId.HasValue)
        {
            var critFields = new List<ReportField>
            {
                new("Damage", ReportValue.Int(weapon.CriticalDamage)),
                new("Chance", ReportValue.FloatDisplay(weapon.CriticalChance, $"x{weapon.CriticalChance:F1}"))
            };
            if (weapon.CriticalEffectFormId.HasValue)
                critFields.Add(new ReportField("Effect",
                    ReportValue.FormId(weapon.CriticalEffectFormId.Value, resolver),
                    $"0x{weapon.CriticalEffectFormId.Value:X8}"));
            sections.Add(new ReportSection("Critical", critFields));
        }

        // Value / Weight
        sections.Add(new ReportSection("Value / Weight",
        [
            new ReportField("Value", ReportValue.Int(weapon.Value, $"{weapon.Value} caps")),
            new ReportField("Weight", ReportValue.Float(weapon.Weight)),
            new ReportField("Health", ReportValue.Int(weapon.Health))
        ]));

        // Ammo & Projectile (conditional)
        if (weapon.AmmoFormId.HasValue || weapon.ProjectileFormId.HasValue ||
            weapon.ImpactDataSetFormId.HasValue)
        {
            var ammoFields = new List<ReportField>();
            if (weapon.AmmoFormId.HasValue)
                ammoFields.Add(new ReportField("Ammo",
                    ReportValue.FormId(weapon.AmmoFormId.Value, resolver),
                    $"0x{weapon.AmmoFormId.Value:X8}"));
            if (weapon.ProjectileFormId.HasValue)
                ammoFields.Add(new ReportField("Projectile",
                    ReportValue.FormId(weapon.ProjectileFormId.Value, resolver),
                    $"0x{weapon.ProjectileFormId.Value:X8}"));
            if (weapon.ImpactDataSetFormId.HasValue)
                ammoFields.Add(new ReportField("Impact Data",
                    ReportValue.FormId(weapon.ImpactDataSetFormId.Value, resolver),
                    $"0x{weapon.ImpactDataSetFormId.Value:X8}"));
            sections.Add(new ReportSection("Ammo & Projectile", ammoFields));
        }

        // Projectile Physics (conditional)
        if (weapon.ProjectileData != null)
        {
            var proj = weapon.ProjectileData;
            var projFields = new List<ReportField>
            {
                new("Speed", ReportValue.FloatDisplay(proj.Speed, $"{proj.Speed:F1} units/sec")),
                new("Gravity", ReportValue.Float(proj.Gravity, "F4")),
                new("Range", ReportValue.FloatDisplay(proj.Range, $"{proj.Range:F0}")),
                new("Force", ReportValue.Float(proj.Force))
            };
            if (proj.MuzzleFlashDuration > 0)
                projFields.Add(new ReportField("Muzzle Flash",
                    ReportValue.FloatDisplay(proj.MuzzleFlashDuration, $"{proj.MuzzleFlashDuration:F3}s")));
            if (proj.ExplosionFormId.HasValue)
                projFields.Add(new ReportField("Explosion",
                    ReportValue.FormId(proj.ExplosionFormId.Value, resolver),
                    $"0x{proj.ExplosionFormId.Value:X8}"));
            if (proj.ActiveSoundLoopFormId.HasValue)
                projFields.Add(new ReportField("In-Flight Snd",
                    ReportValue.FormId(proj.ActiveSoundLoopFormId.Value,
                        resolver.FormatWithEditorId(proj.ActiveSoundLoopFormId.Value)),
                    $"0x{proj.ActiveSoundLoopFormId.Value:X8}"));
            if (proj.CountdownSoundFormId.HasValue)
                projFields.Add(new ReportField("Countdown Snd",
                    ReportValue.FormId(proj.CountdownSoundFormId.Value,
                        resolver.FormatWithEditorId(proj.CountdownSoundFormId.Value)),
                    $"0x{proj.CountdownSoundFormId.Value:X8}"));
            if (proj.DeactivateSoundFormId.HasValue)
                projFields.Add(new ReportField("Deactivate Snd",
                    ReportValue.FormId(proj.DeactivateSoundFormId.Value,
                        resolver.FormatWithEditorId(proj.DeactivateSoundFormId.Value)),
                    $"0x{proj.DeactivateSoundFormId.Value:X8}"));
            if (!string.IsNullOrEmpty(proj.ModelPath))
                projFields.Add(new ReportField("Proj. Model", ReportValue.String(proj.ModelPath)));
            sections.Add(new ReportSection("Projectile Physics", projFields));
        }

        // Sound Effects (conditional)
        AddSoundEffectsSection(sections, weapon, resolver);

        // Model (conditional)
        if (!string.IsNullOrEmpty(weapon.ModelPath))
        {
            sections.Add(new ReportSection("Model",
            [
                new ReportField("Path", ReportValue.String(weapon.ModelPath))
            ]));
        }

        return new RecordReport("Weapon", weapon.FormId, weapon.EditorId, weapon.FullName, sections);
    }

    private static void AddSoundEffectsSection(
        List<ReportSection> sections, WeaponRecord weapon, FormIdResolver resolver)
    {
        var soundFields = new List<ReportField>();
        AddSoundField(soundFields, "Fire (3D)", weapon.FireSound3DFormId, resolver);
        AddSoundField(soundFields, "Fire (Distant)", weapon.FireSoundDistFormId, resolver);
        AddSoundField(soundFields, "Fire (2D)", weapon.FireSound2DFormId, resolver);
        AddSoundField(soundFields, "Dry Fire", weapon.DryFireSoundFormId, resolver);
        AddSoundField(soundFields, "Idle", weapon.IdleSoundFormId, resolver);
        AddSoundField(soundFields, "Equip", weapon.EquipSoundFormId, resolver);
        AddSoundField(soundFields, "Unequip", weapon.UnequipSoundFormId, resolver);
        AddSoundField(soundFields, "Pickup", weapon.PickupSoundFormId, resolver);
        AddSoundField(soundFields, "Putdown", weapon.PutdownSoundFormId, resolver);
        if (soundFields.Count > 0)
            sections.Add(new ReportSection("Sound Effects", soundFields));
    }

    private static void AddSoundField(List<ReportField> fields, string label, uint? formId,
        FormIdResolver resolver)
    {
        if (!formId.HasValue) return;
        fields.Add(new ReportField(label,
            ReportValue.FormId(formId.Value, resolver),
            $"0x{formId.Value:X8}"));
    }

    internal static void AppendWeaponReportEntry(
        StringBuilder sb,
        WeaponRecord weapon,
        FormIdResolver resolver)
    {
        sb.AppendLine();
        var report = BuildWeaponReport(weapon, resolver);
        sb.Append(ReportTextFormatter.Format(report));
    }

    /// <summary>
    ///     Generate a structured, human-readable per-weapon report with aligned sections
    ///     and display names for all referenced records (ammo, projectile, sounds, criticals).
    /// </summary>
    public static string GenerateWeaponReport(
        List<WeaponRecord> weapons,
        FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        GeckReportHelpers.AppendHeader(sb, $"Weapon Report ({weapons.Count:N0} Weapons)");
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
            AppendWeaponReportEntry(sb, weapon, resolver);
        }

        return sb.ToString();
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
            identityFields.Add(new ReportField("Required Skill",
                ReportValue.Int(recipe.RequiredSkill, $"{recipe.RequiredSkill} (level {recipe.RequiredSkillLevel})")));
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
    internal static RecordReport BuildWeaponModReport(WeaponModRecord mod)
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
