using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class CsvActorWriter
{
    public static string GenerateNpcsCsv(List<NpcRecord> npcs, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Gender,Level,SPECIAL_ST,SPECIAL_PE,SPECIAL_EN,SPECIAL_CH,SPECIAL_IN,SPECIAL_AG,SPECIAL_LK,Barter,EnergyWeapons,Explosives,Guns,Lockpick,Medicine,MeleeWeapons,Repair,Science,Sneak,Speech,Survival,Unarmed,BaseHealth,CalcHealth,CalcFatigue,CritChance,MeleeDmg,UnarmedDmg,PoisonResist,RadResist,Aggression,Confidence,Mood,EnergyLevel,Responsibility,Assistance,FatigueBase,BarterGold,SpeedMult,Karma,Disposition,CalcMin,CalcMax,Flags,RaceFormID,RaceName,ClassFormID,ClassName,ScriptFormID,VoiceTypeFormID,TemplateFormID,HairFormID,HairName,HairLength,EyesFormID,EyesName,CombatStyleFormID,CombatStyleName,HasFaceGen,Endianness,Offset,SubFormID,SubName,SubDetail");

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
                Fmt.Resolve(npc.Race ?? 0, lookup),
                Fmt.FIdN(npc.Class),
                Fmt.Resolve(npc.Class ?? 0, lookup),
                Fmt.FIdN(npc.Script),
                Fmt.FIdN(npc.VoiceType),
                Fmt.FIdN(npc.Template),
                Fmt.FIdN(npc.HairFormId),
                Fmt.Resolve(npc.HairFormId ?? 0, lookup),
                npc.HairLength?.ToString("F2") ?? "",
                Fmt.FIdN(npc.EyesFormId),
                Fmt.Resolve(npc.EyesFormId ?? 0, lookup),
                Fmt.FIdN(npc.CombatStyleFormId),
                Fmt.Resolve(npc.CombatStyleFormId ?? 0, lookup),
                npc.FaceGenGeometrySymmetric != null ? "Yes" : "",
                Fmt.Endian(npc.IsBigEndian),
                npc.Offset.ToString(),
                "", "", ""));

            // Sub-row padding: 63 empty columns between FormID (col 2) and SubFormID (col 66)
            // Total header columns: 68 (RowType + FormID + 63 data cols + SubFormID + SubName + SubDetail)
            var subPad = new string(',', 63); // 63 empty columns
            foreach (var f in npc.Factions)
            {
                sb.AppendLine(
                    $"FACTION,{Fmt.FId(npc.FormId)}{subPad},{Fmt.FId(f.FactionFormId)},{Fmt.Resolve(f.FactionFormId, lookup)},{f.Rank}");
            }

            foreach (var spellId in npc.Spells)
            {
                sb.AppendLine($"SPELL,{Fmt.FId(npc.FormId)}{subPad},{Fmt.FId(spellId)},{Fmt.Resolve(spellId, lookup)},");
            }

            foreach (var item in npc.Inventory)
            {
                sb.AppendLine(
                    $"INVENTORY,{Fmt.FId(npc.FormId)}{subPad},{Fmt.FId(item.ItemFormId)},{Fmt.Resolve(item.ItemFormId, lookup)},{item.Count}");
            }

            foreach (var pkgId in npc.Packages)
            {
                sb.AppendLine($"PACKAGE,{Fmt.FId(npc.FormId)}{subPad},{Fmt.FId(pkgId)},{Fmt.Resolve(pkgId, lookup)},");
            }
        }

        return sb.ToString();
    }

    public static string GenerateCreaturesCsv(List<CreatureRecord> creatures, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,CreatureType,CreatureTypeName,Level,FatigueBase,AttackDamage,CombatSkill,MagicSkill,StealthSkill,ScriptFormID,ModelPath,Endianness,Offset,SubFormID,SubName,SubDetail");

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
                "", "", ""));

            foreach (var f in c.Factions)
            {
                sb.AppendLine(string.Join(",",
                    "FACTION",
                    Fmt.FId(c.FormId),
                    "", "", "", "", "", "", "", "", "", "", "", "",
                    "", "",
                    Fmt.FId(f.FactionFormId),
                    Fmt.Resolve(f.FactionFormId, lookup),
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
                    Fmt.Resolve(spellId, lookup),
                    ""));
            }
        }

        return sb.ToString();
    }

    public static string GenerateRacesCsv(List<RaceRecord> races, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Description,SkillBoosts,MaleHeight,FemaleHeight,MaleVoiceFormID,FemaleVoiceFormID,OlderRaceFormID,YoungerRaceFormID,Endianness,Offset,AbilityFormID,AbilityName");

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
                "", ""));

            foreach (var abilityId in r.AbilityFormIds)
            {
                sb.AppendLine(string.Join(",",
                    "ABILITY",
                    Fmt.FId(r.FormId),
                    "", "", "", "", "", "", "", "", "", "",
                    "", "",
                    Fmt.FId(abilityId),
                    Fmt.Resolve(abilityId, lookup)));
            }
        }

        return sb.ToString();
    }

    public static string GenerateFactionsCsv(List<FactionRecord> factions, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Flags,IsHidden,AllowsEvil,AllowsSpecialCombat,Endianness,Offset,SubFormID,SubName,SubDetail");

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
                "", "", ""));

            foreach (var rank in f.Ranks)
            {
                sb.AppendLine(string.Join(",",
                    "RANK",
                    Fmt.FId(f.FormId),
                    "", "", "", "", "", "",
                    "", "",
                    rank.RankNumber.ToString(),
                    Fmt.CsvEscape(rank.MaleTitle ?? rank.FemaleTitle ?? ""),
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
                    Fmt.Resolve(rel.FactionFormId, lookup),
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
