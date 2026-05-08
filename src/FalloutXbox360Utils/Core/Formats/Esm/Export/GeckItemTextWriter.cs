using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Text-format section writers for item reports (weapons, armor, ammo, consumables, misc, keys, containers).
///     Extracted from GeckItemWriter to keep the structured Build*Report methods separate from text formatting.
/// </summary>
internal static class GeckItemTextWriter
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
            sb.AppendLine($"  Fire Rate:      {weapon.ShotsPerSec:F2}/sec");
            sb.AppendLine($"  Clip Size:      {weapon.ClipSize}");
            sb.AppendLine($"  Range:          {weapon.MinRange:F0} - {weapon.MaxRange:F0}");
            sb.AppendLine($"  Speed:          {weapon.Speed:F2}");
            sb.AppendLine($"  Reach:          {weapon.Reach:F2}");
            sb.AppendLine($"  Ammo Per Shot:  {weapon.AmmoPerShot}");
            sb.AppendLine($"  Projectiles:    {weapon.NumProjectiles}");

            sb.AppendLine();
            sb.AppendLine("Accuracy:");
            sb.AppendLine($"  Spread:         {weapon.Spread:F2}");
            sb.AppendLine($"  Min Spread:     {weapon.MinSpread:F2}");
            sb.AppendLine($"  Drift:          {weapon.Drift:F2}");

            sb.AppendLine();
            sb.AppendLine("VATS:");
            sb.AppendLine($"  AP Cost:        {weapon.ActionPoints:F0}");
            sb.AppendLine($"  Hit Chance:     {weapon.VatsToHitChance}");

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

            if (weapon.CriticalDamage != 0 || Math.Abs(weapon.CriticalChance - 1.0f) > 0.01f ||
                weapon.CriticalEffectFormId.HasValue)
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

            // Projectile Physics
            AppendProjectilePhysics(sb, weapon, resolver);

            if (!string.IsNullOrEmpty(weapon.ModelPath))
            {
                sb.AppendLine($"  Model:          {weapon.ModelPath}");
            }

            // Sound effects
            AppendSoundEffects(sb, weapon, resolver);
        }
    }

    private static void AppendProjectilePhysics(StringBuilder sb, WeaponRecord weapon, FormIdResolver resolver)
    {
        if (weapon.ProjectileData == null)
        {
            return;
        }

        var proj = weapon.ProjectileData;
        sb.AppendLine();
        sb.AppendLine("Projectile Physics:");
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

    private static void AppendSoundEffects(StringBuilder sb, WeaponRecord weapon, FormIdResolver resolver)
    {
        var hasSounds = weapon.PickupSoundFormId.HasValue || weapon.PutdownSoundFormId.HasValue ||
                        weapon.FireSound3DFormId.HasValue || weapon.FireSoundDistFormId.HasValue ||
                        weapon.FireSound2DFormId.HasValue || weapon.DryFireSoundFormId.HasValue ||
                        weapon.IdleSoundFormId.HasValue || weapon.EquipSoundFormId.HasValue ||
                        weapon.UnequipSoundFormId.HasValue;
        if (!hasSounds)
        {
            return;
        }

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

    internal static void AppendArmorSection(StringBuilder sb, List<ArmorRecord> armor,
        FormIdResolver? resolver = null)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Armor ({armor.Count})");

        foreach (var item in armor.OrderBy(a => a.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "ARMO", item.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(item.FormId)}");
            sb.AppendLine($"Editor ID:      {item.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {item.FullName ?? "(none)"}");
            sb.AppendLine($"Equip:          {item.EquipmentTypeName}");
            sb.AppendLine($"Endianness:     {(item.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{item.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  DT:             {item.DamageThreshold:F1}");
            sb.AppendLine($"  DR:             {item.DamageResistance}");
            sb.AppendLine($"  Value:          {item.Value} caps");
            sb.AppendLine($"  Weight:         {item.Weight:F1}");
            sb.AppendLine($"  Health:         {item.Health}");

            if (item.BipedFlags != 0)
            {
                sb.AppendLine($"  Biped Slots:    0x{item.BipedFlags:X8}");
            }

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
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

            var projectileFormIds = item.ProjectileFormIds
                .Where(id => id != 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            if (projectileFormIds.Count > 1)
            {
                sb.AppendLine("  Projectiles:");
                foreach (var projectileFormId in projectileFormIds)
                {
                    sb.AppendLine($"    - {resolver.FormatFull(projectileFormId)}");
                }
            }

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
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

            if (item.Effects.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(
                    $"  {"Effect",-32} {"Magnitude",10} {"Area",6} {"Duration",10} {"Type",-8}");
                sb.AppendLine($"  {new string('\u2500', 70)}");
                foreach (var effect in item.Effects)
                {
                    var effectName = effect.EffectFormId != 0
                        ? resolver.FormatFull(effect.EffectFormId)
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

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
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
                sb.AppendLine($"  {"EditorID",-32} {"Name",-32} {"Qty",5}");
                sb.AppendLine($"  {new string('-', 32)} {new string('-', 32)} {new string('-', 5)}");
                foreach (var item in container.Contents.OrderBy(i => i.ItemFormId))
                {
                    var editorId = resolver.ResolveEditorId(item.ItemFormId);
                    var displayName = resolver.ResolveDisplayName(item.ItemFormId);
                    sb.AppendLine(
                        $"  {GeckReportHelpers.Truncate(editorId, 32),-32} {GeckReportHelpers.Truncate(displayName, 32),-32} {item.Count,5}");
                }
            }

            if (container.Script.HasValue)
            {
                sb.AppendLine($"  Script:         {resolver.FormatFull(container.Script.Value)}");
            }

            if (!string.IsNullOrEmpty(container.ModelPath))
            {
                sb.AppendLine($"  Model:          {container.ModelPath}");
            }
        }
    }
}
