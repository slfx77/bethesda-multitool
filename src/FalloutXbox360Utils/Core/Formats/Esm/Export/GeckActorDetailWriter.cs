using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.FaceGen;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Detailed NPC stat/faction/inventory formatting, FaceGen morph data,
///     and per-NPC report generation. Extracted from GeckActorWriter.
/// </summary>
internal static class GeckActorDetailWriter
{
    /// <summary>Build a structured NPC report from an <see cref="NpcRecord" />.</summary>
    internal static RecordReport BuildNpcReport(NpcRecord npc, FormIdResolver resolver,
        RaceRecord? race = null)
    {
        var sections = new List<ReportSection>();

        // Identity — gender
        if (npc.Stats != null)
        {
            var gender = (npc.Stats.Flags & 1) == 1 ? "Female" : "Male";
            sections.Add(new ReportSection("Identity", [new ReportField("Gender", ReportValue.String(gender))]));
        }

        // Stats
        if (npc.Stats != null || npc.SpecialStats != null)
        {
            var statsFields = new List<ReportField>();
            if (npc.Stats != null)
                statsFields.Add(new ReportField("Level", ReportValue.Int(npc.Stats.Level)));
            if (npc.SpecialStats is { Length: 7 })
            {
                var s = npc.SpecialStats;
                var total = s[0] + s[1] + s[2] + s[3] + s[4] + s[5] + s[6];
                statsFields.Add(new ReportField("S.P.E.C.I.A.L.",
                    ReportValue.String(
                        $"{s[0]} ST, {s[1]} PE, {s[2]} EN, {s[3]} CH, {s[4]} IN, {s[5]} AG, {s[6]} LK  (Total: {total})")));
            }

            if (npc.Skills is { Length: 14 })
            {
                var sk = npc.Skills;
                var skillItems = new List<ReportValue>();
                for (var i = 0; i < 14; i++)
                {
                    var name = resolver.GetSkillName(i) ?? $"Skill#{i}";
                    skillItems.Add(new ReportValue.CompositeVal(
                        [
                            new ReportField("Skill", ReportValue.String(name)),
                            new ReportField("Value", ReportValue.Int(sk[i]))
                        ], $"{name}: {sk[i]}"));
                }

                statsFields.Add(new ReportField("Skills", ReportValue.List(skillItems)));
            }

            sections.Add(new ReportSection("Stats", statsFields));
        }

        // Derived Stats
        if (npc.Stats != null)
        {
            var derivedFields = new List<ReportField>();
            if (npc.SpecialStats is { Length: 7 } sp)
            {
                var baseHealth = sp[2] * 5 + 50;
                var calcHealth = baseHealth + npc.Stats.Level * 10;
                var calcFatigue = npc.Stats.FatigueBase + (sp[0] + sp[2]) * 10;
                derivedFields.Add(new ReportField("Base Health", ReportValue.Int(baseHealth)));
                derivedFields.Add(new ReportField("Calculated Health", ReportValue.Int(calcHealth)));
                derivedFields.Add(new ReportField("Fatigue", ReportValue.Int(npc.Stats.FatigueBase)));
                derivedFields.Add(new ReportField("Calc Fatigue", ReportValue.Int(calcFatigue)));
                derivedFields.Add(new ReportField("Critical Chance", ReportValue.FloatDisplay(sp[6], $"{sp[6]}")));
                derivedFields.Add(new ReportField("Speed Mult",
                    ReportValue.Int(npc.Stats.SpeedMultiplier, $"{npc.Stats.SpeedMultiplier}%")));
            }
            else
            {
                derivedFields.Add(new ReportField("Fatigue", ReportValue.Int(npc.Stats.FatigueBase)));
                derivedFields.Add(new ReportField("Speed Mult",
                    ReportValue.Int(npc.Stats.SpeedMultiplier, $"{npc.Stats.SpeedMultiplier}%")));
            }

            derivedFields.Add(new ReportField("Karma",
                ReportValue.FloatDisplay(npc.Stats.KarmaAlignment,
                    $"{npc.Stats.KarmaAlignment:F2}{GeckReportHelpers.FormatKarmaLabel(npc.Stats.KarmaAlignment)}")));
            derivedFields.Add(new ReportField("Disposition", ReportValue.Int(npc.Stats.DispositionBase)));
            derivedFields.Add(new ReportField("Barter Gold", ReportValue.Int(npc.Stats.BarterGold)));
            sections.Add(new ReportSection("Derived Stats", derivedFields));
        }

        // Combat
        if (npc.Race.HasValue || npc.Class.HasValue || npc.CombatStyleFormId.HasValue)
        {
            var combatFields = new List<ReportField>();
            if (npc.Race.HasValue)
                combatFields.Add(new ReportField("Race", ReportValue.FormId(npc.Race.Value, resolver),
                    $"0x{npc.Race.Value:X8}"));
            if (npc.Class.HasValue)
                combatFields.Add(new ReportField("Class", ReportValue.FormId(npc.Class.Value, resolver),
                    $"0x{npc.Class.Value:X8}"));
            if (npc.CombatStyleFormId.HasValue)
                combatFields.Add(new ReportField("Combat Style",
                    ReportValue.FormId(npc.CombatStyleFormId.Value, resolver),
                    $"0x{npc.CombatStyleFormId.Value:X8}"));
            sections.Add(new ReportSection("Combat", combatFields));
        }

        // Physical Traits
        if (npc.HairFormId.HasValue || npc.EyesFormId.HasValue || npc.HairLength.HasValue ||
            npc.HairColor.HasValue || npc.Height.HasValue || npc.Weight.HasValue)
        {
            var physFields = new List<ReportField>();
            if (npc.HairFormId.HasValue)
                physFields.Add(new ReportField("Hairstyle", ReportValue.FormId(npc.HairFormId.Value, resolver),
                    $"0x{npc.HairFormId.Value:X8}"));
            if (npc.HairLength.HasValue)
                physFields.Add(new ReportField("Hair Length", ReportValue.Float(npc.HairLength.Value, "F2")));
            if (npc.HairColor.HasValue)
                physFields.Add(new ReportField("Hair Color",
                    ReportValue.String(NpcRecord.FormatHairColor(npc.HairColor) ?? "")));
            if (npc.EyesFormId.HasValue)
                physFields.Add(new ReportField("Eyes", ReportValue.FormId(npc.EyesFormId.Value, resolver),
                    $"0x{npc.EyesFormId.Value:X8}"));
            if (npc.Height.HasValue)
                physFields.Add(new ReportField("Height", ReportValue.Float(npc.Height.Value, "F2")));
            if (npc.Weight.HasValue)
                physFields.Add(new ReportField("Weight", ReportValue.Float(npc.Weight.Value)));
            sections.Add(new ReportSection("Physical Traits", physFields));
        }

        // AI Data
        if (npc.AiData != null)
        {
            sections.Add(new ReportSection("AI Data",
            [
                new ReportField("Aggression",
                    ReportValue.String($"{npc.AiData.AggressionName} ({npc.AiData.Aggression})")),
                new ReportField("Confidence",
                    ReportValue.String($"{npc.AiData.ConfidenceName} ({npc.AiData.Confidence})")),
                new ReportField("Mood", ReportValue.String($"{npc.AiData.MoodName} ({npc.AiData.Mood})")),
                new ReportField("Assistance",
                    ReportValue.String($"{npc.AiData.AssistanceName} ({npc.AiData.Assistance})")),
                new ReportField("Energy Level", ReportValue.Int(npc.AiData.EnergyLevel)),
                new ReportField("Responsibility",
                    ReportValue.String(
                        $"{npc.AiData.ResponsibilityName} ({npc.AiData.Responsibility})"))
            ]));
        }

        // References
        if (npc.Script.HasValue || npc.VoiceType.HasValue || npc.Template.HasValue ||
            npc.OriginalRace.HasValue || npc.FaceNpc.HasValue)
        {
            var refFields = new List<ReportField>();
            if (npc.Script.HasValue)
                refFields.Add(new ReportField("Script",
                    ReportValue.FormId(npc.Script.Value,
                        resolver.FormatWithEditorId(npc.Script.Value)),
                    $"0x{npc.Script.Value:X8}"));
            if (npc.VoiceType.HasValue)
                refFields.Add(new ReportField("Voice Type",
                    ReportValue.FormId(npc.VoiceType.Value,
                        resolver.FormatWithEditorId(npc.VoiceType.Value)),
                    $"0x{npc.VoiceType.Value:X8}"));
            if (npc.Template.HasValue)
                refFields.Add(new ReportField("Template", ReportValue.FormId(npc.Template.Value, resolver),
                    $"0x{npc.Template.Value:X8}"));
            if (npc.OriginalRace.HasValue)
                refFields.Add(new ReportField("Original Race",
                    ReportValue.FormId(npc.OriginalRace.Value, resolver),
                    $"0x{npc.OriginalRace.Value:X8}"));
            if (npc.FaceNpc.HasValue)
                refFields.Add(new ReportField("Face NPC", ReportValue.FormId(npc.FaceNpc.Value, resolver),
                    $"0x{npc.FaceNpc.Value:X8}"));
            sections.Add(new ReportSection("References", refFields));
        }

        // Factions
        if (npc.Factions.Count > 0)
        {
            var factionItems = npc.Factions.OrderBy(f => f.FactionFormId)
                .Select(f =>
                {
                    var editorId = resolver.ResolveEditorId(f.FactionFormId);
                    var displayName = resolver.ResolveDisplayName(f.FactionFormId);
                    return (ReportValue)new ReportValue.CompositeVal(
                        [
                            new ReportField("EditorID", ReportValue.String(editorId)),
                            new ReportField("Name", ReportValue.String(displayName)),
                            new ReportField("Rank", ReportValue.Int(f.Rank))
                        ], $"{editorId} (Rank {f.Rank})");
                })
                .ToList();
            sections.Add(new ReportSection($"Factions ({npc.Factions.Count})",
                [new ReportField("Factions", ReportValue.List(factionItems))]));
        }

        // Inventory
        if (npc.Inventory.Count > 0)
        {
            var invItems = npc.Inventory.OrderBy(i => i.ItemFormId)
                .Select(i =>
                {
                    var editorId = resolver.ResolveEditorId(i.ItemFormId);
                    var displayName = resolver.ResolveDisplayName(i.ItemFormId);
                    return (ReportValue)new ReportValue.CompositeVal(
                        [
                            new ReportField("EditorID", ReportValue.String(editorId)),
                            new ReportField("Name", ReportValue.String(displayName)),
                            new ReportField("Qty", ReportValue.Int(i.Count))
                        ], $"{editorId} x{i.Count}");
                })
                .ToList();
            sections.Add(new ReportSection($"Inventory ({npc.Inventory.Count})",
                [new ReportField("Items", ReportValue.List(invItems))]));
        }

        // Spells
        if (npc.Spells.Count > 0)
        {
            var spellItems = npc.Spells.OrderBy(s => s)
                .Select(s => (ReportValue)ReportValue.FormId(s, resolver))
                .ToList();
            sections.Add(new ReportSection($"Spells/Abilities ({npc.Spells.Count})",
                [new ReportField("Spells", ReportValue.List(spellItems))]));
        }

        // AI Packages
        if (npc.Packages.Count > 0)
        {
            var pkgItems = npc.Packages.OrderBy(p => p)
                .Select(p => (ReportValue)ReportValue.FormId(p, resolver))
                .ToList();
            sections.Add(new ReportSection($"AI Packages ({npc.Packages.Count})",
                [new ReportField("Packages", ReportValue.List(pkgItems))]));
        }

        // FaceGen Morph Data — CTL-projected slider values + raw hex for exact comparison
        var hasFaceGen = npc.FaceGenGeometrySymmetric != null ||
                         npc.FaceGenGeometryAsymmetric != null ||
                         npc.FaceGenTextureSymmetric != null;
        if (hasFaceGen)
        {
            var fgFields = new List<ReportField>();
            var isFemale = npc.Stats != null && (npc.Stats.Flags & 1) == 1;

            // FGGS — Geometry Symmetric
            if (npc.FaceGenGeometrySymmetric != null)
            {
                var raceFggs = isFemale
                    ? race?.FemaleFaceGenGeometrySymmetric
                    : race?.MaleFaceGenGeometrySymmetric;
                AppendFaceGenChannel(fgFields, "FGGS",
                    npc.FaceGenGeometrySymmetric,
                    fggs => FaceGenControls.ComputeGeometrySymmetric(fggs, raceFggs));
            }

            // FGGA — Geometry Asymmetric
            if (npc.FaceGenGeometryAsymmetric != null)
            {
                var raceFgga = isFemale
                    ? race?.FemaleFaceGenGeometryAsymmetric
                    : race?.MaleFaceGenGeometryAsymmetric;
                AppendFaceGenChannel(fgFields, "FGGA",
                    npc.FaceGenGeometryAsymmetric,
                    fgga => FaceGenControls.ComputeGeometryAsymmetric(fgga, raceFgga));
            }

            // FGTS — Texture Symmetric
            if (npc.FaceGenTextureSymmetric != null)
            {
                var raceFgts = isFemale
                    ? race?.FemaleFaceGenTextureSymmetric
                    : race?.MaleFaceGenTextureSymmetric;
                AppendFaceGenChannel(fgFields, "FGTS",
                    npc.FaceGenTextureSymmetric,
                    fgts => FaceGenControls.ComputeTextureSymmetric(fgts, raceFgts));
            }

            sections.Add(new ReportSection("FaceGen Morph Data", fgFields));
        }

        return new RecordReport("NPC", npc.FormId, npc.EditorId, npc.FullName, sections);
    }

