using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports for Weapon and Weapon Mod records.
/// </summary>
internal static class GeckWeaponWriter
{
    internal static void AppendWeaponsSection(StringBuilder sb, List<WeaponRecord> weapons,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Weapons ({weapons.Count})");

        foreach (var weapon in weapons.OrderBy(w => w.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "WEAP", weapon.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(weapon.FormId)}");
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
                        $"  Effect:         {resolver.FormatFull(weapon.CriticalEffectFormId.Value)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Value/Weight:");
            sb.AppendLine($"  Value:          {weapon.Value} caps");
            sb.AppendLine($"  Weight:         {weapon.Weight:F1}");
            sb.AppendLine($"  Health:         {weapon.Health}");

            if (weapon.AmmoFormId.HasValue)
            {
                sb.AppendLine($"  Ammo:           {resolver.FormatFull(weapon.AmmoFormId.Value)}");
            }

            if (weapon.ProjectileFormId.HasValue)
            {
                sb.AppendLine($"  Projectile:     {resolver.FormatFull(weapon.ProjectileFormId.Value)}");
            }

            if (weapon.ImpactDataSetFormId.HasValue)
            {
                sb.AppendLine($"  Impact Data:    {resolver.FormatFull(weapon.ImpactDataSetFormId.Value)}");
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
                        $"  Fire (3D):      {resolver.FormatFull(weapon.FireSound3DFormId.Value)}");
                }

                if (weapon.FireSoundDistFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Fire (Distant): {resolver.FormatFull(weapon.FireSoundDistFormId.Value)}");
                }

                if (weapon.FireSound2DFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Fire (2D):      {resolver.FormatFull(weapon.FireSound2DFormId.Value)}");
                }

                if (weapon.DryFireSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Dry Fire:       {resolver.FormatFull(weapon.DryFireSoundFormId.Value)}");
                }

                if (weapon.IdleSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Idle:           {resolver.FormatFull(weapon.IdleSoundFormId.Value)}");
                }

                if (weapon.EquipSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Equip:          {resolver.FormatFull(weapon.EquipSoundFormId.Value)}");
                }

                if (weapon.UnequipSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Unequip:        {resolver.FormatFull(weapon.UnequipSoundFormId.Value)}");
                }

                if (weapon.PickupSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Pickup:         {resolver.FormatFull(weapon.PickupSoundFormId.Value)}");
                }

