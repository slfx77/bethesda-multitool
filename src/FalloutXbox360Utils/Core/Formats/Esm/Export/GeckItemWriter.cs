using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Generates GECK-style text reports for Weapon, Armor, Ammo, Consumable, Misc Item, Key, Container, Weapon Mod, and Leveled List records.</summary>
internal static class GeckItemWriter
{
    internal static void AppendWeaponsSection(StringBuilder sb, List<WeaponRecord> weapons,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Weapons ({weapons.Count})");

        foreach (var weapon in weapons.OrderBy(w => w.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "WEAP", weapon.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(weapon.FormId)}");
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
        sb.AppendLine(new string(GeckReportGenerator.SeparatorChar, GeckReportGenerator.SeparatorWidth));
        var padding = (GeckReportGenerator.SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(GeckReportGenerator.SeparatorChar, GeckReportGenerator.SeparatorWidth));

        // Identity
        sb.AppendLine($"  FormID:         {GeckReportGenerator.FormatFormId(weapon.FormId)}");
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

            GeckReportGenerator.AppendSoundLine(sb, "Fire (3D):", weapon.FireSound3DFormId, resolver);
            GeckReportGenerator.AppendSoundLine(sb, "Fire (Distant):", weapon.FireSoundDistFormId, resolver);
            GeckReportGenerator.AppendSoundLine(sb, "Fire (2D):", weapon.FireSound2DFormId, resolver);
            GeckReportGenerator.AppendSoundLine(sb, "Dry Fire:", weapon.DryFireSoundFormId, resolver);
            GeckReportGenerator.AppendSoundLine(sb, "Idle:", weapon.IdleSoundFormId, resolver);
            GeckReportGenerator.AppendSoundLine(sb, "Equip:", weapon.EquipSoundFormId, resolver);
            GeckReportGenerator.AppendSoundLine(sb, "Unequip:", weapon.UnequipSoundFormId, resolver);
            GeckReportGenerator.AppendSoundLine(sb, "Pickup:", weapon.PickupSoundFormId, resolver);
            GeckReportGenerator.AppendSoundLine(sb, "Putdown:", weapon.PutdownSoundFormId, resolver);
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
        GeckReportGenerator.AppendHeader(sb, $"Weapon Report ({weapons.Count:N0} Weapons)");
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

    internal static void AppendArmorSection(StringBuilder sb, List<ArmorRecord> armor)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Armor ({armor.Count})");

        foreach (var item in armor.OrderBy(a => a.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "ARMO", item.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(item.FormId)}");
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

    /// <summary>
    ///     Generate a report for Armor only.
    /// </summary>
    public static string GenerateArmorReport(List<ArmorRecord> armor, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendArmorSection(sb, armor);
        return sb.ToString();
    }

    internal static void AppendAmmoSection(StringBuilder sb, List<AmmoRecord> ammo,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Ammunition ({ammo.Count})");

        foreach (var item in ammo.OrderBy(a => a.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "AMMO", item.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(item.FormId)}");
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
                sb.AppendLine($"  Projectile:     {resolver.FormatFull(item.ProjectileFormId.Value)}");
            }

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
    }

    /// <summary>
    ///     Generate a report for Ammo only.
    /// </summary>
    public static string GenerateAmmoReport(List<AmmoRecord> ammo, FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendAmmoSection(sb, ammo, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendConsumablesSection(StringBuilder sb, List<ConsumableRecord> consumables,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Consumables ({consumables.Count})");

        foreach (var item in consumables.OrderBy(c => c.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "ALCH", item.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(item.FormId)}");
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
                sb.AppendLine($"  Addiction:      {resolver.FormatFull(item.AddictionFormId.Value)}");
                sb.AppendLine($"  Addict. Chance: {item.AddictionChance * 100:F0}%");
            }

            if (item.EffectFormIds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Effects:");
                foreach (var effect in item.EffectFormIds)
                {
                    sb.AppendLine($"  - {resolver.FormatFull(effect)}");
                }
            }

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
    }

    /// <summary>
    ///     Generate a report for Consumables only.
    /// </summary>
    public static string GenerateConsumablesReport(List<ConsumableRecord> consumables,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendConsumablesSection(sb, consumables, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendMiscItemsSection(StringBuilder sb, List<MiscItemRecord> miscItems)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Miscellaneous Items ({miscItems.Count})");

        foreach (var item in miscItems.OrderBy(m => m.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "MISC", item.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(item.FormId)}");
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

    /// <summary>
    ///     Generate a report for Misc Items only.
    /// </summary>
    public static string GenerateMiscItemsReport(List<MiscItemRecord> miscItems,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendMiscItemsSection(sb, miscItems);
        return sb.ToString();
    }

    internal static void AppendKeysSection(StringBuilder sb, List<KeyRecord> keys)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Keys ({keys.Count})");

        foreach (var key in keys.OrderBy(k => k.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "KEYM", key.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(key.FormId)}");
            sb.AppendLine($"Editor ID:      {key.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {key.FullName ?? "(none)"}");
            sb.AppendLine($"Value:          {key.Value} caps");
            sb.AppendLine($"Weight:         {key.Weight:F1}");
            sb.AppendLine($"Endianness:     {(key.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{key.Offset:X8}");
        }
    }

    /// <summary>
    ///     Generate a report for Keys only.
    /// </summary>
    public static string GenerateKeysReport(List<KeyRecord> keys, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendKeysSection(sb, keys);
        return sb.ToString();
    }

    internal static void AppendContainersSection(StringBuilder sb, List<ContainerRecord> containers,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Containers ({containers.Count})");

        foreach (var container in containers.OrderBy(c => c.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "CONT", container.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(container.FormId)}");
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
        sb.AppendLine(new string(GeckReportGenerator.SeparatorChar, GeckReportGenerator.SeparatorWidth));
        var padding = (GeckReportGenerator.SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(GeckReportGenerator.SeparatorChar, GeckReportGenerator.SeparatorWidth));

        // Basic info
        sb.AppendLine($"  FormID:         {GeckReportGenerator.FormatFormId(container.FormId)}");
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
                sb.AppendLine($"    {GeckReportGenerator.Truncate(editorId, 32),-32} {GeckReportGenerator.Truncate(displayName, 32),-32} {item.Count,5}");
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
        GeckReportGenerator.AppendHeader(sb, $"Container Report ({containers.Count:N0} Containers)");
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
        GeckReportGenerator.AppendSectionHeader(sb, $"Recipes ({recipes.Count})");
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
            sb.AppendLine($"  FormID:         {GeckReportGenerator.FormatFormId(recipe.FormId)}");
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
                    sb.AppendLine($"  {GeckReportGenerator.Truncate(itemName, 50),-50} {ing.Count,6}");
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
                    sb.AppendLine($"  {GeckReportGenerator.Truncate(itemName, 50),-50} {output.Count,6}");
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
        GeckReportGenerator.AppendSectionHeader(sb, $"Weapon Mods ({mods.Count})");
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
            sb.AppendLine($"  FormID:      {GeckReportGenerator.FormatFormId(mod.FormId)}");
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
