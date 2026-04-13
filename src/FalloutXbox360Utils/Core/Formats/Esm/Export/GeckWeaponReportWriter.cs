using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Weapon-specific report building and text generation. Extracted from GeckItemDetailWriter.
/// </summary>
internal static class GeckWeaponReportWriter
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

        // Flags (only set ones)
        var flagFields = new List<ReportField>();
        if (weapon.IsAutomatic) flagFields.Add(new ReportField("Automatic", ReportValue.Bool(true)));
        if (weapon.HasScope) flagFields.Add(new ReportField("Has Scope", ReportValue.Bool(true)));
        if (weapon.CantDrop) flagFields.Add(new ReportField("Can't Drop", ReportValue.Bool(true)));
        if (weapon.HideBackpack) flagFields.Add(new ReportField("Hide Backpack", ReportValue.Bool(true)));
        if (weapon.IsEmbeddedWeapon) flagFields.Add(new ReportField("Embedded Weapon", ReportValue.Bool(true)));
        if (weapon.IsNonPlayable) flagFields.Add(new ReportField("Non-Playable", ReportValue.Bool(true)));
        if (weapon.IsPlayerOnly) flagFields.Add(new ReportField("Player Only", ReportValue.Bool(true)));
        if (weapon.NpcsUseAmmo) flagFields.Add(new ReportField("NPCs Use Ammo", ReportValue.Bool(true)));
        if (weapon.NoJamAfterReload) flagFields.Add(new ReportField("No Jam After Reload", ReportValue.Bool(true)));
        if (weapon.IsMinorCrime) flagFields.Add(new ReportField("Minor Crime", ReportValue.Bool(true)));
        if (weapon.IsRangeFixed) flagFields.Add(new ReportField("Range Fixed", ReportValue.Bool(true)));
        if (weapon.NotUsedInNormalCombat)
            flagFields.Add(new ReportField("Not Used in Normal Combat", ReportValue.Bool(true)));
        if (weapon.HasLongBursts) flagFields.Add(new ReportField("Long Bursts", ReportValue.Bool(true)));
        if (weapon.DontUseFirstPersonIsAnimations)
            flagFields.Add(new ReportField("No 1st-Person IS Anims", ReportValue.Bool(true)));
        if (weapon.DontUseThirdPersonIsAnimations)
            flagFields.Add(new ReportField("No 3rd-Person IS Anims", ReportValue.Bool(true)));
        if (flagFields.Count > 0)
        {
            sections.Add(new ReportSection("Flags", flagFields));
        }

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

        // VATS Attack (special VATS effect — Phase 4)
        AddVatsAttackSection(sections, weapon, resolver);

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

        // Phase 3 optional sections
        AddOverrideSection(sections, weapon);
        AddSemiAutoFireDelaySection(sections, weapon);
        AddAnimationOverridesSection(sections, weapon);
        AddMiscSection(sections, weapon);
        AddRumbleSection(sections, weapon);

        // Ammo & Projectile (conditional)
        AddAmmoProjectileSection(sections, weapon, resolver);

        // Projectile Physics (conditional)
        AddProjectilePhysicsSection(sections, weapon, resolver);

        // Sound Effects (conditional)
        AddSoundEffectsSection(sections, weapon, resolver);

        // Weapon Mods (conditional)
        AddWeaponModsSection(sections, weapon, resolver);

        // Art Assets (Phase 5)
        AddArtAssetsSection(sections, weapon);

        // Repair Item List (Phase 5)
        if (weapon.RepairItemListFormId is { } repairFormId && repairFormId != 0)
        {
            sections.Add(new ReportSection("Repair",
            [
                new ReportField("Repair List", ReportValue.FormId(repairFormId, resolver),
                    $"0x{repairFormId:X8}")
            ]));
        }

        // Modded Model Variants (Phase 6)
        AddModelVariantsSection(sections, weapon, resolver);

        return new RecordReport("Weapon", weapon.FormId, weapon.EditorId, weapon.FullName, sections);
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

    private static void AddVatsAttackSection(
        List<ReportSection> sections, WeaponRecord weapon, FormIdResolver resolver)
    {
        if (weapon.VatsAttack is not { } vats ||
            (vats.EffectFormId == 0 && Math.Abs(vats.ActionPointCost) <= 0f && Math.Abs(vats.DamageMultiplier) <= 0f
             && !vats.IsSilent && !vats.RequiresMod && vats.ExtraFlags == 0))
        {
            return;
        }

        var vatsFields = new List<ReportField>();
        if (vats.EffectFormId != 0)
            vatsFields.Add(new ReportField("Effect",
                ReportValue.FormId(vats.EffectFormId, resolver),
                $"0x{vats.EffectFormId:X8}"));
        if (Math.Abs(vats.ActionPointCost) > 0.001f)
            vatsFields.Add(new ReportField("AP Cost", ReportValue.Float(vats.ActionPointCost)));
        if (Math.Abs(vats.DamageMultiplier) > 0.001f)
            vatsFields.Add(new ReportField("Damage Mult", ReportValue.Float(vats.DamageMultiplier)));
        if (Math.Abs(vats.SkillRequired) > 0.001f)
            vatsFields.Add(new ReportField("Skill Required", ReportValue.Float(vats.SkillRequired)));
        if (vats.IsSilent) vatsFields.Add(new ReportField("Silent", ReportValue.Bool(true)));
        if (vats.RequiresMod) vatsFields.Add(new ReportField("Mod Required", ReportValue.Bool(true)));
        if (vats.ExtraFlags != 0)
            vatsFields.Add(new ReportField("Extra Flags",
                ReportValue.Int(vats.ExtraFlags, $"0x{vats.ExtraFlags:X2}")));
        sections.Add(new ReportSection("VATS Attack", vatsFields));
    }

    private static void AddOverrideSection(List<ReportSection> sections, WeaponRecord weapon)
    {
        var overrideFields = new List<ReportField>();
        if (Math.Abs(weapon.DamageToWeaponMult - 1.0f) > 0.001f)
            overrideFields.Add(new ReportField("Damage to Weapon Mult", ReportValue.Float(weapon.DamageToWeaponMult)));
        if (Math.Abs(weapon.KillImpulse) > 0.001f)
            overrideFields.Add(new ReportField("Kill Impulse", ReportValue.Float(weapon.KillImpulse)));
        if (Math.Abs(weapon.KillImpulseDistance) > 0.001f)
            overrideFields.Add(new ReportField("Impulse Distance", ReportValue.Float(weapon.KillImpulseDistance)));
        if (overrideFields.Count > 0)
            sections.Add(new ReportSection("Override", overrideFields));
    }

    private static void AddSemiAutoFireDelaySection(List<ReportSection> sections, WeaponRecord weapon)
    {
        if (Math.Abs(weapon.SemiAutoFireDelayMin) > 0.001f || Math.Abs(weapon.SemiAutoFireDelayMax) > 0.001f)
        {
            sections.Add(new ReportSection("Semi-Auto Fire Delay",
            [
                new ReportField("Min (sec)", ReportValue.Float(weapon.SemiAutoFireDelayMin)),
                new ReportField("Max (sec)", ReportValue.Float(weapon.SemiAutoFireDelayMax))
            ]));
        }
    }

    private static void AddAnimationOverridesSection(List<ReportSection> sections, WeaponRecord weapon)
    {
        var animFields = new List<ReportField>();
        if (Math.Abs(weapon.AnimShotsPerSecond) > 0.001f)
            animFields.Add(new ReportField("Anim Shots/Sec", ReportValue.Float(weapon.AnimShotsPerSecond)));
        if (Math.Abs(weapon.AnimReloadTime) > 0.001f)
            animFields.Add(new ReportField("Anim Reload Time", ReportValue.Float(weapon.AnimReloadTime)));
        if (Math.Abs(weapon.AnimJamTime) > 0.001f)
            animFields.Add(new ReportField("Anim Jam Time", ReportValue.Float(weapon.AnimJamTime)));
        if (weapon.PowerAttackOverrideAnim != 0)
            animFields.Add(new ReportField("Power Attack Anim", ReportValue.Int(weapon.PowerAttackOverrideAnim)));
        if (weapon.ModReloadClipAnimation != 0)
            animFields.Add(new ReportField("Mod Reload Anim", ReportValue.Int(weapon.ModReloadClipAnimation)));
        if (weapon.ModFireAnimation != 0)
            animFields.Add(new ReportField("Mod Fire Anim", ReportValue.Int(weapon.ModFireAnimation)));
        if (animFields.Count > 0)
            sections.Add(new ReportSection("Animation Overrides", animFields));
    }

    private static void AddMiscSection(List<ReportSection> sections, WeaponRecord weapon)
    {
        var miscFields = new List<ReportField>();
        if (weapon.Resistance != 0)
            miscFields.Add(new ReportField("Resistance", ReportValue.Int((int)weapon.Resistance)));
        if (Math.Abs(weapon.IronSightUseMult - 1.0f) > 0.001f)
            miscFields.Add(new ReportField("Sight Usage Mult", ReportValue.Float(weapon.IronSightUseMult)));
        if (Math.Abs(weapon.AmmoRegenRate) > 0.001f)
            miscFields.Add(new ReportField("Ammo Regen Rate", ReportValue.Float(weapon.AmmoRegenRate)));
        if (Math.Abs(weapon.CookTimer) > 0.001f)
            miscFields.Add(new ReportField("Cook Timer (sec)", ReportValue.Float(weapon.CookTimer)));
        if (miscFields.Count > 0)
            sections.Add(new ReportSection("Misc", miscFields));
    }

    private static void AddRumbleSection(List<ReportSection> sections, WeaponRecord weapon)
    {
        if (Math.Abs(weapon.RumbleLeftMotor) > 0.001f || Math.Abs(weapon.RumbleRightMotor) > 0.001f
                                                      || Math.Abs(weapon.RumbleDuration) > 0.001f)
        {
            sections.Add(new ReportSection("Rumble",
            [
                new ReportField("Left Motor", ReportValue.Float(weapon.RumbleLeftMotor)),
                new ReportField("Right Motor", ReportValue.Float(weapon.RumbleRightMotor)),
                new ReportField("Duration", ReportValue.Float(weapon.RumbleDuration)),
                new ReportField("Pattern", ReportValue.Int((int)weapon.RumblePattern)),
                new ReportField("Wavelength", ReportValue.Float(weapon.RumbleWavelength))
            ]));
        }
    }

    private static void AddAmmoProjectileSection(
        List<ReportSection> sections, WeaponRecord weapon, FormIdResolver resolver)
    {
        if (!weapon.AmmoFormId.HasValue && !weapon.ProjectileFormId.HasValue &&
            !weapon.ImpactDataSetFormId.HasValue)
        {
            return;
        }

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

    private static void AddProjectilePhysicsSection(
        List<ReportSection> sections, WeaponRecord weapon, FormIdResolver resolver)
    {
        if (weapon.ProjectileData == null)
        {
            return;
        }

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

    private static void AddSoundEffectsSection(
        List<ReportSection> sections, WeaponRecord weapon, FormIdResolver resolver)
    {
        var soundFields = new List<ReportField>();
        // Item handling
        AddSoundField(soundFields, "Pickup", weapon.PickupSoundFormId, resolver);
        AddSoundField(soundFields, "Putdown", weapon.PutdownSoundFormId, resolver);
        // Attack
        AddSoundField(soundFields, "Fire (3D)", weapon.FireSound3DFormId, resolver);
        AddSoundField(soundFields, "Fire (Distant)", weapon.FireSoundDistFormId, resolver);
        AddSoundField(soundFields, "Fire (2D)", weapon.FireSound2DFormId, resolver);
        AddSoundField(soundFields, "Attack Loop", weapon.AttackLoopSoundFormId, resolver);
        AddSoundField(soundFields, "Dry Fire", weapon.DryFireSoundFormId, resolver);
        AddSoundField(soundFields, "Melee Block", weapon.MeleeBlockSoundFormId, resolver);
        // Equipment
        AddSoundField(soundFields, "Idle", weapon.IdleSoundFormId, resolver);
        AddSoundField(soundFields, "Equip", weapon.EquipSoundFormId, resolver);
        AddSoundField(soundFields, "Unequip", weapon.UnequipSoundFormId, resolver);
        // Mod silenced variants
        AddSoundField(soundFields, "Mod Silenced (3D)", weapon.ModSilencedSound3DFormId, resolver);
        AddSoundField(soundFields, "Mod Silenced (Distant)", weapon.ModSilencedSoundDistFormId, resolver);
        AddSoundField(soundFields, "Mod Silenced (2D)", weapon.ModSilencedSound2DFormId, resolver);
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

    private static void AddWeaponModsSection(
        List<ReportSection> sections, WeaponRecord weapon, FormIdResolver resolver)
    {
        if (weapon.ModSlots.Count == 0)
        {
            return;
        }

        var modItems = weapon.ModSlots
            .OrderBy(s => s.SlotIndex)
            .Select(slot =>
            {
                var fields = new List<ReportField>
                {
                    new("Effect", ReportValue.String(slot.ActionName)),
                    new("Value", ReportValue.Float(slot.Value))
                };
                if (MathF.Abs(slot.ValueTwo) > 0.001f)
                    fields.Add(new ReportField("Value 2", ReportValue.Float(slot.ValueTwo)));
                if (slot.ModFormId.HasValue)
                    fields.Add(new ReportField("Mod",
                        ReportValue.FormId(slot.ModFormId.Value, resolver),
                        $"0x{slot.ModFormId.Value:X8}"));

                var modName = slot.ModFormId.HasValue
                    ? resolver.FormatFull(slot.ModFormId.Value)
                    : "(unassigned)";
                return (ReportValue)new ReportValue.CompositeVal(fields,
                    $"[Slot {slot.SlotIndex}] {slot.ActionName}: {slot.Value:G} — {modName}");
            })
            .ToList();

        sections.Add(new ReportSection($"Weapon Mods ({weapon.ModSlots.Count})",
        [
            new ReportField("Mods", ReportValue.List(modItems))
        ]));
    }

    private static void AddArtAssetsSection(List<ReportSection> sections, WeaponRecord weapon)
    {
        var artFields = new List<ReportField>();
        if (!string.IsNullOrEmpty(weapon.ModelPath))
            artFields.Add(new ReportField("Model", ReportValue.String(weapon.ModelPath)));
        if (!string.IsNullOrEmpty(weapon.ShellCasingModelPath))
            artFields.Add(new ReportField("Shell Casing Model", ReportValue.String(weapon.ShellCasingModelPath)));
        if (!string.IsNullOrEmpty(weapon.InventoryIconPath))
            artFields.Add(new ReportField("Inventory Icon", ReportValue.String(weapon.InventoryIconPath)));
        if (!string.IsNullOrEmpty(weapon.MessageIconPath))
            artFields.Add(new ReportField("Message Icon", ReportValue.String(weapon.MessageIconPath)));
        if (!string.IsNullOrEmpty(weapon.EmbeddedWeaponNode))
            artFields.Add(new ReportField("Embedded Weapon Node", ReportValue.String(weapon.EmbeddedWeaponNode)));
        if (artFields.Count > 0)
            sections.Add(new ReportSection("Art Assets", artFields));
    }

    private static void AddModelVariantsSection(
        List<ReportSection> sections, WeaponRecord weapon, FormIdResolver resolver)
    {
        if (weapon.ModelVariants.Count == 0)
        {
            return;
        }

        var variantItems = weapon.ModelVariants
            .Select(v =>
            {
                var fields = new List<ReportField>
                {
                    new("Combination", ReportValue.String(v.CombinationName))
                };
                if (!string.IsNullOrEmpty(v.ThirdPersonModelPath))
                    fields.Add(new ReportField("3rd Person Model", ReportValue.String(v.ThirdPersonModelPath)));
                if (v.FirstPersonObjectFormId is > 0)
                    fields.Add(new ReportField("1st Person Object",
                        ReportValue.FormId(v.FirstPersonObjectFormId.Value, resolver),
                        $"0x{v.FirstPersonObjectFormId.Value:X8}"));

                var summary = v.CombinationName;
                if (!string.IsNullOrEmpty(v.ThirdPersonModelPath))
                    summary += $": {v.ThirdPersonModelPath}";
                return (ReportValue)new ReportValue.CompositeVal(fields, summary);
            })
            .ToList();

        sections.Add(new ReportSection($"Modded Models ({weapon.ModelVariants.Count})",
        [
            new ReportField("Variants", ReportValue.List(variantItems))
        ]));
    }
}