    /// <summary>
    ///     Append CTL-projected control values and raw hex for a single FaceGen channel
    ///     (FGGS, FGGA, or FGTS) to the report field list.
    /// </summary>
    private static void AppendFaceGenChannel(
        List<ReportField> fgFields,
        string label,
        float[] coefficients,
        Func<float[], (string Name, float Value)[]> computeControls)
    {
        // Compute named slider projections via CTL basis vectors
        var controls = computeControls(coefficients);
        var activeControls = controls
            .Where(c => Math.Abs(c.Value) > 0.01f)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => (ReportValue)new ReportValue.CompositeVal(
            [
                new ReportField("Control", ReportValue.String(c.Name)),
                new ReportField("Value", ReportValue.Float(c.Value, "F4"))
            ], $"{c.Name}: {c.Value:F4}"))
            .ToList();

        fgFields.Add(new ReportField($"{label} Controls",
            ReportValue.List(activeControls, $"{activeControls.Count}/{controls.Length} active")));

        // Raw IEEE 754 little-endian hex for exact byte-level comparison
        fgFields.Add(new ReportField($"{label} Hex", ReportValue.String(BuildFaceGenHexString(coefficients))));
    }

    /// <summary>
    ///     Convert a FaceGen float array to a hex string of IEEE 754 little-endian bytes.
    ///     Suitable for exact binary comparison or GECK import.
    /// </summary>
    private static string BuildFaceGenHexString(float[] values)
    {
        if (values.Length == 0) return "(empty)";

        var sb = new StringBuilder(values.Length * 12);
        for (var i = 0; i < values.Length; i++)
        {
            var bytes = BitConverter.GetBytes(values[i]);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);

            if (i > 0) sb.Append(' ');
            sb.Append($"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}");
        }

        return sb.ToString();
    }

    internal static void AppendNpcReportEntry(
        StringBuilder sb,
        NpcRecord npc,
        FormIdResolver resolver,
        RaceRecord? race = null)
    {
        sb.AppendLine();
        var report = BuildNpcReport(npc, resolver, race);
        sb.Append(ReportTextFormatter.Format(report));
    }

    /// <summary>
    ///     Generate a structured, human-readable per-NPC report with aligned tables
    ///     and display names for all referenced records (factions, inventory, spells, packages).
    /// </summary>
    public static string GenerateNpcReport(
        List<NpcRecord> npcs,
        FormIdResolver resolver,
        IReadOnlyList<RaceRecord>? races = null)
    {
        var sb = new StringBuilder();
        GeckReportHelpers.AppendHeader(sb, $"NPC Report ({npcs.Count:N0} NPCs)");
        sb.AppendLine();
        sb.AppendLine($"Total NPCs: {npcs.Count:N0}");

        var withFactions = npcs.Count(n => n.Factions.Count > 0);
        var withInventory = npcs.Count(n => n.Inventory.Count > 0);
        var withSpecial = npcs.Count(n => n.SpecialStats != null);
        var withSkills = npcs.Count(n => n.Skills != null);
        var withAiData = npcs.Count(n => n.AiData != null);
        var withFaceGen = npcs.Count(n => n.FaceGenGeometrySymmetric != null);
        var totalFactionRows = npcs.Sum(n => n.Factions.Count);
        var totalInventoryRows = npcs.Sum(n => n.Inventory.Count);
        sb.AppendLine($"NPCs with S.P.E.C.I.A.L.: {withSpecial:N0}");
        sb.AppendLine($"NPCs with Skills: {withSkills:N0}");
        sb.AppendLine($"NPCs with AI data: {withAiData:N0}");
        sb.AppendLine($"NPCs with FaceGen: {withFaceGen:N0}");
        sb.AppendLine($"NPCs with factions: {withFactions:N0} ({totalFactionRows:N0} total assignments)");
        sb.AppendLine($"NPCs with inventory: {withInventory:N0} ({totalInventoryRows:N0} total items)");

        // Build race lookup for FaceGen base coefficient merging
        var raceLookup = races?.ToDictionary(r => r.FormId);

        foreach (var npc in npcs.OrderBy(n => n.EditorId ?? ""))
        {
            RaceRecord? raceRecord = null;
            if (npc.Race.HasValue)
            {
                raceLookup?.TryGetValue(npc.Race.Value, out raceRecord);
            }

            AppendNpcReportEntry(sb, npc, resolver, raceRecord);
        }

        return sb.ToString();
    }
}
