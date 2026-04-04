using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Generates GECK-style text reports for Perk, Spell, Enchantment, and Base Effect records.</summary>
internal static class GeckEffectsWriter
{
    internal static RecordReport BuildPerkReport(PerkRecord perk, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Description
        if (!string.IsNullOrEmpty(perk.Description))
        {
            sections.Add(new("Description",
            [
                new("Text", ReportValue.String(perk.Description))
            ]));
        }

        // Requirements
        var reqFields = new List<ReportField>
        {
            new("Ranks", ReportValue.Int(perk.Ranks)),
            new("Min Level", ReportValue.Int(perk.MinLevel)),
            new("Playable", ReportValue.Bool(perk.IsPlayable)),
            new("Is Trait", ReportValue.Bool(perk.IsTrait))
        };
        if (!string.IsNullOrEmpty(perk.IconPath))
        {
            reqFields.Add(new("Icon", ReportValue.String(perk.IconPath)));
        }

        sections.Add(new("Requirements", reqFields));

        // Entries
        if (perk.Entries.Count > 0)
        {
            var items = perk.Entries
                .OrderBy(e => e.Rank).ThenBy(e => e.TypeName)
                .Select(entry =>
                {
                    var fields = new List<ReportField>
                    {
                        new("Rank", ReportValue.Int(entry.Rank)),
                        new("Type", ReportValue.String(entry.TypeName))
                    };
                    if (entry.AbilityFormId.HasValue)
                    {
                        fields.Add(new("Ability", ReportValue.FormId(entry.AbilityFormId.Value, resolver),
                            $"0x{entry.AbilityFormId.Value:X8}"));
                    }

                    var abilityStr = entry.AbilityFormId.HasValue
                        ? $" Ability: {resolver.FormatFull(entry.AbilityFormId.Value)}"
                        : "";
                    return (ReportValue)new ReportValue.CompositeVal(fields,
                        $"[Rank {entry.Rank}] {entry.TypeName}{abilityStr}");
                })
                .ToList();

            sections.Add(new($"Entries ({perk.Entries.Count})",
            [
                new("Entries", ReportValue.List(items))
            ]));
        }

        return new RecordReport("Perk", perk.FormId, perk.EditorId, perk.FullName, sections);
    }

    internal static RecordReport BuildSpellReport(SpellRecord spell, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        sections.Add(new("Identity",
        [
            new("Type", ReportValue.String(spell.TypeName))
        ]));

        // Stats
        sections.Add(new("Stats",
        [
            new("Cost", ReportValue.Int((int)spell.Cost)),
            new("Level", ReportValue.Int((int)spell.Level)),
            new("Flags", ReportValue.Int(spell.Flags, $"0x{spell.Flags:X2}"))
        ]));

        // Effects
        if (spell.Effects.Count > 0)
        {
            var items = spell.Effects
                .OrderBy(e => e.EffectFormId)
                .Select(effect =>
                {
                    var typeName = effect.Type switch
                    {
                        0 => "Self",
                        1 => "Touch",
                        2 => "Target",
                        _ => $"#{effect.Type}"
                    };
                    var effectName = effect.EffectFormId != 0
                        ? resolver.FormatFull(effect.EffectFormId)
                        : "(none)";

                    var fields = new List<ReportField>
                    {
                        new("Effect", ReportValue.FormId(effect.EffectFormId, resolver),
                            effect.EffectFormId != 0 ? $"0x{effect.EffectFormId:X8}" : null),
                        new("Magnitude", ReportValue.Float(effect.Magnitude)),
                        new("Area", ReportValue.Int((int)effect.Area)),
                        new("Duration", ReportValue.Int((int)effect.Duration)),
                        new("Type", ReportValue.String(typeName))
                    };

                    return (ReportValue)new ReportValue.CompositeVal(fields,
                        $"{effectName} Mag:{effect.Magnitude:F1} Area:{effect.Area} Dur:{effect.Duration} {typeName}");
                })
                .ToList();

            sections.Add(new($"Effects ({spell.Effects.Count})",
            [
                new("Effects", ReportValue.List(items))
            ]));
        }

        return new RecordReport("Spell", spell.FormId, spell.EditorId, spell.FullName, sections);
    }

