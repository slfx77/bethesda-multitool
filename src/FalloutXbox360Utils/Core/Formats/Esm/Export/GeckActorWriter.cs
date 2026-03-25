using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports for NPC, Creature, Race, and Class records.
///     Detailed per-NPC reports and FaceGen formatting are in GeckActorDetailWriter.
/// </summary>
internal static class GeckActorWriter
{
    internal static void AppendNpcsSection(StringBuilder sb, List<NpcRecord> npcs,
        FormIdResolver resolver, IReadOnlyList<RaceRecord>? races = null)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"NPCs ({npcs.Count})");

        var raceLookup = races?.ToDictionary(r => r.FormId);

        foreach (var npc in npcs.OrderBy(n => n.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "NPC", npc.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(npc.FormId)}");
            sb.AppendLine($"Editor ID:      {npc.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {npc.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(npc.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{npc.Offset:X8}");

            if (npc.Stats != null)
            {
                sb.AppendLine();
                sb.AppendLine("Stats (ACBS):");
                sb.AppendLine($"  Level:          {npc.Stats.Level}");
                sb.AppendLine($"  Fatigue Base:   {npc.Stats.FatigueBase}");
                sb.AppendLine($"  Barter Gold:    {npc.Stats.BarterGold}");
                sb.AppendLine($"  Speed Mult:     {npc.Stats.SpeedMultiplier}");
                sb.AppendLine($"  Karma:          {npc.Stats.KarmaAlignment:F2}");
                sb.AppendLine($"  Disposition:    {npc.Stats.DispositionBase}");
                sb.AppendLine($"  Calc Range:     {npc.Stats.CalcMin} - {npc.Stats.CalcMax}");
                sb.AppendLine($"  Flags:          0x{npc.Stats.Flags:X8}");
            }

            if (npc.Race.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine($"Race:           {resolver.FormatFull(npc.Race.Value)}");
            }

            if (npc.Class.HasValue)
            {
                sb.AppendLine($"Class:          {resolver.FormatFull(npc.Class.Value)}");
            }

            if (npc.Script.HasValue)
            {
                sb.AppendLine($"Script:         {resolver.FormatFull(npc.Script.Value)}");
            }

            if (npc.VoiceType.HasValue)
            {
                sb.AppendLine($"Voice Type:     {resolver.FormatFull(npc.VoiceType.Value)}");
            }

            if (npc.Template.HasValue)
            {
                sb.AppendLine($"Template:       {resolver.FormatFull(npc.Template.Value)}");
            }

            if (npc.Factions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Factions:");
                foreach (var faction in npc.Factions)
                {
                    sb.AppendLine($"  - {resolver.FormatFull(faction.FactionFormId)} (Rank: {faction.Rank})");
                }
            }

            if (npc.Spells.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Spells/Abilities:");
                foreach (var spell in npc.Spells)
                {
                    sb.AppendLine($"  - {resolver.FormatFull(spell)}");
                }
            }

            if (npc.Inventory.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Inventory:");
                foreach (var item in npc.Inventory)
                {
                    sb.AppendLine($"  - {resolver.FormatFull(item.ItemFormId)} x{item.Count}");
                }
            }

            if (npc.Packages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("AI Packages:");
                foreach (var package in npc.Packages)
                {
                    sb.AppendLine($"  - {resolver.FormatFull(package)}");
                }
            }

            // FaceGen slider summary (counts only -- detailed values are in the per-NPC report)
            if (raceLookup != null && npc.FaceGenGeometrySymmetric != null)
            {
                var isFemale = npc.Stats != null && (npc.Stats.Flags & 1) == 1;
                raceLookup.TryGetValue(npc.Race ?? 0, out var race);

                var parts = new List<string>();
                AppendFaceGenSliderCount(parts, "FGGS", npc.FaceGenGeometrySymmetric,
                    m => FaceGenControls.ComputeGeometrySymmetric(m,
                        isFemale ? race?.FemaleFaceGenGeometrySymmetric : race?.MaleFaceGenGeometrySymmetric));
                AppendFaceGenSliderCount(parts, "FGGA", npc.FaceGenGeometryAsymmetric,
                    m => FaceGenControls.ComputeGeometryAsymmetric(m,
                        isFemale ? race?.FemaleFaceGenGeometryAsymmetric : race?.MaleFaceGenGeometryAsymmetric));
                AppendFaceGenSliderCount(parts, "FGTS", npc.FaceGenTextureSymmetric,
                    m => FaceGenControls.ComputeTextureSymmetric(m,
                        isFemale ? race?.FemaleFaceGenTextureSymmetric : race?.MaleFaceGenTextureSymmetric));

                if (parts.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"FaceGen:        {string.Join(", ", parts)}");
                }
            }
        }
    }

    private static void AppendFaceGenSliderCount(
        List<string> parts,
        string label,
        float[]? morphs,
        Func<float[], (string Name, float Value)[]> computeFunc)
    {
        if (morphs == null || morphs.Length == 0) return;
        var sliders = computeFunc(morphs);
        var active = sliders.Count(s => Math.Abs(s.Value) > 0.01f);
        if (active > 0)
        {
            parts.Add($"{label} {active}/{sliders.Length} active");
        }
    }

    /// <summary>
    ///     Generate a report for NPCs only.
    /// </summary>
    public static string GenerateNpcsReport(List<NpcRecord> npcs, FormIdResolver? resolver = null,
        IReadOnlyList<RaceRecord>? races = null)
    {
        var sb = new StringBuilder();
        AppendNpcsSection(sb, npcs, resolver ?? FormIdResolver.Empty, races);
        return sb.ToString();
    }

    internal static void AppendCreaturesSection(StringBuilder sb, List<CreatureRecord> creatures)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Creatures ({creatures.Count})");

        foreach (var creature in creatures.OrderBy(c => c.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "CREA", creature.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(creature.FormId)}");
            sb.AppendLine($"Editor ID:      {creature.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {creature.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {creature.CreatureTypeName}");
            sb.AppendLine($"Endianness:     {(creature.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{creature.Offset:X8}");

            if (creature.Stats != null)
            {
                sb.AppendLine();
                sb.AppendLine("Stats (ACBS):");
                sb.AppendLine($"  Level:          {creature.Stats.Level}");
                sb.AppendLine($"  Fatigue Base:   {creature.Stats.FatigueBase}");
            }

            if (creature.AttackDamage > 0)
            {
                sb.AppendLine($"  Attack Damage:  {creature.AttackDamage}");
            }
        }
    }

    /// <summary>
    ///     Generate a report for Creatures only.
    /// </summary>
    public static string GenerateCreaturesReport(List<CreatureRecord> creatures,
        FormIdResolver? _resolver = null)
    {
        var sb = new StringBuilder();
        AppendCreaturesSection(sb, creatures);
        return sb.ToString();
    }

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
}
