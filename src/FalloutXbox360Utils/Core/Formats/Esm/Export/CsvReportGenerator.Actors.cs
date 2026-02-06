using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

public static partial class CsvReportGenerator
{
    public static string GenerateNpcsCsv(List<ReconstructedNpc> npcs, Dictionary<uint, string> lookup)
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
                FId(npc.FormId),
                E(npc.EditorId),
                E(npc.FullName),
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
                FIdN(npc.Race),
                Resolve(npc.Race ?? 0, lookup),
                FIdN(npc.Class),
                Resolve(npc.Class ?? 0, lookup),
                FIdN(npc.Script),
                FIdN(npc.VoiceType),
                FIdN(npc.Template),
                FIdN(npc.HairFormId),
                Resolve(npc.HairFormId ?? 0, lookup),
                npc.HairLength?.ToString("F2") ?? "",
                FIdN(npc.EyesFormId),
                Resolve(npc.EyesFormId ?? 0, lookup),
                FIdN(npc.CombatStyleFormId),
                Resolve(npc.CombatStyleFormId ?? 0, lookup),
                npc.FaceGenGeometrySymmetric != null ? "Yes" : "",
                Endian(npc.IsBigEndian),
                npc.Offset.ToString(),
                "", "", ""));

            // Sub-row padding: 63 empty columns between FormID (col 2) and SubFormID (col 66)
            // Total header columns: 68 (RowType + FormID + 63 data cols + SubFormID + SubName + SubDetail)
            var subPad = new string(',', 63); // 63 empty columns
            foreach (var f in npc.Factions)
            {
                sb.AppendLine(
                    $"FACTION,{FId(npc.FormId)}{subPad},{FId(f.FactionFormId)},{Resolve(f.FactionFormId, lookup)},{f.Rank}");
            }

            foreach (var spellId in npc.Spells)
            {
                sb.AppendLine($"SPELL,{FId(npc.FormId)}{subPad},{FId(spellId)},{Resolve(spellId, lookup)},");
            }

            foreach (var item in npc.Inventory)
            {
                sb.AppendLine(
                    $"INVENTORY,{FId(npc.FormId)}{subPad},{FId(item.ItemFormId)},{Resolve(item.ItemFormId, lookup)},{item.Count}");
            }

            foreach (var pkgId in npc.Packages)
            {
                sb.AppendLine($"PACKAGE,{FId(npc.FormId)}{subPad},{FId(pkgId)},{Resolve(pkgId, lookup)},");
            }
        }

        return sb.ToString();
    }

    public static string GenerateCreaturesCsv(List<ReconstructedCreature> creatures, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,CreatureType,CreatureTypeName,Level,FatigueBase,AttackDamage,CombatSkill,MagicSkill,StealthSkill,ScriptFormID,ModelPath,Endianness,Offset,SubFormID,SubName,SubDetail");

        foreach (var c in creatures.OrderBy(c => c.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "CREATURE",
                FId(c.FormId),
                E(c.EditorId),
                E(c.FullName),
                c.CreatureType.ToString(),
                E(c.CreatureTypeName),
                c.Stats?.Level.ToString() ?? "",
                c.Stats?.FatigueBase.ToString() ?? "",
                c.AttackDamage.ToString(),
                c.CombatSkill.ToString(),
                c.MagicSkill.ToString(),
                c.StealthSkill.ToString(),
                FIdN(c.Script),
                E(c.ModelPath),
                Endian(c.IsBigEndian),
                c.Offset.ToString(),
                "", "", ""));

            foreach (var f in c.Factions)
            {
                sb.AppendLine(string.Join(",",
                    "FACTION",
                    FId(c.FormId),
                    "", "", "", "", "", "", "", "", "", "", "", "",
                    "", "",
                    FId(f.FactionFormId),
                    Resolve(f.FactionFormId, lookup),
                    f.Rank.ToString()));
            }

            foreach (var spellId in c.Spells)
            {
                sb.AppendLine(string.Join(",",
                    "SPELL",
                    FId(c.FormId),
                    "", "", "", "", "", "", "", "", "", "", "", "",
                    "", "",
                    FId(spellId),
                    Resolve(spellId, lookup),
                    ""));
            }
        }

        return sb.ToString();
    }

    public static string GenerateRacesCsv(List<ReconstructedRace> races, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Description,Strength,Perception,Endurance,Charisma,Intelligence,Agility,Luck,MaleHeight,FemaleHeight,MaleVoiceFormID,FemaleVoiceFormID,OlderRaceFormID,YoungerRaceFormID,Endianness,Offset,AbilityFormID,AbilityName");

        foreach (var r in races.OrderBy(r => r.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "RACE",
                FId(r.FormId),
                E(r.EditorId),
                E(r.FullName),
                E(r.Description),
                r.Strength.ToString(),
                r.Perception.ToString(),
                r.Endurance.ToString(),
                r.Charisma.ToString(),
                r.Intelligence.ToString(),
                r.Agility.ToString(),
                r.Luck.ToString(),
                r.MaleHeight.ToString("F4"),
                r.FemaleHeight.ToString("F4"),
                FIdN(r.MaleVoiceFormId),
                FIdN(r.FemaleVoiceFormId),
                FIdN(r.OlderRaceFormId),
                FIdN(r.YoungerRaceFormId),
                Endian(r.IsBigEndian),
                r.Offset.ToString(),
                "", ""));

            foreach (var abilityId in r.AbilityFormIds)
            {
                sb.AppendLine(string.Join(",",
                    "ABILITY",
                    FId(r.FormId),
                    "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
                    "", "",
                    FId(abilityId),
                    Resolve(abilityId, lookup)));
            }
        }

        return sb.ToString();
    }

    public static string GenerateFactionsCsv(List<ReconstructedFaction> factions, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Flags,IsHidden,AllowsEvil,AllowsSpecialCombat,Endianness,Offset,SubFormID,SubName,SubDetail");

        foreach (var f in factions.OrderBy(f => f.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "FACTION",
                FId(f.FormId),
                E(f.EditorId),
                E(f.FullName),
                f.Flags.ToString(),
                f.IsHiddenFromPlayer.ToString(),
                f.AllowsEvil.ToString(),
                f.AllowsSpecialCombat.ToString(),
                Endian(f.IsBigEndian),
                f.Offset.ToString(),
                "", "", ""));

            for (var i = 0; i < f.RankNames.Count; i++)
            {
                sb.AppendLine(string.Join(",",
                    "RANK",
                    FId(f.FormId),
                    "", "", "", "", "", "",
                    "", "",
                    i.ToString(),
                    E(f.RankNames[i]),
                    ""));
            }

            foreach (var rel in f.Relations)
            {
                sb.AppendLine(string.Join(",",
                    "RELATION",
                    FId(f.FormId),
                    "", "", "", "", "", "",
                    "", "",
                    FId(rel.FactionFormId),
                    Resolve(rel.FactionFormId, lookup),
                    rel.Modifier.ToString()));
            }
        }

        return sb.ToString();
    }

    public static string GenerateClassesCsv(List<ReconstructedClass> classes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,TagSkills,Playable,Guard,TrainingSkill,TrainingLevel,Endianness,Offset");

        foreach (var c in classes.OrderBy(c => c.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "CLAS",
                FId(c.FormId),
                E(c.EditorId),
                E(c.FullName),
                E(string.Join(";", c.TagSkills)),
                c.IsPlayable ? "Yes" : "No",
                c.IsGuard ? "Yes" : "No",
                c.TrainingSkill.ToString(),
                c.TrainingLevel.ToString(),
                Endian(c.IsBigEndian),
                c.Offset.ToString()));
        }

        return sb.ToString();
    }
}