    internal static void AppendPerksSection(StringBuilder sb, List<PerkRecord> perks,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Perks ({perks.Count})");

        foreach (var perk in perks.OrderBy(p => p.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "PERK", perk.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(perk.FormId)}");
            sb.AppendLine($"Editor ID:      {perk.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {perk.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(perk.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{perk.Offset:X8}");

            if (!string.IsNullOrEmpty(perk.Description))
            {
                sb.AppendLine();
                sb.AppendLine("Description:");
                foreach (var line in perk.Description.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Requirements:");
            sb.AppendLine($"  Ranks:          {perk.Ranks}");
            sb.AppendLine($"  Min Level:      {perk.MinLevel}");
            sb.AppendLine($"  Playable:       {(perk.IsPlayable ? "Yes" : "No")}");
            sb.AppendLine($"  Is Trait:       {(perk.IsTrait ? "Yes" : "No")}");

            if (!string.IsNullOrEmpty(perk.IconPath))
            {
                sb.AppendLine($"  Icon:           {perk.IconPath}");
            }

            if (perk.Entries.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Entries:");
                foreach (var entry in perk.Entries.OrderBy(e => e.Rank).ThenBy(e => e.TypeName))
                {
                    var abilityStr = entry.AbilityFormId.HasValue
                        ? $" Ability: {resolver.FormatFull(entry.AbilityFormId.Value)}"
                        : "";
                    sb.AppendLine($"  [Rank {entry.Rank}] {entry.TypeName}{abilityStr}");
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Perks only.
    /// </summary>
    public static string GeneratePerksReport(List<PerkRecord> perks, FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendPerksSection(sb, perks, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendSpellsSection(StringBuilder sb, List<SpellRecord> spells,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Spells/Abilities ({spells.Count})");

        foreach (var spell in spells.OrderBy(s => s.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "SPEL", spell.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(spell.FormId)}");
            sb.AppendLine($"Editor ID:      {spell.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {spell.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {spell.TypeName}");
            sb.AppendLine($"Endianness:     {(spell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{spell.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  Cost:           {spell.Cost}");
            sb.AppendLine($"  Level:          {spell.Level}");
            sb.AppendLine($"  Flags:          0x{spell.Flags:X2}");

            if (spell.Effects.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(
                    $"  {"Effect",-32} {"Magnitude",10} {"Area",6} {"Duration",10} {"Type",-8}");
                sb.AppendLine($"  {new string('\u2500', 70)}");
                foreach (var effect in spell.Effects.OrderBy(e => e.EffectFormId))
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
        }
    }

    /// <summary>
    ///     Generate a report for Spells only.
    /// </summary>
    public static string GenerateSpellsReport(List<SpellRecord> spells, FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendSpellsSection(sb, spells, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendEnchantmentsSection(StringBuilder sb, List<EnchantmentRecord> enchantments,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Enchantments ({enchantments.Count})");
        sb.AppendLine();

        var byType = enchantments.GroupBy(e => e.TypeName).OrderBy(g => g.Key).ToList();
        sb.AppendLine($"Total Enchantments: {enchantments.Count:N0}");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key}: {group.Count():N0}");
        }

        var withEffects = enchantments.Count(e => e.Effects.Count > 0);
        sb.AppendLine($"  With Effects: {withEffects:N0} ({enchantments.Sum(e => e.Effects.Count):N0} total effects)");
        sb.AppendLine();

        foreach (var ench in enchantments.OrderBy(e => e.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  ENCHANTMENT: {ench.EditorId ?? "(none)"} \u2014 {ench.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(ench.FormId)}");
            sb.AppendLine($"  Type:        {ench.TypeName}");
            sb.AppendLine($"  Charge:      {ench.ChargeAmount}");
            sb.AppendLine($"  Cost:        {ench.EnchantCost}");
            if (ench.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{ench.Flags:X2}");
            }

            if (ench.Effects.Count > 0)
            {
                sb.AppendLine(
                    $"  \u2500\u2500 Effects ({ench.Effects.Count}) {new string('\u2500', 80 - 18 - ench.Effects.Count.ToString().Length)}");
                sb.AppendLine($"  {"Effect",-32} {"Magnitude",10} {"Area",6} {"Duration",10} {"Type",-8}");
                sb.AppendLine($"  {new string('\u2500', 70)}");
                foreach (var effect in ench.Effects.OrderBy(e => e.EffectFormId))
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

            sb.AppendLine();
        }
    }

    public static string GenerateEnchantmentsReport(List<EnchantmentRecord> enchantments,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendEnchantmentsSection(sb, enchantments, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendBaseEffectsSection(StringBuilder sb, List<BaseEffectRecord> effects,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Base Effects ({effects.Count})");
        sb.AppendLine();

        var byArchetype = effects.GroupBy(e => e.ArchetypeName).OrderByDescending(g => g.Count()).ToList();
        sb.AppendLine($"Total Base Effects: {effects.Count:N0}");
        sb.AppendLine("By Archetype:");
        foreach (var group in byArchetype)
        {
            sb.AppendLine($"  {group.Key,-30} {group.Count(),5:N0}");
        }

        sb.AppendLine();

        foreach (var effect in effects.OrderBy(e => e.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  EFFECT: {effect.EditorId ?? "(none)"} \u2014 {effect.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(effect.FormId)}");
            sb.AppendLine($"  Archetype:   {effect.ArchetypeName}");
            sb.AppendLine($"  Base Cost:   {effect.BaseCost:F2}");
            if (!string.IsNullOrEmpty(effect.EffectCode))
            {
                sb.AppendLine($"  Effect Code: {effect.EffectCode}");
            }

            if (effect.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{effect.Flags:X8}");
            }

            if (effect.ActorValue >= 0)
            {
                sb.AppendLine($"  Actor Value: {effect.ActorValue}");
            }

            if (effect.ResistValue >= 0)
            {
                sb.AppendLine($"  Resist Value: {effect.ResistValue}");
            }

            if (effect.MagicSchool >= 0)
            {
                sb.AppendLine($"  Magic School: {effect.MagicSchool}");
            }

            if (!string.IsNullOrEmpty(effect.Description))
            {
                sb.AppendLine($"  Description: {effect.Description}");
            }

            if (effect.Projectile != 0)
            {
                sb.AppendLine($"  Projectile:  {resolver.FormatFull(effect.Projectile)}");
            }

            if (effect.Explosion != 0)
            {
                sb.AppendLine($"  Explosion:   {resolver.FormatFull(effect.Explosion)}");
            }

            if (effect.AssociatedItem != 0)
            {
                sb.AppendLine($"  Assoc. Item: {resolver.FormatFull(effect.AssociatedItem)}");
            }

            if (!string.IsNullOrEmpty(effect.ModelPath))
            {
                sb.AppendLine($"  Model:       {effect.ModelPath}");
            }

            if (!string.IsNullOrEmpty(effect.Icon))
            {
                sb.AppendLine($"  Icon:        {effect.Icon}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateBaseEffectsReport(List<BaseEffectRecord> effects,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendBaseEffectsSection(sb, effects, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }
}
