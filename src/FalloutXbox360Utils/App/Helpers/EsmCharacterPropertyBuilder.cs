using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds property entries for character-related records: NPCs, creatures, races,
///     including ACBS stats, AI data, S.P.E.C.I.A.L., skills, and FaceGen morphs.
/// </summary>
internal static class EsmCharacterPropertyBuilder
{
    /// <summary>
    ///     Processes ActorBaseSubrecord (ACBS) into property entries.
    /// </summary>
    internal static void AddActorBaseStats(
        List<EsmPropertyEntry> properties,
        ActorBaseSubrecord stats,
        bool isNpc)
    {
        // Common fields (both NPC and Creature)
        var gender = (stats.Flags & 1) == 1 ? "Female" : "Male";
        properties.Add(new EsmPropertyEntry { Name = "Gender", Value = gender, Category = "Characteristics" });
        properties.Add(new EsmPropertyEntry
        {
            Name = "Actor Flags",
            Value = FlagRegistry.DecodeFlagNamesWithHex(stats.Flags, FlagRegistry.ActorBaseFlags),
            Category = "Characteristics"
        });
        properties.Add(new EsmPropertyEntry
            { Name = "Level", Value = stats.Level.ToString(), Category = "Attributes" });
        properties.Add(new EsmPropertyEntry
            { Name = "Calc Min Level", Value = stats.CalcMin.ToString(), Category = "Attributes" });
        properties.Add(new EsmPropertyEntry
            { Name = "Calc Max Level", Value = stats.CalcMax.ToString(), Category = "Attributes" });
        properties.Add(new EsmPropertyEntry
            { Name = "Fatigue", Value = stats.FatigueBase.ToString(), Category = "Attributes" });
        properties.Add(new EsmPropertyEntry
            { Name = "Speed Multiplier", Value = $"{stats.SpeedMultiplier}%", Category = "Attributes" });

        // NPC-only fields (creatures don't have barter gold, karma, disposition)
        if (isNpc)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Barter Gold", Value = stats.BarterGold.ToString(), Category = "Attributes" });
            properties.Add(new EsmPropertyEntry
                { Name = "Karma", Value = $"{stats.KarmaAlignment:F2}", Category = "Attributes" });
            properties.Add(new EsmPropertyEntry
                { Name = "Disposition", Value = stats.DispositionBase.ToString(), Category = "Attributes" });
        }

