using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports for Weapon, Armor, Ammo, Consumable, Misc Item, Key, and Container records.
///     Detailed weapon/container reports, recipes, and weapon mods are in GeckItemDetailWriter.
/// </summary>
internal static class GeckItemWriter
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

    internal static void AppendArmorSection(StringBuilder sb, List<ArmorRecord> armor)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Armor ({armor.Count})");

        foreach (var item in armor.OrderBy(a => a.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "ARMO", item.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(item.FormId)}");
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
    public static string GenerateArmorReport(List<ArmorRecord> armor, Dictionary<uint, string>? _lookup = null)
    {
        var sb = new StringBuilder();
        AppendArmorSection(sb, armor);
        return sb.ToString();
    }

    internal static void AppendAmmoSection(StringBuilder sb, List<AmmoRecord> ammo,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Ammunition ({ammo.Count})");

        foreach (var item in ammo.OrderBy(a => a.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "AMMO", item.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(item.FormId)}");
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
        GeckReportHelpers.AppendSectionHeader(sb, $"Consumables ({consumables.Count})");

        foreach (var item in consumables.OrderBy(c => c.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "ALCH", item.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(item.FormId)}");
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
        GeckReportHelpers.AppendSectionHeader(sb, $"Miscellaneous Items ({miscItems.Count})");

        foreach (var item in miscItems.OrderBy(m => m.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "MISC", item.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(item.FormId)}");
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
        Dictionary<uint, string>? _lookup = null)
    {
        var sb = new StringBuilder();
        AppendMiscItemsSection(sb, miscItems);
        return sb.ToString();
    }

    internal static void AppendKeysSection(StringBuilder sb, List<KeyRecord> keys)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Keys ({keys.Count})");

        foreach (var key in keys.OrderBy(k => k.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "KEYM", key.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(key.FormId)}");
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
    public static string GenerateKeysReport(List<KeyRecord> keys, Dictionary<uint, string>? _lookup = null)
    {
        var sb = new StringBuilder();
        AppendKeysSection(sb, keys);
        return sb.ToString();
    }

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
}
