using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class CsvActorWriter
{
    public static string GenerateNpcsCsv(List<NpcRecord> npcs, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Gender,Level,SPECIAL_ST,SPECIAL_PE,SPECIAL_EN,SPECIAL_CH,SPECIAL_IN,SPECIAL_AG,SPECIAL_LK,Barter,EnergyWeapons,Explosives,Guns,Lockpick,Medicine,MeleeWeapons,Repair,Science,Sneak,Speech,Survival,Unarmed,BaseHealth,CalcHealth,CalcFatigue,CritChance,MeleeDmg,UnarmedDmg,PoisonResist,RadResist,Aggression,Confidence,Mood,EnergyLevel,Responsibility,Assistance,FatigueBase,BarterGold,SpeedMult,Karma,Disposition,CalcMin,CalcMax,Flags,RaceFormID,RaceName,RaceDisplayName,ClassFormID,ClassName,ClassDisplayName,ScriptFormID,VoiceTypeFormID,TemplateFormID,HairFormID,HairName,HairDisplayName,HairLength,EyesFormID,EyesName,EyesDisplayName,CombatStyleFormID,CombatStyleName,CombatStyleDisplayName,HasFaceGen,Endianness,Offset,SubFormID,SubName,SubDisplayName,SubDetail");

        foreach (var npc in npcs.OrderBy(n => n.EditorId ?? ""))
        {
            var s = npc.Stats;
            var sp = npc.SpecialStats;
            var sk = npc.Skills;
            var ai = npc.AiData;
            // Gender is bit 0 of ACBS flags: 0 = Male, 1 = Female
            var gender = "";
            if (s != null)
            {
                gender = (s.Flags & 1) == 1 ? "Female" : "Male";
            }

            // Derived stats (computed from SPECIAL + Level + Fatigue)
            var hasDerived = sp is { Length: 7 } && s != null;
            var baseHealth = hasDerived ? (sp![2] * 5 + 50).ToString() : "";
            var calcHealth = hasDerived ? (sp![2] * 5 + 50 + s!.Level * 10).ToString() : "";
            var calcFatigue = hasDerived ? (s!.FatigueBase + (sp![0] + sp[2]) * 10).ToString() : "";
            var critChance = sp is { Length: 7 } ? sp[6].ToString("F0") : "";
            var meleeDmg = sp is { Length: 7 } ? (sp[0] * 0.5f).ToString("F2") : "";
            var unarmedDmg = sp is { Length: 7 } ? (0.5f + sp[0] * 0.1f).ToString("F2") : "";
            var poisonResist = sp is { Length: 7 } ? ((sp[2] - 1) * 5f).ToString("F2") : "";
            var radResist = sp is { Length: 7 } ? ((sp[2] - 1) * 2f).ToString("F2") : "";

            sb.AppendLine(string.Join(",",
                "NPC",
                Fmt.FId(npc.FormId),
                Fmt.CsvEscape(npc.EditorId),
                Fmt.CsvEscape(npc.FullName),
                gender,
                s?.Level.ToString() ?? "",
                sp is { Length: 7 } ? sp[0].ToString() : "",
                sp is { Length: 7 } ? sp[1].ToString() : "",
                sp is { Length: 7 } ? sp[2].ToString() : "",
                sp is { Length: 7 } ? sp[3].ToString() : "",
                sp is { Length: 7 } ? sp[4].ToString() : "",
                sp is { Length: 7 } ? sp[5].ToString() : "",
                sp is { Length: 7 } ? sp[6].ToString() : "",
                // Skills (13 columns, skip BigGuns index 1)
                sk is { Length: 14 } ? sk[0].ToString() : "", // Barter
                sk is { Length: 14 } ? sk[2].ToString() : "", // EnergyWeapons
                sk is { Length: 14 } ? sk[3].ToString() : "", // Explosives
                sk is { Length: 14 } ? sk[9].ToString() : "", // Guns
                sk is { Length: 14 } ? sk[4].ToString() : "", // Lockpick
                sk is { Length: 14 } ? sk[5].ToString() : "", // Medicine
                sk is { Length: 14 } ? sk[6].ToString() : "", // MeleeWeapons
                sk is { Length: 14 } ? sk[7].ToString() : "", // Repair
                sk is { Length: 14 } ? sk[8].ToString() : "", // Science
                sk is { Length: 14 } ? sk[10].ToString() : "", // Sneak
                sk is { Length: 14 } ? sk[11].ToString() : "", // Speech
                sk is { Length: 14 } ? sk[12].ToString() : "", // Survival
                sk is { Length: 14 } ? sk[13].ToString() : "", // Unarmed
                // Derived stats
                baseHealth, calcHealth, calcFatigue,
                critChance, meleeDmg, unarmedDmg, poisonResist, radResist,
                // AI Data (with Mood)
                ai?.Aggression.ToString() ?? "",
                ai?.Confidence.ToString() ?? "",
                ai?.Mood.ToString() ?? "",
                ai?.EnergyLevel.ToString() ?? "",
                ai?.Responsibility.ToString() ?? "",
                ai?.Assistance.ToString() ?? "",
                s?.FatigueBase.ToString() ?? "",
                s?.BarterGold.ToString() ?? "",
                s?.SpeedMultiplier.ToString() ?? "",
                s?.KarmaAlignment.ToString() ?? "",
                s?.DispositionBase.ToString() ?? "",
                s?.CalcMin.ToString() ?? "",
                s?.CalcMax.ToString() ?? "",
                s?.Flags.ToString() ?? "",
                Fmt.FIdN(npc.Race),
                resolver.ResolveCsv(npc.Race ?? 0),
                resolver.ResolveDisplayNameCsv(npc.Race ?? 0),
                Fmt.FIdN(npc.Class),
                resolver.ResolveCsv(npc.Class ?? 0),
                resolver.ResolveDisplayNameCsv(npc.Class ?? 0),
                Fmt.FIdN(npc.Script),
                Fmt.FIdN(npc.VoiceType),
                Fmt.FIdN(npc.Template),
                Fmt.FIdN(npc.HairFormId),
                resolver.ResolveCsv(npc.HairFormId ?? 0),
                resolver.ResolveDisplayNameCsv(npc.HairFormId ?? 0),
                npc.HairLength?.ToString("F2") ?? "",
                Fmt.FIdN(npc.EyesFormId),
                resolver.ResolveCsv(npc.EyesFormId ?? 0),
                resolver.ResolveDisplayNameCsv(npc.EyesFormId ?? 0),
                Fmt.FIdN(npc.CombatStyleFormId),
                resolver.ResolveCsv(npc.CombatStyleFormId ?? 0),
                resolver.ResolveDisplayNameCsv(npc.CombatStyleFormId ?? 0),
                npc.FaceGenGeometrySymmetric != null ? "Yes" : "",
                Fmt.Endian(npc.IsBigEndian),
                npc.Offset.ToString(),
                "", "", "", ""));

            // Sub-row padding: 68 empty columns between FormID (col 2) and SubFormID (col 71)
            // Total header columns: 75 (RowType + FormID + 68 data cols + SubFormID + SubName + SubDisplayName + SubDetail)
            var subPad = new string(',', 68); // 68 empty columns
            foreach (var f in npc.Factions)
            {
                sb.AppendLine(
                    $"FACTION,{Fmt.FId(npc.FormId)}{subPad},{Fmt.FId(f.FactionFormId)},{resolver.ResolveCsv(f.FactionFormId)},{resolver.ResolveDisplayNameCsv(f.FactionFormId)},{f.Rank}");
            }

            foreach (var spellId in npc.Spells)
            {
                sb.AppendLine($"SPELL,{Fmt.FId(npc.FormId)}{subPad},{Fmt.FId(spellId)},{resolver.ResolveCsv(spellId)},{resolver.ResolveDisplayNameCsv(spellId)},");
            }

            foreach (var item in npc.Inventory)
            {
                sb.AppendLine(
                    $"INVENTORY,{Fmt.FId(npc.FormId)}{subPad},{Fmt.FId(item.ItemFormId)},{resolver.ResolveCsv(item.ItemFormId)},{resolver.ResolveDisplayNameCsv(item.ItemFormId)},{item.Count}");
            }

            foreach (var pkgId in npc.Packages)
            {
                sb.AppendLine($"PACKAGE,{Fmt.FId(npc.FormId)}{subPad},{Fmt.FId(pkgId)},{resolver.ResolveCsv(pkgId)},{resolver.ResolveDisplayNameCsv(pkgId)},");
            }
        }

        return sb.ToString();
    }

    public static string GenerateCreaturesCsv(List<CreatureRecord> creatures, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,CreatureType,CreatureTypeName,Level,FatigueBase,AttackDamage,CombatSkill,MagicSkill,StealthSkill,ScriptFormID,ModelPath,Endianness,Offset,SubFormID,SubName,SubDisplayName,SubDetail");

        foreach (var c in creatures.OrderBy(c => c.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "CREATURE",
                Fmt.FId(c.FormId),
                Fmt.CsvEscape(c.EditorId),
                Fmt.CsvEscape(c.FullName),
                c.CreatureType.ToString(),
                Fmt.CsvEscape(c.CreatureTypeName),
                c.Stats?.Level.ToString() ?? "",
                c.Stats?.FatigueBase.ToString() ?? "",
                c.AttackDamage.ToString(),
                c.CombatSkill.ToString(),
                c.MagicSkill.ToString(),
                c.StealthSkill.ToString(),
                Fmt.FIdN(c.Script),
                Fmt.CsvEscape(c.ModelPath),
                Fmt.Endian(c.IsBigEndian),
                c.Offset.ToString(),
                "", "", "", ""));

            foreach (var f in c.Factions)
            {
                sb.AppendLine(string.Join(",",
                    "FACTION",
                    Fmt.FId(c.FormId),
                    "", "", "", "", "", "", "", "", "", "", "", "",
                    "", "",
                    Fmt.FId(f.FactionFormId),
                    resolver.ResolveCsv(f.FactionFormId),
                    resolver.ResolveDisplayNameCsv(f.FactionFormId),
                    f.Rank.ToString()));
            }

            foreach (var spellId in c.Spells)
            {
                sb.AppendLine(string.Join(",",
                    "SPELL",
                    Fmt.FId(c.FormId),
                    "", "", "", "", "", "", "", "", "", "", "", "",
                    "", "",
                    Fmt.FId(spellId),
                    resolver.ResolveCsv(spellId),
                    resolver.ResolveDisplayNameCsv(spellId),
                    ""));
            }
        }

        return sb.ToString();
    }

    public static string GenerateRacesCsv(List<RaceRecord> races, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Description,SkillBoosts,MaleHeight,FemaleHeight,MaleVoiceFormID,FemaleVoiceFormID,OlderRaceFormID,YoungerRaceFormID,Endianness,Offset,AbilityFormID,AbilityName,AbilityDisplayName");

        foreach (var r in races.OrderBy(r => r.EditorId ?? ""))
        {
            var boosts = r.SkillBoosts.Count > 0
                ? string.Join("; ", r.SkillBoosts.Select(b => $"AV{b.SkillIndex}:{b.Boost:+#;-#;0}"))
                : "";
            sb.AppendLine(string.Join(",",
                "RACE",
                Fmt.FId(r.FormId),
                Fmt.CsvEscape(r.EditorId),
                Fmt.CsvEscape(r.FullName),
                Fmt.CsvEscape(r.Description),
                Fmt.CsvEscape(boosts),
                r.MaleHeight.ToString("F4"),
                r.FemaleHeight.ToString("F4"),
                Fmt.FIdN(r.MaleVoiceFormId),
                Fmt.FIdN(r.FemaleVoiceFormId),
                Fmt.FIdN(r.OlderRaceFormId),
                Fmt.FIdN(r.YoungerRaceFormId),
                Fmt.Endian(r.IsBigEndian),
                r.Offset.ToString(),
                "", "", ""));

            foreach (var abilityId in r.AbilityFormIds)
            {
                sb.AppendLine(string.Join(",",
                    "ABILITY",
                    Fmt.FId(r.FormId),
                    "", "", "", "", "", "", "", "", "", "",
                    "", "",
                    Fmt.FId(abilityId),
                    resolver.ResolveCsv(abilityId),
                    resolver.ResolveDisplayNameCsv(abilityId)));
            }
        }

        return sb.ToString();
    }

    public static string GenerateFactionsCsv(List<FactionRecord> factions, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Flags,IsHidden,AllowsEvil,AllowsSpecialCombat,Endianness,Offset,SubFormID,SubName,SubDisplayName,SubDetail");

        foreach (var f in factions.OrderBy(f => f.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "FACTION",
                Fmt.FId(f.FormId),
                Fmt.CsvEscape(f.EditorId),
                Fmt.CsvEscape(f.FullName),
                f.Flags.ToString(),
                f.IsHiddenFromPlayer.ToString(),
                f.AllowsEvil.ToString(),
                f.AllowsSpecialCombat.ToString(),
                Fmt.Endian(f.IsBigEndian),
                f.Offset.ToString(),
                "", "", "", ""));

            foreach (var rank in f.Ranks)
            {
                sb.AppendLine(string.Join(",",
                    "RANK",
                    Fmt.FId(f.FormId),
                    "", "", "", "", "", "",
                    "", "",
                    rank.RankNumber.ToString(),
                    Fmt.CsvEscape(rank.MaleTitle ?? rank.FemaleTitle ?? ""),
                    "",
                    ""));
            }

            foreach (var rel in f.Relations)
            {
                sb.AppendLine(string.Join(",",
                    "RELATION",
                    Fmt.FId(f.FormId),
                    "", "", "", "", "", "",
                    "", "",
                    Fmt.FId(rel.FactionFormId),
                    resolver.ResolveCsv(rel.FactionFormId),
                    resolver.ResolveDisplayNameCsv(rel.FactionFormId),
                    rel.Modifier.ToString()));
            }
        }

        return sb.ToString();
    }

    public static string GenerateClassesCsv(List<ClassRecord> classes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,TagSkills,Playable,Guard,TrainingSkill,TrainingLevel,Endianness,Offset");

        foreach (var c in classes.OrderBy(c => c.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "CLAS",
                Fmt.FId(c.FormId),
                Fmt.CsvEscape(c.EditorId),
                Fmt.CsvEscape(c.FullName),
                Fmt.CsvEscape(string.Join(";", c.TagSkills)),
                c.IsPlayable ? "Yes" : "No",
                c.IsGuard ? "Yes" : "No",
                c.TrainingSkill.ToString(),
                c.TrainingLevel.ToString(),
                Fmt.Endian(c.IsBigEndian),
                c.Offset.ToString()));
        }

        return sb.ToString();
    }
}
