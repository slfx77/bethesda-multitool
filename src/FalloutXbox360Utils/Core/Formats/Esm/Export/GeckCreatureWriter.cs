using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Generates GECK-style text reports for Creature, Race, and Class records.</summary>
internal static class GeckCreatureWriter
{
    /// <summary>Build a structured creature report from a <see cref="CreatureRecord" />.</summary>
    internal static RecordReport BuildCreatureReport(CreatureRecord creature, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        sections.Add(new("Identity", [new("Type", ReportValue.String(creature.CreatureTypeName))]));

        // Stats (ACBS)
        if (creature.Stats != null)
        {
            sections.Add(new("Stats",
            [
                new("Level", ReportValue.Int(creature.Stats.Level)),
                new("Fatigue Base", ReportValue.Int(creature.Stats.FatigueBase)),
                new("Barter Gold", ReportValue.Int(creature.Stats.BarterGold)),
                new("Speed Mult", ReportValue.Int(creature.Stats.SpeedMultiplier)),
                new("Calc Range",
                    ReportValue.String($"{creature.Stats.CalcMin} - {creature.Stats.CalcMax}")),
                new("Flags", ReportValue.String($"0x{creature.Stats.Flags:X8}"))
            ]));
        }

        // Combat
        sections.Add(new("Combat",
        [
            new("Attack Damage", ReportValue.Int(creature.AttackDamage)),
            new("Combat Skill", ReportValue.Int(creature.CombatSkill)),
            new("Magic Skill", ReportValue.Int(creature.MagicSkill)),
            new("Stealth Skill", ReportValue.Int(creature.StealthSkill))
        ]));

        // AI Data
        if (creature.AiData != null)
        {
            var ai = creature.AiData;
            sections.Add(new("AI Data",
            [
                new("Aggression", ReportValue.String(ai.AggressionName)),
                new("Confidence", ReportValue.String(ai.ConfidenceName)),
                new("Mood", ReportValue.String(ai.MoodName)),
                new("Assistance", ReportValue.String(ai.AssistanceName))
            ]));
        }

        // Model
        if (!string.IsNullOrEmpty(creature.ModelPath))
            sections.Add(new("Model", [new("Path", ReportValue.String(creature.ModelPath))]));

        return new RecordReport("Creature", creature.FormId, creature.EditorId, creature.FullName,
            sections);
    }

    /// <summary>Build a structured race report from a <see cref="RaceRecord" />.</summary>
    internal static RecordReport BuildRaceReport(RaceRecord race, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Description
        if (!string.IsNullOrEmpty(race.Description))
            sections.Add(new("Description",
                [new("Description", ReportValue.String(race.Description))]));

        // Skill Boosts
        if (race.SkillBoosts.Count > 0)
        {
            var boostItems = race.SkillBoosts
                .Select(b =>
                {
                    var skillName = resolver.GetActorValueName(b.SkillIndex) ?? $"AV#{b.SkillIndex}";
                    return (ReportValue)new ReportValue.CompositeVal(
                    [
                        new("Skill", ReportValue.String(skillName)),
                        new("Boost", ReportValue.Int(b.Boost, GeckReportHelpers.FormatModifier(b.Boost)))
                    ], $"{skillName} {GeckReportHelpers.FormatModifier(b.Boost)}");
                })
                .ToList();
            sections.Add(new("Skill Boosts", [new("Boosts", ReportValue.List(boostItems))]));
        }

        // Height
        sections.Add(new("Height",
        [
            new("Male", ReportValue.Float(race.MaleHeight, "F2")),
            new("Female", ReportValue.Float(race.FemaleHeight, "F2"))
        ]));

        // Voice Types
        if (race.MaleVoiceFormId.HasValue || race.FemaleVoiceFormId.HasValue)
        {
            var voiceFields = new List<ReportField>();
            if (race.MaleVoiceFormId.HasValue)
                voiceFields.Add(new("Male", ReportValue.FormId(race.MaleVoiceFormId.Value, resolver),
                    $"0x{race.MaleVoiceFormId.Value:X8}"));
            if (race.FemaleVoiceFormId.HasValue)
                voiceFields.Add(new("Female", ReportValue.FormId(race.FemaleVoiceFormId.Value, resolver),
                    $"0x{race.FemaleVoiceFormId.Value:X8}"));
            sections.Add(new("Voice Types", voiceFields));
        }

        // Related Races
        if (race.OlderRaceFormId.HasValue || race.YoungerRaceFormId.HasValue)
        {
            var relFields = new List<ReportField>();
            if (race.OlderRaceFormId.HasValue)
                relFields.Add(new("Older", ReportValue.FormId(race.OlderRaceFormId.Value, resolver),
                    $"0x{race.OlderRaceFormId.Value:X8}"));
            if (race.YoungerRaceFormId.HasValue)
                relFields.Add(new("Younger", ReportValue.FormId(race.YoungerRaceFormId.Value, resolver),
                    $"0x{race.YoungerRaceFormId.Value:X8}"));
            sections.Add(new("Related Races", relFields));
        }

        // Racial Abilities
        if (race.AbilityFormIds.Count > 0)
        {
            var abilityItems = race.AbilityFormIds
                .Select(a => (ReportValue)ReportValue.FormId(a, resolver))
                .ToList();
            sections.Add(new("Racial Abilities",
                [new("Abilities", ReportValue.List(abilityItems))]));
        }

        return new RecordReport("Race", race.FormId, race.EditorId, race.FullName, sections);
    }

