using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

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
            sections.Add(new("Identity", [new("Gender", ReportValue.String(gender))]));
        }

        // Stats
        if (npc.Stats != null || npc.SpecialStats != null)
        {
            var statsFields = new List<ReportField>();
            if (npc.Stats != null)
                statsFields.Add(new("Level", ReportValue.Int(npc.Stats.Level)));
            if (npc.SpecialStats is { Length: 7 })
            {
                var s = npc.SpecialStats;
                var total = s[0] + s[1] + s[2] + s[3] + s[4] + s[5] + s[6];
                statsFields.Add(new("S.P.E.C.I.A.L.",
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
                        new("Skill", ReportValue.String(name)),
                        new("Value", ReportValue.Int(sk[i]))
                    ], $"{name}: {sk[i]}"));
                }

                statsFields.Add(new("Skills", ReportValue.List(skillItems)));
            }

            sections.Add(new("Stats", statsFields));
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
                derivedFields.Add(new("Base Health", ReportValue.Int(baseHealth)));
                derivedFields.Add(new("Calculated Health", ReportValue.Int(calcHealth)));
                derivedFields.Add(new("Fatigue", ReportValue.Int(npc.Stats.FatigueBase)));
                derivedFields.Add(new("Calc Fatigue", ReportValue.Int(calcFatigue)));
                derivedFields.Add(new("Critical Chance", ReportValue.FloatDisplay(sp[6], $"{sp[6]}")));
                derivedFields.Add(new("Speed Mult",
                    ReportValue.Int(npc.Stats.SpeedMultiplier, $"{npc.Stats.SpeedMultiplier}%")));
            }
            else
            {
                derivedFields.Add(new("Fatigue", ReportValue.Int(npc.Stats.FatigueBase)));
                derivedFields.Add(new("Speed Mult",
                    ReportValue.Int(npc.Stats.SpeedMultiplier, $"{npc.Stats.SpeedMultiplier}%")));
            }

            derivedFields.Add(new("Karma",
                ReportValue.FloatDisplay(npc.Stats.KarmaAlignment,
                    $"{npc.Stats.KarmaAlignment:F2}{GeckReportHelpers.FormatKarmaLabel(npc.Stats.KarmaAlignment)}")));
            derivedFields.Add(new("Disposition", ReportValue.Int(npc.Stats.DispositionBase)));
            derivedFields.Add(new("Barter Gold", ReportValue.Int(npc.Stats.BarterGold)));
            sections.Add(new("Derived Stats", derivedFields));
        }

        // Combat
        if (npc.Race.HasValue || npc.Class.HasValue || npc.CombatStyleFormId.HasValue)
        {
            var combatFields = new List<ReportField>();
            if (npc.Race.HasValue)
                combatFields.Add(new("Race", ReportValue.FormId(npc.Race.Value, resolver),
                    $"0x{npc.Race.Value:X8}"));
            if (npc.Class.HasValue)
                combatFields.Add(new("Class", ReportValue.FormId(npc.Class.Value, resolver),
                    $"0x{npc.Class.Value:X8}"));
            if (npc.CombatStyleFormId.HasValue)
                combatFields.Add(new("Combat Style",
                    ReportValue.FormId(npc.CombatStyleFormId.Value, resolver),
                    $"0x{npc.CombatStyleFormId.Value:X8}"));
            sections.Add(new("Combat", combatFields));
        }

        // Physical Traits
        if (npc.HairFormId.HasValue || npc.EyesFormId.HasValue || npc.HairLength.HasValue ||
            npc.HairColor.HasValue || npc.Height.HasValue || npc.Weight.HasValue)
        {
            var physFields = new List<ReportField>();
            if (npc.HairFormId.HasValue)
                physFields.Add(new("Hairstyle", ReportValue.FormId(npc.HairFormId.Value, resolver),
                    $"0x{npc.HairFormId.Value:X8}"));
            if (npc.HairLength.HasValue)
                physFields.Add(new("Hair Length", ReportValue.Float(npc.HairLength.Value, "F2")));
            if (npc.HairColor.HasValue)
                physFields.Add(new("Hair Color",
                    ReportValue.String(NpcRecord.FormatHairColor(npc.HairColor))));
            if (npc.EyesFormId.HasValue)
                physFields.Add(new("Eyes", ReportValue.FormId(npc.EyesFormId.Value, resolver),
                    $"0x{npc.EyesFormId.Value:X8}"));
            if (npc.Height.HasValue)
                physFields.Add(new("Height", ReportValue.Float(npc.Height.Value, "F2")));
            if (npc.Weight.HasValue)
                physFields.Add(new("Weight", ReportValue.Float(npc.Weight.Value, "F1")));
            sections.Add(new("Physical Traits", physFields));
        }

        // AI Data
        if (npc.AiData != null)
        {
            sections.Add(new("AI Data",
            [
                new("Aggression",
                    ReportValue.String($"{npc.AiData.AggressionName} ({npc.AiData.Aggression})")),
                new("Confidence",
                    ReportValue.String($"{npc.AiData.ConfidenceName} ({npc.AiData.Confidence})")),
                new("Mood", ReportValue.String($"{npc.AiData.MoodName} ({npc.AiData.Mood})")),
                new("Assistance",
                    ReportValue.String($"{npc.AiData.AssistanceName} ({npc.AiData.Assistance})")),
                new("Energy Level", ReportValue.Int(npc.AiData.EnergyLevel)),
                new("Responsibility",
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
                refFields.Add(new("Script",
                    ReportValue.FormId(npc.Script.Value,
                        resolver.FormatWithEditorId(npc.Script.Value)),
                    $"0x{npc.Script.Value:X8}"));
            if (npc.VoiceType.HasValue)
                refFields.Add(new("Voice Type",
                    ReportValue.FormId(npc.VoiceType.Value,
                        resolver.FormatWithEditorId(npc.VoiceType.Value)),
                    $"0x{npc.VoiceType.Value:X8}"));
            if (npc.Template.HasValue)
                refFields.Add(new("Template", ReportValue.FormId(npc.Template.Value, resolver),
                    $"0x{npc.Template.Value:X8}"));
            if (npc.OriginalRace.HasValue)
                refFields.Add(new("Original Race",
                    ReportValue.FormId(npc.OriginalRace.Value, resolver),
                    $"0x{npc.OriginalRace.Value:X8}"));
            if (npc.FaceNpc.HasValue)
                refFields.Add(new("Face NPC", ReportValue.FormId(npc.FaceNpc.Value, resolver),
                    $"0x{npc.FaceNpc.Value:X8}"));
            sections.Add(new("References", refFields));
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
                        new("EditorID", ReportValue.String(editorId)),
                        new("Name", ReportValue.String(displayName)),
                        new("Rank", ReportValue.Int(f.Rank))
                    ], $"{editorId} (Rank {f.Rank})");
                })
                .ToList();
            sections.Add(new($"Factions ({npc.Factions.Count})",
                [new("Factions", ReportValue.List(factionItems))]));
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
                        new("EditorID", ReportValue.String(editorId)),
                        new("Name", ReportValue.String(displayName)),
                        new("Qty", ReportValue.Int(i.Count))
                    ], $"{editorId} x{i.Count}");
                })
                .ToList();
            sections.Add(new($"Inventory ({npc.Inventory.Count})",
                [new("Items", ReportValue.List(invItems))]));
        }

        // Spells
        if (npc.Spells.Count > 0)
        {
            var spellItems = npc.Spells.OrderBy(s => s)
                .Select(s => (ReportValue)ReportValue.FormId(s, resolver))
                .ToList();
            sections.Add(new($"Spells/Abilities ({npc.Spells.Count})",
                [new("Spells", ReportValue.List(spellItems))]));
        }

        // AI Packages
        if (npc.Packages.Count > 0)
        {
            var pkgItems = npc.Packages.OrderBy(p => p)
                .Select(p => (ReportValue)ReportValue.FormId(p, resolver))
                .ToList();
            sections.Add(new($"AI Packages ({npc.Packages.Count})",
                [new("Packages", ReportValue.List(pkgItems))]));
        }

        // FaceGen Morph Data (store as raw hex strings for comparison — the computed projections
        // depend on the race record which may not be available)
        var hasFaceGen = npc.FaceGenGeometrySymmetric != null ||
                         npc.FaceGenGeometryAsymmetric != null ||
                         npc.FaceGenTextureSymmetric != null;
        if (hasFaceGen)
        {
            var fgFields = new List<ReportField>();
            if (npc.FaceGenGeometrySymmetric != null)
                fgFields.Add(new("FGGS", ReportValue.String(FormatFaceGenHex(npc.FaceGenGeometrySymmetric))));
            if (npc.FaceGenGeometryAsymmetric != null)
                fgFields.Add(new("FGGA", ReportValue.String(FormatFaceGenHex(npc.FaceGenGeometryAsymmetric))));
            if (npc.FaceGenTextureSymmetric != null)
                fgFields.Add(new("FGTS", ReportValue.String(FormatFaceGenHex(npc.FaceGenTextureSymmetric))));
            sections.Add(new("FaceGen Morph Data", fgFields));
        }

        return new RecordReport("NPC", npc.FormId, npc.EditorId, npc.FullName, sections);
    }

    private static string FormatFaceGenHex(float[] coefficients)
    {
        // Compact hex representation for comparison: first 8 coefficients only
        if (coefficients.Length == 0) return "(empty)";
        var count = Math.Min(8, coefficients.Length);
        var nonZero = 0;
        for (var i = 0; i < coefficients.Length; i++)
            if (Math.Abs(coefficients[i]) > 0.001f) nonZero++;
        return $"{nonZero}/{coefficients.Length} non-zero";
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