                if (weapon.PutdownSoundFormId.HasValue)
                {
                    sb.AppendLine(
                        $"  Putdown:        {resolver.FormatFull(weapon.PutdownSoundFormId.Value)}");
                }
            }
        }
    }

    internal static void AppendWeaponReportEntry(
        StringBuilder sb,
        WeaponRecord weapon,
        FormIdResolver resolver)
    {
        sb.AppendLine();

        // Header
        var title = !string.IsNullOrEmpty(weapon.FullName)
            ? $"WEAPON: {weapon.EditorId ?? "(unknown)"} \u2014 {weapon.FullName}"
            : $"WEAPON: {weapon.EditorId ?? "(unknown)"}";
        sb.AppendLine(new string(GeckReportHelpers.SeparatorChar, GeckReportHelpers.SeparatorWidth));
        var padding = (GeckReportHelpers.SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(GeckReportHelpers.SeparatorChar, GeckReportHelpers.SeparatorWidth));

        // Identity
        sb.AppendLine($"  FormID:         {GeckReportHelpers.FormatFormId(weapon.FormId)}");
        sb.AppendLine($"  Editor ID:      {weapon.EditorId ?? "(none)"}");
        sb.AppendLine($"  Display Name:   {weapon.FullName ?? "(none)"}");
        sb.AppendLine($"  Type:           {weapon.WeaponTypeName}");

        // Combat Stats
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

        // Accuracy
        sb.AppendLine();
        sb.AppendLine($"  \u2500\u2500 Accuracy {new string('\u2500', 70)}");
        sb.AppendLine($"  Spread:         {weapon.Spread:F2}");
        sb.AppendLine($"  Min Spread:     {weapon.MinSpread:F2}");
        sb.AppendLine($"  Drift:          {weapon.Drift:F2}");

        // VATS
        sb.AppendLine();
        sb.AppendLine($"  \u2500\u2500 VATS {new string('\u2500', 74)}");
        sb.AppendLine($"  AP Cost:        {weapon.ActionPoints:F0}");
        sb.AppendLine($"  Hit Chance:     {weapon.VatsToHitChance}");

        // Requirements
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

        // Critical
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
                    $"  Effect:         {resolver.FormatFull(weapon.CriticalEffectFormId.Value)}");
            }
        }

        // Value / Weight
        sb.AppendLine();
        sb.AppendLine($"  \u2500\u2500 Value / Weight {new string('\u2500', 64)}");
        sb.AppendLine($"  Value:          {weapon.Value} caps");
        sb.AppendLine($"  Weight:         {weapon.Weight:F1}");
        sb.AppendLine($"  Health:         {weapon.Health}");

        // Ammo & Projectile
        if (weapon.AmmoFormId.HasValue || weapon.ProjectileFormId.HasValue ||
            weapon.ImpactDataSetFormId.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Ammo & Projectile {new string('\u2500', 61)}");
            if (weapon.AmmoFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Ammo:           {resolver.FormatFull(weapon.AmmoFormId.Value)}");
            }

            if (weapon.ProjectileFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Projectile:     {resolver.FormatFull(weapon.ProjectileFormId.Value)}");
            }

            if (weapon.ImpactDataSetFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Impact Data:    {resolver.FormatFull(weapon.ImpactDataSetFormId.Value)}");
            }
        }

        // Projectile Physics
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
                    $"  Explosion:      {resolver.FormatFull(proj.ExplosionFormId.Value)}");
            }

            if (proj.ActiveSoundLoopFormId.HasValue)
            {
                sb.AppendLine(
                    $"  In-Flight Snd:  {resolver.FormatWithEditorId(proj.ActiveSoundLoopFormId.Value)}");
            }

            if (proj.CountdownSoundFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Countdown Snd:  {resolver.FormatWithEditorId(proj.CountdownSoundFormId.Value)}");
            }

            if (proj.DeactivateSoundFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Deactivate Snd: {resolver.FormatWithEditorId(proj.DeactivateSoundFormId.Value)}");
            }

            if (!string.IsNullOrEmpty(proj.ModelPath))
            {
                sb.AppendLine($"  Proj. Model:    {proj.ModelPath}");
            }
        }

        // Sound Effects
        var hasSounds = weapon.FireSound3DFormId.HasValue || weapon.FireSoundDistFormId.HasValue ||
                        weapon.FireSound2DFormId.HasValue || weapon.DryFireSoundFormId.HasValue ||
                        weapon.IdleSoundFormId.HasValue || weapon.EquipSoundFormId.HasValue ||
                        weapon.UnequipSoundFormId.HasValue || weapon.PickupSoundFormId.HasValue ||
                        weapon.PutdownSoundFormId.HasValue;
        if (hasSounds)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Sound Effects {new string('\u2500', 65)}");

            GeckReportHelpers.AppendSoundLine(sb, "Fire (3D):", weapon.FireSound3DFormId, resolver);
            GeckReportHelpers.AppendSoundLine(sb, "Fire (Distant):", weapon.FireSoundDistFormId, resolver);
            GeckReportHelpers.AppendSoundLine(sb, "Fire (2D):", weapon.FireSound2DFormId, resolver);
            GeckReportHelpers.AppendSoundLine(sb, "Dry Fire:", weapon.DryFireSoundFormId, resolver);
            GeckReportHelpers.AppendSoundLine(sb, "Idle:", weapon.IdleSoundFormId, resolver);
            GeckReportHelpers.AppendSoundLine(sb, "Equip:", weapon.EquipSoundFormId, resolver);
            GeckReportHelpers.AppendSoundLine(sb, "Unequip:", weapon.UnequipSoundFormId, resolver);
            GeckReportHelpers.AppendSoundLine(sb, "Pickup:", weapon.PickupSoundFormId, resolver);
            GeckReportHelpers.AppendSoundLine(sb, "Putdown:", weapon.PutdownSoundFormId, resolver);
        }

        // Model
        if (!string.IsNullOrEmpty(weapon.ModelPath))
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Model {new string('\u2500', 73)}");
            sb.AppendLine($"  Path:           {weapon.ModelPath}");
        }
    }

    /// <summary>
    ///     Generate a report for Weapons only.
    /// </summary>
    public static string GenerateWeaponsReport(List<WeaponRecord> weapons,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendWeaponsSection(sb, weapons, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
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