    internal static void AppendCreaturesSection(StringBuilder sb, List<CreatureRecord> creatures)
        => GeckActorWriter.AppendCreaturesSection(sb, creatures, FormIdResolver.Empty);

    /// <summary>
    ///     Generate a report for Creatures only.
    /// </summary>
    public static string GenerateCreaturesReport(List<CreatureRecord> creatures,
        FormIdResolver? resolver = null)
        => GeckActorWriter.GenerateCreaturesReport(creatures, resolver);

    internal static void AppendRacesSection(StringBuilder sb, List<RaceRecord> races,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Races ({races.Count})");

        foreach (var race in races.OrderBy(r => r.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "RACE", race.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(race.FormId)}");
            sb.AppendLine($"Editor ID:      {race.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {race.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(race.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{race.Offset:X8}");

            if (!string.IsNullOrEmpty(race.Description))
            {
                sb.AppendLine();
                sb.AppendLine("Description:");
                foreach (var line in race.Description.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }

            if (race.SkillBoosts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Skill Boosts:");
                foreach (var (skillIndex, boost) in race.SkillBoosts)
                {
                    var skillName = resolver.GetActorValueName(skillIndex) ?? $"AV#{skillIndex}";
                    sb.AppendLine($"  {skillName,-15} {GeckReportHelpers.FormatModifier(boost)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Height:");
            sb.AppendLine($"  Male:         {race.MaleHeight:F2}");
            sb.AppendLine($"  Female:       {race.FemaleHeight:F2}");

            if (race.MaleVoiceFormId.HasValue || race.FemaleVoiceFormId.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine("Voice Types:");
                if (race.MaleVoiceFormId.HasValue)
                {
                    sb.AppendLine($"  Male:         {resolver.FormatFull(race.MaleVoiceFormId.Value)}");
                }

                if (race.FemaleVoiceFormId.HasValue)
                {
                    sb.AppendLine($"  Female:       {resolver.FormatFull(race.FemaleVoiceFormId.Value)}");
                }
            }

            if (race.OlderRaceFormId.HasValue || race.YoungerRaceFormId.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine("Related Races:");
                if (race.OlderRaceFormId.HasValue)
                {
                    sb.AppendLine($"  Older:        {resolver.FormatFull(race.OlderRaceFormId.Value)}");
                }

                if (race.YoungerRaceFormId.HasValue)
                {
                    sb.AppendLine($"  Younger:      {resolver.FormatFull(race.YoungerRaceFormId.Value)}");
                }
            }

            if (race.AbilityFormIds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Racial Abilities:");
                foreach (var ability in race.AbilityFormIds)
                {
                    sb.AppendLine($"  - {resolver.FormatFull(ability)}");
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Races only.
    /// </summary>
    public static string GenerateRacesReport(List<RaceRecord> races, FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendRacesSection(sb, races, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendClassesSection(StringBuilder sb, List<ClassRecord> classes)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Classes ({classes.Count})");
        sb.AppendLine();

        var playable = classes.Count(c => c.IsPlayable);
        var guards = classes.Count(c => c.IsGuard);
        sb.AppendLine($"Total Classes: {classes.Count:N0}");
        sb.AppendLine($"  Playable: {playable:N0}");
        sb.AppendLine($"  Guard:    {guards:N0}");
        sb.AppendLine();

        string[] specialNames = ["Strength", "Perception", "Endurance", "Charisma", "Intelligence", "Agility", "Luck"];
        string[] skillNames =
        [
            "Barter", "Big Guns", "Energy Weapons", "Explosives", "Lockpick", "Medicine", "Melee Weapons",
            "Repair", "Science", "Guns", "Sneak", "Speech", "Survival", "Unarmed"
        ];

        foreach (var cls in classes.OrderBy(c => c.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  CLASS: {cls.EditorId ?? "(none)"} \u2014 {cls.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(cls.FormId)}");
            var flags = new List<string>();
            if (cls.IsPlayable)
            {
                flags.Add("Playable");
            }

            if (cls.IsGuard)
            {
                flags.Add("Guard");
            }

            if (flags.Count > 0)
            {
                sb.AppendLine($"  Flags:       {string.Join(", ", flags)}");
            }

            if (!string.IsNullOrEmpty(cls.Description))
            {
                sb.AppendLine($"  Description: {cls.Description}");
            }

            if (cls.TagSkills.Length > 0)
            {
                sb.AppendLine($"  \u2500\u2500 Tag Skills {new string('\u2500', 65)}");
                foreach (var skillIdx in cls.TagSkills)
                {
                    var skillName = skillIdx >= 32 && skillIdx <= 45
                        ? skillNames[skillIdx - 32]
                        : $"AV#{skillIdx}";
                    sb.AppendLine($"    {skillName}");
                }
            }

            if (cls.AttributeWeights.Length > 0)
            {
                sb.AppendLine($"  \u2500\u2500 Attribute Weights {new string('\u2500', 58)}");
                for (var i = 0; i < cls.AttributeWeights.Length && i < specialNames.Length; i++)
                {
                    sb.AppendLine($"    {specialNames[i],-15} {cls.AttributeWeights[i],3}");
                }
            }

            if (cls.TrainingSkill != 0 || cls.TrainingLevel != 0)
            {
                var trainingName = cls.TrainingSkill >= 32 && cls.TrainingSkill <= 45
                    ? skillNames[cls.TrainingSkill - 32]
                    : $"AV#{cls.TrainingSkill}";
                sb.AppendLine($"  Training:    {trainingName}, Level {cls.TrainingLevel}");
            }

            if (cls.BarterFlags != 0)
            {
                sb.AppendLine($"  Barter Flags: 0x{cls.BarterFlags:X8}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateClassesReport(List<ClassRecord> classes)
    {
        var sb = new StringBuilder();
        AppendClassesSection(sb, classes);
        return sb.ToString();
    }

    // ── Appearance Sections ──────────────────────────────────────────────

    internal static void AppendHairSection(StringBuilder sb, List<HairRecord> hair)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Hair Styles ({hair.Count})");
        sb.AppendLine();

        var playable = hair.Count(h => h.IsPlayable);
        sb.AppendLine($"Total Hair Styles: {hair.Count:N0}");
        sb.AppendLine($"  Playable: {playable:N0}");
        sb.AppendLine();

        foreach (var h in hair.OrderBy(x => x.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  HAIR: {h.EditorId ?? "(none)"} \u2014 {h.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(h.FormId)}");
            if (h.IsPlayable)
            {
                sb.AppendLine("  Playable:    Yes");
            }

            if (!string.IsNullOrEmpty(h.ModelPath))
            {
                sb.AppendLine($"  Model:       {h.ModelPath}");
            }

            if (!string.IsNullOrEmpty(h.TexturePath))
            {
                sb.AppendLine($"  Texture:     {h.TexturePath}");
            }

            if (h.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{h.Flags:X2}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateHairReport(List<HairRecord> hair)
    {
        var sb = new StringBuilder();
        AppendHairSection(sb, hair);
        return sb.ToString();
    }

    internal static void AppendEyesSection(StringBuilder sb, List<EyesRecord> eyes)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Eye Types ({eyes.Count})");
        sb.AppendLine();

        var playable = eyes.Count(e => e.IsPlayable);
        sb.AppendLine($"Total Eye Types: {eyes.Count:N0}");
        sb.AppendLine($"  Playable: {playable:N0}");
        sb.AppendLine();

        foreach (var eye in eyes.OrderBy(e => e.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  EYES: {eye.EditorId ?? "(none)"} \u2014 {eye.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(eye.FormId)}");
            if (eye.IsPlayable)
            {
                sb.AppendLine("  Playable:    Yes");
            }

            if (!string.IsNullOrEmpty(eye.TexturePath))
            {
                sb.AppendLine($"  Texture:     {eye.TexturePath}");
            }

            if (eye.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{eye.Flags:X2}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateEyesReport(List<EyesRecord> eyes)
    {
        var sb = new StringBuilder();
        AppendEyesSection(sb, eyes);
        return sb.ToString();
    }
}
