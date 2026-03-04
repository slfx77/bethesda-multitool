using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Detailed NPC stat/faction/inventory formatting, FaceGen morph data,
///     and per-NPC report generation. Extracted from GeckActorWriter.
/// </summary>
internal static class GeckActorDetailWriter
{
    internal static void AppendNpcReportEntry(
        StringBuilder sb,
        NpcRecord npc,
        FormIdResolver resolver,
        RaceRecord? race = null)
    {
        sb.AppendLine();

        // Header with both EditorID and display name
        var title = !string.IsNullOrEmpty(npc.FullName)
            ? $"NPC: {npc.EditorId ?? "(unknown)"} \u2014 {npc.FullName}"
            : $"NPC: {npc.EditorId ?? "(unknown)"}";
        sb.AppendLine(new string(GeckReportHelpers.SeparatorChar, GeckReportHelpers.SeparatorWidth));
        var padding = (GeckReportHelpers.SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(GeckReportHelpers.SeparatorChar, GeckReportHelpers.SeparatorWidth));

        // Basic info
        sb.AppendLine($"  FormID:         {GeckReportHelpers.FormatFormId(npc.FormId)}");
        sb.AppendLine($"  Editor ID:      {npc.EditorId ?? "(none)"}");
        sb.AppendLine($"  Display Name:   {npc.FullName ?? "(none)"}");

        if (npc.Stats != null)
        {
            var gender = (npc.Stats.Flags & 1) == 1 ? "Female" : "Male";
            sb.AppendLine($"  Gender:         {gender}");
        }

        // Stats
        if (npc.Stats != null || npc.SpecialStats != null)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Stats {new string('\u2500', 73)}");

            if (npc.Stats != null)
            {
                sb.AppendLine($"  Level:          {npc.Stats.Level}");
            }

            if (npc.SpecialStats is { Length: 7 })
            {
                var s = npc.SpecialStats;
                var total = s[0] + s[1] + s[2] + s[3] + s[4] + s[5] + s[6];
                sb.AppendLine(
                    $"  S.P.E.C.I.A.L.: {s[0]} ST, {s[1]} PE, {s[2]} EN, {s[3]} CH, {s[4]} IN, {s[5]} AG, {s[6]} LK  (Total: {total})");
            }

            // Skills (14 bytes, skip BigGuns index 1 for FNV)
            if (npc.Skills is { Length: 14 })
            {
                var sk = npc.Skills;
                // Use AVIF-sourced names when available, resolver provides hardcoded fallback
                string Sk(int i) => resolver.GetSkillName(i) ?? $"Skill#{i}";
                sb.AppendLine("  Skills:");
                sb.AppendLine(
                    $"    {Sk(0),-18}{sk[0],3}    {Sk(2),-18}{sk[2],3}    {Sk(3),-18}{sk[3],3}");
                sb.AppendLine($"    {Sk(9),-18}{sk[9],3}    {Sk(4),-18}{sk[4],3}    {Sk(5),-18}{sk[5],3}");
                sb.AppendLine(
                    $"    {Sk(6),-18}{sk[6],3}    {Sk(7),-18}{sk[7],3}    {Sk(8),-18}{sk[8],3}");
                sb.AppendLine($"    {Sk(10),-18}{sk[10],3}    {Sk(11),-18}{sk[11],3}    {Sk(12),-18}{sk[12],3}");
                sb.AppendLine($"    {Sk(13),-18}{sk[13],3}");
            }
        }

        // Derived Stats (computed from SPECIAL + Level + Fatigue, plus ACBS stats)
        if (npc.Stats != null)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Derived Stats {new string('\u2500', 65)}");