        if (stats.TemplateFlags != 0)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Template Flags",
                Value = FlagRegistry.DecodeFlagNamesWithHex(stats.TemplateFlags, FlagRegistry.TemplateUseFlags),
                Category = "Characteristics"
            });
        }
    }

    /// <summary>
    ///     Processes NpcAiData into property entries.
    /// </summary>
    internal static void AddAiData(List<EsmPropertyEntry> properties, NpcAiData ai)
    {
        properties.Add(new EsmPropertyEntry
            { Name = "Aggression", Value = $"{ai.AggressionName} ({ai.Aggression})", Category = "AI" });
        properties.Add(new EsmPropertyEntry
            { Name = "Confidence", Value = $"{ai.ConfidenceName} ({ai.Confidence})", Category = "AI" });
        properties.Add(new EsmPropertyEntry
            { Name = "Mood", Value = $"{ai.MoodName} ({ai.Mood})", Category = "AI" });
        properties.Add(new EsmPropertyEntry
            { Name = "Assistance", Value = $"{ai.AssistanceName} ({ai.Assistance})", Category = "AI" });
        properties.Add(new EsmPropertyEntry
            { Name = "Energy Level", Value = ai.EnergyLevel.ToString(), Category = "AI" });
        properties.Add(new EsmPropertyEntry
        {
            Name = "Responsibility",
            Value = $"{ai.ResponsibilityName} ({ai.Responsibility})",
            Category = "AI"
        });
    }

    /// <summary>
    ///     Processes S.P.E.C.I.A.L. stats (byte[7]) into a property entry.
    /// </summary>
    internal static void AddSpecialStats(List<EsmPropertyEntry> properties, byte[] special)
    {
        var total = special.Sum(b => b);
        var formatted = $"{special[0]} ST, {special[1]} PE, {special[2]} EN, {special[3]} CH, " +
                        $"{special[4]} IN, {special[5]} AG, {special[6]} LK  (Total: {total})";
        properties.Add(new EsmPropertyEntry
            { Name = "S.P.E.C.I.A.L.", Value = formatted, Category = "Attributes" });
    }

    /// <summary>
    ///     Processes Skills (byte[14]) into an expandable property entry.
    ///     Uses AVIF-sourced names from the resolver when available, falling back to hardcoded names.
    /// </summary>
    internal static void AddSkills(List<EsmPropertyEntry> properties, byte[] skills,
        FormIdResolver? resolver = null)
    {
        var subItems = new List<EsmPropertyEntry>();
        for (var i = 0; i < skills.Length && i < EsmPropertyFormatter.SkillNames.Length; i++)
        {
            if (i == 1) continue; // Skip BigGuns (index 1) - unused in Fallout NV
            var name = resolver?.GetSkillName(i) ?? EsmPropertyFormatter.SkillNames[i];
            subItems.Add(new EsmPropertyEntry { Name = name, Value = skills[i].ToString() });
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = "Skills",
            Value = $"{subItems.Count} skills",
            Category = "Attributes",
            IsExpandable = true,
            SubItems = subItems
        });
    }

    /// <summary>
    ///     Processes FaceGen float arrays into slider and raw hex property entries.
    /// </summary>
    internal static void AddFaceGenMorphs(
        List<EsmPropertyEntry> properties,
        string propertyName,
        string displayName,
        float[] morphs,
        object record,
        IReadOnlyDictionary<uint, RaceRecord>? raceLookup)
    {
        // For NPC records, compute and display slider values with race base merging
        if (record is NpcRecord npc)
        {
            var isFemale = npc.Stats != null && (npc.Stats.Flags & 1) == 1;
            RaceRecord? race = null;
            if (npc.Race.HasValue && raceLookup != null)
            {
                raceLookup.TryGetValue(npc.Race.Value, out race);
            }

            float[]? raceBase = null;
            (string Name, float Value)[]? sliders = null;

            if (propertyName == "FaceGenGeometrySymmetric" && morphs.Length == 50)
            {
                raceBase = isFemale ? race?.FemaleFaceGenGeometrySymmetric : race?.MaleFaceGenGeometrySymmetric;
                sliders = FaceGenControls.ComputeGeometrySymmetric(morphs, raceBase);
            }
            else if (propertyName == "FaceGenGeometryAsymmetric" && morphs.Length == 30)
            {
                raceBase = isFemale
                    ? race?.FemaleFaceGenGeometryAsymmetric
                    : race?.MaleFaceGenGeometryAsymmetric;
                sliders = FaceGenControls.ComputeGeometryAsymmetric(morphs, raceBase);
            }
            else if (propertyName == "FaceGenTextureSymmetric" && morphs.Length == 50)
            {
                raceBase = isFemale ? race?.FemaleFaceGenTextureSymmetric : race?.MaleFaceGenTextureSymmetric;
                sliders = FaceGenControls.ComputeTextureSymmetric(morphs, raceBase);
            }

            if (sliders is { Length: > 0 })
            {
                var activeSliders = sliders
                    .Where(s => Math.Abs(s.Value) > 0.01f)
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (activeSliders.Count > 0)
                {
                    var sliderSubItems = activeSliders
                        .Select(s => new EsmPropertyEntry { Name = s.Name, Value = $"{s.Value:F4}" })
                        .ToList();

                    properties.Add(new EsmPropertyEntry
                    {
                        Name = $"{displayName} Sliders",
                        Value = $"{activeSliders.Count} of {sliders.Length} active",
                        Category = "Characteristics",
                        IsExpandable = true,
                        SubItems = sliderSubItems
                    });
                }
            }
        }

        // Raw hex panel (collapsed) - explicit little-endian bytes
        var rawBytes = new byte[morphs.Length * 4];
        for (var i = 0; i < morphs.Length; i++)
        {
            var bytes = BitConverter.GetBytes(morphs[i]);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            Buffer.BlockCopy(bytes, 0, rawBytes, i * 4, 4);
        }

        var hexLines = new List<string>();
        for (var i = 0; i < rawBytes.Length; i += 16)
        {
            var lineBytes = rawBytes.Skip(i).Take(16);
            hexLines.Add(string.Join(" ", lineBytes.Select(b => b.ToString("X2"))));
        }

        var hexBlock = string.Join("\n", hexLines);
        var hexSubItems = new List<EsmPropertyEntry>
        {
            new() { Name = "", Value = hexBlock }
        };

        properties.Add(new EsmPropertyEntry
        {
            Name = $"{displayName} Raw Hex",
            Value = $"{rawBytes.Length} bytes (little-endian)",
            Category = "Characteristics",
            IsExpandable = true,
            SubItems = hexSubItems
        });
    }

    /// <summary>
    ///     Processes class-specific tag skills (Actor Value codes) into a property entry.
    ///     Uses AVIF-sourced names from the resolver when available, falling back to hardcoded names.
    /// </summary>
    internal static void AddTagSkills(List<EsmPropertyEntry> properties, int[] tagSkillIndices,
        FormIdResolver? resolver = null)
    {
        var names = tagSkillIndices
            .Where(i => i >= 0)
            .Select(i => resolver?.GetActorValueName(i) ?? EsmPropertyFormatter.ActorValueToSkillName(i) ?? $"AV#{i}");
        properties.Add(new EsmPropertyEntry
        {
            Name = "Tag Skills",
            Value = string.Join(", ", names),
            Category = "Attributes"
        });
    }

    /// <summary>
    ///     Processes class-specific training skill (Actor Value code) into a property entry.
    ///     Uses AVIF-sourced names from the resolver when available, falling back to hardcoded names.
    /// </summary>
    internal static void AddTrainingSkill(List<EsmPropertyEntry> properties, byte trainingIdx,
        FormIdResolver? resolver = null)
    {
        var skillName = resolver?.GetActorValueName(trainingIdx)
                        ?? EsmPropertyFormatter.ActorValueToSkillName(trainingIdx)
                        ?? $"Unknown ({trainingIdx})";
        properties.Add(new EsmPropertyEntry
        {
            Name = "Training Skill",
            Value = skillName,
            Category = "Attributes"
        });
    }

    /// <summary>
    ///     Processes class-specific attribute weights (byte[7]) into a property entry.
    /// </summary>
    internal static void AddAttributeWeights(List<EsmPropertyEntry> properties, byte[] attrWeights)
    {
        var formatted =
            $"{attrWeights[0]} ST, {attrWeights[1]} PE, {attrWeights[2]} EN, {attrWeights[3]} CH, " +
            $"{attrWeights[4]} IN, {attrWeights[5]} AG, {attrWeights[6]} LK";
        properties.Add(new EsmPropertyEntry
        {
            Name = "Attribute Weights",
            Value = formatted,
            Category = "Attributes"
        });
    }

    /// <summary>
    ///     Adds creature-specific properties (type, combat/magic/stealth skills, attack damage).
    /// </summary>
    internal static void AddCreatureProperties(List<EsmPropertyEntry> properties, object record)
    {
        if (record is not CreatureRecord crea)
        {
            return;
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = "Creature Type",
            Value = crea.CreatureTypeName,
            Category = "Characteristics"
        });

        if (crea.CombatSkill > 0 || crea.MagicSkill > 0 || crea.StealthSkill > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Combat Skill", Value = crea.CombatSkill.ToString(), Category = "Attributes" });
            properties.Add(new EsmPropertyEntry
                { Name = "Magic Skill", Value = crea.MagicSkill.ToString(), Category = "Attributes" });
            properties.Add(new EsmPropertyEntry
                { Name = "Stealth Skill", Value = crea.StealthSkill.ToString(), Category = "Attributes" });
        }

        if (crea.AttackDamage != 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Attack Damage", Value = crea.AttackDamage.ToString(), Category = "Attributes" });
        }
    }

    /// <summary>
    ///     Adds NPC derived stats (health, fatigue, crit chance, etc.) calculated from S.P.E.C.I.A.L.
    /// </summary>
    internal static void AddNpcDerivedStats(List<EsmPropertyEntry> properties, object record)
    {
        if (record is not NpcRecord npc || npc.SpecialStats is not { Length: >= 7 } || npc.Stats == null)
        {
            return;
        }

        var str = npc.SpecialStats[0];
        var end = npc.SpecialStats[2];
        var lck = npc.SpecialStats[6];
        var level = npc.Stats.Level;
        var fatigueBase = npc.Stats.FatigueBase;

        var baseHealth = end * 5 + 50;
        var calcHealth = baseHealth + level * 10;
        var calcFatigue = fatigueBase + (str + end) * 10;
        var critChance = (float)lck;
        var meleeDamage = str * 0.5f;
        var unarmedDamage = 0.5f + str * 0.1f;
        var poisonResist = (end - 1) * 5;
        var radResist = (end - 1) * 2;

        properties.Add(new EsmPropertyEntry
            { Name = "Health", Value = $"{calcHealth} (Base: {baseHealth} + Level\u00d710)", Category = "Derived Stats" });
        properties.Add(new EsmPropertyEntry
        {
            Name = "Fatigue", Value = $"{calcFatigue} (Base: {fatigueBase} + (STR+END)\u00d710)",
            Category = "Derived Stats"
        });
        properties.Add(new EsmPropertyEntry
            { Name = "Critical Chance", Value = $"{critChance:F0}%", Category = "Derived Stats" });
        properties.Add(new EsmPropertyEntry
            { Name = "Melee Damage", Value = $"{meleeDamage:F1}", Category = "Derived Stats" });
        properties.Add(new EsmPropertyEntry
            { Name = "Unarmed Damage", Value = $"{unarmedDamage:F1}", Category = "Derived Stats" });
        properties.Add(new EsmPropertyEntry
            { Name = "Poison Resistance", Value = $"{poisonResist}%", Category = "Derived Stats" });
        properties.Add(new EsmPropertyEntry
            { Name = "Radiation Resistance", Value = $"{radResist}%", Category = "Derived Stats" });
    }

    /// <summary>
    ///     Adds race skill boosts as a property entry.
    ///     Uses AVIF-sourced names from the resolver when available, falling back to hardcoded names.
    /// </summary>
    internal static void AddRaceSkillBoosts(List<EsmPropertyEntry> properties, object record,
        FormIdResolver? resolver = null)
    {
        if (record is not RaceRecord raceRecord || raceRecord.SkillBoosts.Count == 0)
        {
            return;
        }

        var boosts = raceRecord.SkillBoosts
            .Select(b =>
            {
                var name = resolver?.GetActorValueName(b.SkillIndex)
                           ?? EsmPropertyFormatter.ActorValueToSkillName(b.SkillIndex)
                           ?? $"AV#{b.SkillIndex}";
                return $"{name} {b.Boost:+#;-#;0}";
            });
        properties.Add(new EsmPropertyEntry
        {
            Name = "Skill Boosts",
            Value = string.Join(", ", boosts),
            Category = "Attributes"
        });
    }
}
