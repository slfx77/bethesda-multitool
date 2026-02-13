using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Generates GECK-style text reports for NPC, Creature, and Race records.</summary>
internal static class GeckActorWriter
{
    internal static void AppendNpcsSection(StringBuilder sb, List<NpcRecord> npcs,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"NPCs ({npcs.Count})");

        foreach (var npc in npcs.OrderBy(n => n.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "NPC", npc.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(npc.FormId)}");
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
        }
    }

    internal static void AppendNpcReportEntry(
        StringBuilder sb,
        NpcRecord npc,
        FormIdResolver resolver)
    {
        sb.AppendLine();

        // Header with both EditorID and display name
        var title = !string.IsNullOrEmpty(npc.FullName)
            ? $"NPC: {npc.EditorId ?? "(unknown)"} \u2014 {npc.FullName}"
            : $"NPC: {npc.EditorId ?? "(unknown)"}";
        sb.AppendLine(new string(GeckReportGenerator.SeparatorChar, GeckReportGenerator.SeparatorWidth));
        var padding = (GeckReportGenerator.SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(GeckReportGenerator.SeparatorChar, GeckReportGenerator.SeparatorWidth));

        // Basic info
        sb.AppendLine($"  FormID:         {GeckReportGenerator.FormatFormId(npc.FormId)}");
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
                sb.AppendLine("  Skills:");
                sb.AppendLine(
                    $"    {"Barter",-18}{sk[0],3}    {"Energy Weapons",-18}{sk[2],3}    {"Explosives",-18}{sk[3],3}");
                sb.AppendLine($"    {"Guns",-18}{sk[9],3}    {"Lockpick",-18}{sk[4],3}    {"Medicine",-18}{sk[5],3}");
                sb.AppendLine(
                    $"    {"Melee Weapons",-18}{sk[6],3}    {"Repair",-18}{sk[7],3}    {"Science",-18}{sk[8],3}");
                sb.AppendLine($"    {"Sneak",-18}{sk[10],3}    {"Speech",-18}{sk[11],3}    {"Survival",-18}{sk[12],3}");
                sb.AppendLine($"    {"Unarmed",-18}{sk[13],3}");
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

            sb.AppendLine($"  {"Karma:",-18}{npc.Stats.KarmaAlignment:F2}{GeckReportGenerator.FormatKarmaLabel(npc.Stats.KarmaAlignment)}");
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
        if (npc.HairFormId.HasValue || npc.EyesFormId.HasValue || npc.HairLength.HasValue)
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

            if (npc.EyesFormId.HasValue)
            {
                sb.AppendLine(
                    $"  Eyes:           {resolver.FormatFull(npc.EyesFormId.Value)}");
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
        if (npc.Script.HasValue || npc.VoiceType.HasValue || npc.Template.HasValue)
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
                sb.AppendLine($"    {GeckReportGenerator.Truncate(editorId, 32),-32} {GeckReportGenerator.Truncate(displayName, 32),-32} {faction.Rank,4}");
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
                sb.AppendLine($"    {GeckReportGenerator.Truncate(editorId, 32),-32} {GeckReportGenerator.Truncate(displayName, 32),-32} {item.Count,5}");
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
                sb.AppendLine($"    {GeckReportGenerator.Truncate(editorId, 32),-32} {GeckReportGenerator.Truncate(displayName, 32),-32}");
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
                sb.AppendLine($"    {GeckReportGenerator.Truncate(editorId, 32),-32} {GeckReportGenerator.Truncate(displayName, 32),-32}");
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

            AppendFaceGenControlSection(sb, "Geometry-Symmetric",
                npc.FaceGenGeometrySymmetric,
                FaceGenControls.ComputeGeometrySymmetric);
            AppendFaceGenRawHex(sb, "FGGS", npc.FaceGenGeometrySymmetric);

            AppendFaceGenControlSection(sb, "Geometry-Asymmetric",
                npc.FaceGenGeometryAsymmetric,
                FaceGenControls.ComputeGeometryAsymmetric);
            AppendFaceGenRawHex(sb, "FGGA", npc.FaceGenGeometryAsymmetric);

            AppendFaceGenControlSection(sb, "Texture-Symmetric",
                npc.FaceGenTextureSymmetric,
                FaceGenControls.ComputeTextureSymmetric);
            AppendFaceGenRawHex(sb, "FGTS", npc.FaceGenTextureSymmetric);
        }
    }

    /// <summary>
    ///     Generate a report for NPCs only.
    /// </summary>
    public static string GenerateNpcsReport(List<NpcRecord> npcs, FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendNpcsSection(sb, npcs, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a structured, human-readable per-NPC report with aligned tables
    ///     and display names for all referenced records (factions, inventory, spells, packages).
    /// </summary>
    public static string GenerateNpcReport(
        List<NpcRecord> npcs,
        FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        GeckReportGenerator.AppendHeader(sb, $"NPC Report ({npcs.Count:N0} NPCs)");
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

        foreach (var npc in npcs.OrderBy(n => n.EditorId ?? ""))
        {
            AppendNpcReportEntry(sb, npc, resolver);
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

    internal static void AppendCreaturesSection(StringBuilder sb, List<CreatureRecord> creatures)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Creatures ({creatures.Count})");

        foreach (var creature in creatures.OrderBy(c => c.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "CREA", creature.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(creature.FormId)}");
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
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendCreaturesSection(sb, creatures);
        return sb.ToString();
    }

    internal static void AppendRacesSection(StringBuilder sb, List<RaceRecord> races,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Races ({races.Count})");

        foreach (var race in races.OrderBy(r => r.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "RACE", race.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(race.FormId)}");
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
                string[] raceSkillNames =
                [
                    "Barter", "Big Guns", "Energy Weapons", "Explosives", "Lockpick", "Medicine",
                    "Melee Weapons", "Repair", "Science", "Guns", "Sneak", "Speech", "Survival", "Unarmed"
                ];
                sb.AppendLine();
                sb.AppendLine("Skill Boosts:");
                foreach (var (skillIndex, boost) in race.SkillBoosts)
                {
                    var skillName = skillIndex >= 32 && skillIndex <= 45
                        ? raceSkillNames[skillIndex - 32]
                        : $"AV#{skillIndex}";
                    sb.AppendLine($"  {skillName,-15} {GeckReportGenerator.FormatModifier(boost)}");
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
        GeckReportGenerator.AppendSectionHeader(sb, $"Classes ({classes.Count})");
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
            sb.AppendLine($"  FormID:      {GeckReportGenerator.FormatFormId(cls.FormId)}");
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