            if (npc.SpecialStats is { Length: 7 } sp2)
            {
                var str = sp2[0];
                var end = sp2[2];
                var lck = sp2[6];
                var baseHealth = end * 5 + 50;
                var calcHealth = baseHealth + npc.Stats.Level * 10;
                var calcFatigue = npc.Stats.FatigueBase + (str + end) * 10;
                var critChance = (float)lck;
                var meleeDamage = str * 0.5f;
                var unarmedDamage = 0.5f + str * 0.1f;
                var poisonResist = (end - 1) * 5f;
                var radResist = (end - 1) * 2f;

                sb.AppendLine($"  {"Base Health:",-18}{baseHealth,-10}{"Calculated Health:",-22}{calcHealth}");
                sb.AppendLine($"  {"Fatigue:",-18}{npc.Stats.FatigueBase,-10}{"Calc Fatigue:",-22}{calcFatigue}");
                sb.AppendLine(
                    $"  {"Critical Chance:",-18}{critChance,-10:F0}{"Speed Mult:",-22}{npc.Stats.SpeedMultiplier}%");
                sb.AppendLine($"  {"Melee Damage:",-18}{meleeDamage,-10:F2}{"Unarmed Damage:",-22}{unarmedDamage:F2}");
                sb.AppendLine($"  {"Poison Resist:",-18}{poisonResist,-10:F2}{"Rad Resist:",-22}{radResist:F2}");
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
        if (npc.Race.HasValue || npc.Class.HasValue || npc.CombatStyleFormId.HasValue || npc.Factions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Combat {new string('\u2500', 72)}");

            if (npc.Race.HasValue)
            {
                sb.AppendLine(
                    $"  Race:           {resolver.FormatFull(npc.Race.Value)}");
            }

            if (npc.Class.HasValue)
            {
                sb.AppendLine(
                    $"  Class:          {resolver.FormatFull(npc.Class.Value)}");
            }

            if (npc.CombatStyleFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Combat Style:   {resolver.FormatFull(npc.CombatStyleFormId.Value)}");
            }
        }

        // Physical Traits
        if (npc.HairFormId.HasValue || npc.EyesFormId.HasValue || npc.HairLength.HasValue ||
            npc.HairColor.HasValue || npc.Height.HasValue || npc.Weight.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 Physical Traits {new string('\u2500', 63)}");

            if (npc.HairFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Hairstyle:      {resolver.FormatFull(npc.HairFormId.Value)}");
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
                sb.AppendLine(
                    $"  Eyes:           {resolver.FormatFull(npc.EyesFormId.Value)}");
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
            sb.AppendLine($"  \u2500\u2500 AI Data {new string('\u2500', 71)}");
            sb.AppendLine($"  Aggression:     {npc.AiData.AggressionName} ({npc.AiData.Aggression})");
            sb.AppendLine($"  Confidence:     {npc.AiData.ConfidenceName} ({npc.AiData.Confidence})");
            sb.AppendLine($"  Mood:           {npc.AiData.MoodName} ({npc.AiData.Mood})");
            sb.AppendLine($"  Assistance:     {npc.AiData.AssistanceName} ({npc.AiData.Assistance})");
            sb.AppendLine($"  Energy Level:   {npc.AiData.EnergyLevel}");
            sb.AppendLine($"  Responsibility: {npc.AiData.ResponsibilityName} ({npc.AiData.Responsibility})");
        }

        // References
        if (npc.Script.HasValue || npc.VoiceType.HasValue || npc.Template.HasValue ||
            npc.OriginalRace.HasValue || npc.FaceNpc.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 References {new string('\u2500', 68)}");

            if (npc.Script.HasValue)
            {
                sb.AppendLine($"  Script:         {resolver.FormatWithEditorId(npc.Script.Value)}");
            }

            if (npc.VoiceType.HasValue)
            {
                sb.AppendLine($"  Voice Type:     {resolver.FormatWithEditorId(npc.VoiceType.Value)}");
            }

            if (npc.Template.HasValue)
            {
                sb.AppendLine(
                    $"  Template:       {resolver.FormatFull(npc.Template.Value)}");
            }

            if (npc.OriginalRace.HasValue)
            {
                sb.AppendLine(
                    $"  Original Race:  {resolver.FormatFull(npc.OriginalRace.Value)}");
            }

            if (npc.FaceNpc.HasValue)
            {
                sb.AppendLine(
                    $"  Face NPC:       {resolver.FormatFull(npc.FaceNpc.Value)}");
            }
        }

        // Factions table
        if (npc.Factions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Factions ({npc.Factions.Count}):");
            sb.AppendLine($"    {"EditorID",-32} {"Name",-32} {"Rank",4}");
            sb.AppendLine($"    {new string('\u2500', 32)} {new string('\u2500', 32)} {new string('\u2500', 4)}");

            foreach (var faction in npc.Factions)
            {
                var editorId = resolver.ResolveEditorId(faction.FactionFormId);
                var displayName = resolver.ResolveDisplayName(faction.FactionFormId);
                sb.AppendLine(
                    $"    {GeckReportHelpers.Truncate(editorId, 32),-32} {GeckReportHelpers.Truncate(displayName, 32),-32} {faction.Rank,4}");
            }
        }

        // Inventory table
        if (npc.Inventory.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Inventory ({npc.Inventory.Count}):");
            sb.AppendLine($"    {"EditorID",-32} {"Name",-32} {"Qty",5}");
            sb.AppendLine($"    {new string('\u2500', 32)} {new string('\u2500', 32)} {new string('\u2500', 5)}");

            foreach (var item in npc.Inventory)
            {
                var editorId = resolver.ResolveEditorId(item.ItemFormId);
                var displayName = resolver.ResolveDisplayName(item.ItemFormId);
                sb.AppendLine(
                    $"    {GeckReportHelpers.Truncate(editorId, 32),-32} {GeckReportHelpers.Truncate(displayName, 32),-32} {item.Count,5}");
            }
        }

        // Spells table
        if (npc.Spells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Spells/Abilities ({npc.Spells.Count}):");
            sb.AppendLine($"    {"EditorID",-32} {"Name",-32}");
            sb.AppendLine($"    {new string('\u2500', 32)} {new string('\u2500', 32)}");

            foreach (var spellId in npc.Spells)
            {
                var editorId = resolver.ResolveEditorId(spellId);
                var displayName = resolver.ResolveDisplayName(spellId);
                sb.AppendLine(
                    $"    {GeckReportHelpers.Truncate(editorId, 32),-32} {GeckReportHelpers.Truncate(displayName, 32),-32}");
            }
        }

        // AI Packages table
        if (npc.Packages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  AI Packages ({npc.Packages.Count}):");
            sb.AppendLine($"    {"EditorID",-32} {"Name",-32}");
            sb.AppendLine($"    {new string('\u2500', 32)} {new string('\u2500', 32)}");

            foreach (var pkgId in npc.Packages)
            {
                var editorId = resolver.ResolveEditorId(pkgId);
                var displayName = resolver.ResolveDisplayName(pkgId);
                sb.AppendLine(
                    $"    {GeckReportHelpers.Truncate(editorId, 32),-32} {GeckReportHelpers.Truncate(displayName, 32),-32}");
            }
        }

        // FaceGen Morph Data
        var hasFaceGen = npc.FaceGenGeometrySymmetric != null ||
                         npc.FaceGenGeometryAsymmetric != null ||
                         npc.FaceGenTextureSymmetric != null;
        if (hasFaceGen)
        {
            sb.AppendLine();
            sb.AppendLine($"  \u2500\u2500 FaceGen Morph Data {new string('\u2500', 60)}");

            // NPC face data is an offset from the race's base face.
            // Merge race base + NPC offset before projecting onto control vectors.
            var isFemale = npc.Stats != null && (npc.Stats.Flags & 1) == 1;
            var raceFggs = isFemale ? race?.FemaleFaceGenGeometrySymmetric : race?.MaleFaceGenGeometrySymmetric;
            var raceFgga = isFemale ? race?.FemaleFaceGenGeometryAsymmetric : race?.MaleFaceGenGeometryAsymmetric;
            var raceFgts = isFemale ? race?.FemaleFaceGenTextureSymmetric : race?.MaleFaceGenTextureSymmetric;

            AppendFaceGenControlSection(sb, "Geometry-Symmetric",
                npc.FaceGenGeometrySymmetric,
                fggs => FaceGenControls.ComputeGeometrySymmetric(fggs, raceFggs));
            AppendFaceGenRawHex(sb, "FGGS", npc.FaceGenGeometrySymmetric);

            AppendFaceGenControlSection(sb, "Geometry-Asymmetric",
                npc.FaceGenGeometryAsymmetric,
                fgga => FaceGenControls.ComputeGeometryAsymmetric(fgga, raceFgga));
            AppendFaceGenRawHex(sb, "FGGA", npc.FaceGenGeometryAsymmetric);

            AppendFaceGenControlSection(sb, "Texture-Symmetric",
                npc.FaceGenTextureSymmetric,
                fgts => FaceGenControls.ComputeTextureSymmetric(fgts, raceFgts));
            AppendFaceGenRawHex(sb, "FGTS", npc.FaceGenTextureSymmetric);
        }
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

    /// <summary>
    ///     Append a FaceGen control section using CTL-based projections.
    ///     Computes named slider values by projecting basis coefficients (FGGS/FGGA/FGTS)
    ///     through the si.ctl linear control direction vectors.
    ///     Controls are sorted alphabetically and grouped by facial region.
    /// </summary>
    internal static void AppendFaceGenControlSection(
        StringBuilder sb,
        string sectionLabel,
        float[]? basisValues,
        Func<float[], (string Name, float Value)[]> computeControls)
    {
        if (basisValues == null || basisValues.Length == 0)
        {
            return;
        }

        // Check if all basis values are zero
        var basisActive = 0;
        foreach (var v in basisValues)
        {
            if (Math.Abs(v) > 0.0001f)
            {
                basisActive++;
            }
        }

        if (basisActive == 0)
        {
            sb.AppendLine($"  {sectionLabel} ({basisValues.Length} basis values): all zero");
            return;
        }

        // Compute named control projections
        var controls = computeControls(basisValues);
        var activeControls = controls.Where(c => Math.Abs(c.Value) > 0.01f).ToList();
        activeControls.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        sb.AppendLine($"  {sectionLabel} ({controls.Length} controls, {activeControls.Count} active):");

        if (activeControls.Count == 0)
        {
            sb.AppendLine("    (all controls near zero)");
            return;
        }

        foreach (var (name, value) in activeControls)
        {
            sb.AppendLine($"    {name,-45} {value,+8:F4}");
        }
    }

    /// <summary>
    ///     Append raw little-endian hex bytes for a FaceGen float array.
    ///     Each float is converted to its IEEE 754 little-endian 4-byte representation
    ///     (PC-compatible format for GECK import/ESM editing).
    ///     This allows exact reproduction without floating-point rounding.
    /// </summary>
    internal static void AppendFaceGenRawHex(StringBuilder sb, string label, float[]? values)
    {
        if (values == null || values.Length == 0)
        {
            return;
        }

        // Check if all zero - skip hex if so
        var allZero = true;
        foreach (var v in values)
        {
            if (Math.Abs(v) > 0.0001f)
            {
                allZero = false;
                break;
            }
        }

        if (allZero)
        {
            return;
        }

        sb.AppendLine($"  {label} Raw Hex ({values.Length * 4} bytes, little-endian / PC):");

        // Convert each float to little-endian bytes and format as hex
        var hexLine = new StringBuilder("    ");
        for (var i = 0; i < values.Length; i++)
        {
            var bytes = BitConverter.GetBytes(values[i]);
            // BitConverter gives native endian (LE on x86); reverse if running on BE
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            hexLine.Append($"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}");

            if (i < values.Length - 1)
            {
                hexLine.Append(' ');
            }

            // Line break every 10 floats (40 bytes) for readability
            if ((i + 1) % 10 == 0 && i < values.Length - 1)
            {
                sb.AppendLine(hexLine.ToString().TrimEnd());
                hexLine.Clear();
                hexLine.Append("    ");
            }
        }

        if (hexLine.Length > 4)
        {
            sb.AppendLine(hexLine.ToString().TrimEnd());
        }
    }
}
