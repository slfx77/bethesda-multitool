using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.FaceGen;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

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
            if (npc.Stats != null)
            {
                sb.AppendLine($"Gender:         {((npc.Stats.Flags & 1) == 1 ? "Female" : "Male")}");
            }

            sb.AppendLine($"Endianness:     {(npc.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{npc.Offset:X8}");

            // Stats
            if (npc.Stats != null || npc.SpecialStats != null)
            {
                sb.AppendLine();
                sb.AppendLine("Stats:");
                if (npc.Stats != null)
                {
                    sb.AppendLine($"  Level:          {npc.Stats.Level}");
                }

                if (npc.SpecialStats is { Length: 7 } sp)
                {
                    var total = sp[0] + sp[1] + sp[2] + sp[3] + sp[4] + sp[5] + sp[6];
                    sb.AppendLine(
                        $"  S.P.E.C.I.A.L.: {sp[0]} ST, {sp[1]} PE, {sp[2]} EN, {sp[3]} CH, {sp[4]} IN, {sp[5]} AG, {sp[6]} LK  (Total: {total})");
                }

                if (npc.Skills is { Length: 14 })
                {
                    var sk = npc.Skills;
                    var hasBigGuns = resolver.SkillEra?.BigGunsActive ?? false;

                    string Sk(int i)
                    {
                        return resolver.GetSkillName(i) ?? $"Skill#{i}";
                    }

                    sb.AppendLine("  Skills:");
                    sb.AppendLine(
                        $"    {Sk(0),-18}{sk[0],3}    {Sk(2),-18}{sk[2],3}    {Sk(3),-18}{sk[3],3}");
                    if (hasBigGuns)
                        sb.AppendLine(
                            $"    {Sk(1),-18}{sk[1],3}    {Sk(4),-18}{sk[4],3}    {Sk(5),-18}{sk[5],3}");
                    else
                        sb.AppendLine(
                            $"    {Sk(9),-18}{sk[9],3}    {Sk(4),-18}{sk[4],3}    {Sk(5),-18}{sk[5],3}");
                    sb.AppendLine(
                        $"    {Sk(6),-18}{sk[6],3}    {Sk(7),-18}{sk[7],3}    {Sk(8),-18}{sk[8],3}");
                    sb.AppendLine(
                        $"    {Sk(10),-18}{sk[10],3}    {Sk(11),-18}{sk[11],3}    {Sk(12),-18}{sk[12],3}");
                    sb.AppendLine($"    {Sk(13),-18}{sk[13],3}");
                }
            }

            // Derived Stats
            if (npc.Stats != null)
            {
                sb.AppendLine();
                sb.AppendLine("Derived Stats:");
                if (npc.SpecialStats is { Length: 7 } sp2)
                {
                    var str = sp2[0];
                    var end = sp2[2];
                    var lck = sp2[6];
                    var baseHealth = end * 5 + 50;
                    var calcHealth = baseHealth + npc.Stats.Level * 10;
                    var calcFatigue = npc.Stats.FatigueBase + (str + end) * 10;

                    sb.AppendLine($"  {"Base Health:",-18}{baseHealth,-10}{"Calculated Health:",-22}{calcHealth}");
                    sb.AppendLine(
                        $"  {"Fatigue:",-18}{npc.Stats.FatigueBase,-10}{"Calc Fatigue:",-22}{calcFatigue}");
                    sb.AppendLine(
                        $"  {"Critical Chance:",-18}{(float)lck,-10:F0}{"Speed Mult:",-22}{npc.Stats.SpeedMultiplier}%");
                    sb.AppendLine(
                        $"  {"Melee Damage:",-18}{str * 0.5f,-10:F2}{"Unarmed Damage:",-22}{0.5f + str * 0.1f:F2}");
                    sb.AppendLine(
                        $"  {"Poison Resist:",-18}{(end - 1) * 5f,-10:F2}{"Rad Resist:",-22}{(end - 1) * 2f:F2}");
                }
                else
                {
                    sb.AppendLine(
                        $"  {"Fatigue:",-18}{npc.Stats.FatigueBase,-10}{"Speed Mult:",-22}{npc.Stats.SpeedMultiplier}%");
                }

                sb.AppendLine(
                    $"  {"Karma:",-18}{npc.Stats.KarmaAlignment:F2}{GeckReportHelpers.FormatKarmaLabel(npc.Stats.KarmaAlignment)}");
                sb.AppendLine(
                    $"  {"Disposition:",-18}{npc.Stats.DispositionBase,-10}{"Barter Gold:",-22}{npc.Stats.BarterGold}");
            }

            // Combat
            if (npc.Race.HasValue)
            {
                sb.AppendLine($"Race:           {resolver.FormatFull(npc.Race.Value)}");
            }

            if (npc.Class.HasValue)
            {
                sb.AppendLine($"Class:          {resolver.FormatFull(npc.Class.Value)}");
            }

            if (npc.CombatStyleFormId.HasValue)
            {
                sb.AppendLine($"Combat Style:   {resolver.FormatFull(npc.CombatStyleFormId.Value)}");
            }

            // Physical Traits
            if (npc.HairFormId.HasValue || npc.EyesFormId.HasValue || npc.HairLength.HasValue ||
                npc.HairColor.HasValue || npc.Height.HasValue || npc.Weight.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine("Physical Traits:");
                if (npc.HairFormId.HasValue)
                {
                    sb.AppendLine($"  Hairstyle:      {resolver.FormatFull(npc.HairFormId.Value)}");
                }

                if (npc.HairLength.HasValue)
                {
                    sb.AppendLine($"  Hair Length:    {npc.HairLength.Value:F2}");
                }

                if (npc.HairColor.HasValue)
                {
                    sb.AppendLine($"  Hair Color:    {NpcRecord.FormatHairColor(npc.HairColor)}");
                }

                if (npc.EyesFormId.HasValue)
                {
                    sb.AppendLine($"  Eyes:           {resolver.FormatFull(npc.EyesFormId.Value)}");
                }

                if (npc.Height.HasValue)
                {
                    sb.AppendLine($"  Height:         {npc.Height.Value:F2}");
                }

                if (npc.Weight.HasValue)
                {
                    sb.AppendLine($"  Weight:         {npc.Weight.Value:F1}");
                }
            }

            // AI Data
            if (npc.AiData != null)
            {
                sb.AppendLine();
                sb.AppendLine("AI Data:");
                sb.AppendLine($"  Aggression:     {npc.AiData.AggressionName} ({npc.AiData.Aggression})");
                sb.AppendLine($"  Confidence:     {npc.AiData.ConfidenceName} ({npc.AiData.Confidence})");
                sb.AppendLine($"  Mood:           {npc.AiData.MoodName} ({npc.AiData.Mood})");
                sb.AppendLine($"  Assistance:     {npc.AiData.AssistanceName} ({npc.AiData.Assistance})");
                sb.AppendLine($"  Energy Level:   {npc.AiData.EnergyLevel}");
                sb.AppendLine($"  Responsibility: {npc.AiData.ResponsibilityName} ({npc.AiData.Responsibility})");
            }

            // References
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

            if (npc.OriginalRace.HasValue)
            {
                sb.AppendLine($"Original Race:  {resolver.FormatFull(npc.OriginalRace.Value)}");
            }

            if (npc.FaceNpc.HasValue)
            {
                sb.AppendLine($"Face NPC:       {resolver.FormatFull(npc.FaceNpc.Value)}");
            }

            // Factions with display names
            if (npc.Factions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Factions ({npc.Factions.Count}):");
                sb.AppendLine($"  {"EditorID",-32} {"Name",-32} {"Rank",4}");
                sb.AppendLine($"  {new string('-', 32)} {new string('-', 32)} {new string('-', 4)}");
                foreach (var faction in npc.Factions.OrderBy(f => f.FactionFormId))
                {
                    var editorId = resolver.ResolveEditorId(faction.FactionFormId);
                    var displayName = resolver.ResolveDisplayName(faction.FactionFormId);
                    sb.AppendLine(
                        $"  {GeckReportHelpers.Truncate(editorId, 32),-32} {GeckReportHelpers.Truncate(displayName, 32),-32} {faction.Rank,4}");
                }
            }

            // Inventory with display names
            if (npc.Inventory.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Inventory ({npc.Inventory.Count}):");
                sb.AppendLine($"  {"EditorID",-32} {"Name",-32} {"Qty",5}");
                sb.AppendLine($"  {new string('-', 32)} {new string('-', 32)} {new string('-', 5)}");
                foreach (var item in npc.Inventory.OrderBy(i => i.ItemFormId))
                {
                    var editorId = resolver.ResolveEditorId(item.ItemFormId);
                    var displayName = resolver.ResolveDisplayName(item.ItemFormId);
                    sb.AppendLine(
                        $"  {GeckReportHelpers.Truncate(editorId, 32),-32} {GeckReportHelpers.Truncate(displayName, 32),-32} {item.Count,5}");
                }
            }

            // Spells with display names
            if (npc.Spells.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Spells/Abilities ({npc.Spells.Count}):");
                foreach (var spell in npc.Spells.OrderBy(s => s))
                {
                    sb.AppendLine($"  - {resolver.FormatFull(spell)}");
                }
            }

            // AI Packages with display names
            if (npc.Packages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"AI Packages ({npc.Packages.Count}):");
                foreach (var package in npc.Packages.OrderBy(p => p))
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

    internal static void AppendCreaturesSection(StringBuilder sb, List<CreatureRecord> creatures,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Creatures ({creatures.Count})");

        var byType = creatures.GroupBy(c => c.CreatureTypeName).OrderByDescending(g => g.Count()).ToList();
        sb.AppendLine($"Total Creatures: {creatures.Count:N0}");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key}: {group.Count():N0}");
        }

        sb.AppendLine();

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
                sb.AppendLine($"  Barter Gold:    {creature.Stats.BarterGold}");
                sb.AppendLine($"  Speed Mult:     {creature.Stats.SpeedMultiplier}");
                sb.AppendLine($"  Calc Range:     {creature.Stats.CalcMin} - {creature.Stats.CalcMax}");
                sb.AppendLine($"  Flags:          0x{creature.Stats.Flags:X8}");
            }

            sb.AppendLine();
            sb.AppendLine("Combat:");
            sb.AppendLine($"  Attack Damage:  {creature.AttackDamage}");
            sb.AppendLine($"  Combat Skill:   {creature.CombatSkill}");
            sb.AppendLine($"  Magic Skill:    {creature.MagicSkill}");
            sb.AppendLine($"  Stealth Skill:  {creature.StealthSkill}");

            if (creature.AiData != null)
            {
                var ai = creature.AiData;
                sb.AppendLine();
                sb.AppendLine("AI Data (AIDT):");
                sb.AppendLine($"  Aggression:     {ai.AggressionName}");
                sb.AppendLine($"  Confidence:     {ai.ConfidenceName}");
                sb.AppendLine($"  Assistance:     {ai.AssistanceName}");
                sb.AppendLine($"  Mood:           {ai.MoodName}");
                sb.AppendLine($"  Energy:         {ai.EnergyLevel}");
                sb.AppendLine($"  Responsibility: {ai.ResponsibilityName}");
                if (ai.Flags != 0)
                {
                    sb.AppendLine($"  Service Flags:  0x{ai.Flags:X8}");
                }
            }

            if (creature.Script.HasValue)
            {
                sb.AppendLine($"Script:         {resolver.FormatFull(creature.Script.Value)}");
            }

            if (creature.DeathItem.HasValue)
            {
                sb.AppendLine($"Death Item:     {resolver.FormatFull(creature.DeathItem.Value)}");
            }

            if (creature.Factions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Factions:");
                foreach (var faction in creature.Factions.OrderBy(f => f.FactionFormId))
                {
                    sb.AppendLine($"  - {resolver.FormatFull(faction.FactionFormId)} (Rank: {faction.Rank})");
                }
            }

            if (creature.Spells.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Spells/Abilities:");
                foreach (var spell in creature.Spells.OrderBy(s => s))
                {
                    sb.AppendLine($"  - {resolver.FormatFull(spell)}");
                }
            }

            if (creature.Packages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("AI Packages:");
                foreach (var package in creature.Packages.OrderBy(p => p))
                {
                    sb.AppendLine($"  - {resolver.FormatFull(package)}");
                }
            }

            if (!string.IsNullOrEmpty(creature.ModelPath))
            {
                sb.AppendLine($"Model:          {creature.ModelPath}");
            }

            sb.AppendLine();
        }
    }

    /// <summary>
    ///     Generate a report for Creatures only.
    /// </summary>
    public static string GenerateCreaturesReport(List<CreatureRecord> creatures,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendCreaturesSection(sb, creatures, resolver ?? FormIdResolver.Empty);
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
