using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Export;

/// <summary>
///     Generates CSV reports from reconstructed ESM data.
///     Uses a RowType column for records with nested sub-lists.
/// </summary>
public static partial class CsvReportGenerator
{
    private static string E(string? value)
    {
        return Fmt.CsvEscape(value);
    }

    private static string FId(uint formId)
    {
        return Fmt.FId(formId);
    }

    private static string FIdN(uint? formId)
    {
        return Fmt.FIdN(formId);
    }

    private static string Endian(bool isBigEndian)
    {
        return Fmt.Endian(isBigEndian);
    }

    private static string Resolve(uint formId, Dictionary<uint, string> lookup)
    {
        return Fmt.Resolve(formId, lookup);
    }

    // ───────────────────────────────── NPCs ─────────────────────────────────

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
            var gender = s != null ? (s.Flags & 1) == 1 ? "Female" : "Male" : "";

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

    // ───────────────────────────────── Weapons ─────────────────────────────────

    public static string GenerateWeaponsCsv(List<ReconstructedWeapon> weapons, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,WeaponType,WeaponTypeName,Damage,DPS,FireRate,ClipSize,MinRange,MaxRange,Spread,MinSpread,Drift,StrReq,SkillReq,CritDamage,CritChance,CritEffectFormID,Value,Weight,Health,AmmoFormID,AmmoName,ProjectileFormID,ProjectileName,ImpactDataSetFormID,ImpactDataSetName,APCost,ModelPath,PickupSoundFormID,PickupSoundName,PutdownSoundFormID,PutdownSoundName,FireSound3DFormID,FireSound3DName,FireSoundDistFormID,FireSoundDistName,FireSound2DFormID,FireSound2DName,DryFireSoundFormID,DryFireSoundName,IdleSoundFormID,IdleSoundName,EquipSoundFormID,EquipSoundName,UnequipSoundFormID,UnequipSoundName,ProjSpeed,ProjGravity,ProjRange,ProjForce,ProjExplosionFormID,ProjExplosionName,ProjInFlightSoundFormID,ProjInFlightSoundName,ProjModelPath,Endianness,Offset");

        foreach (var w in weapons.OrderBy(w => w.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "WEAPON",
                FId(w.FormId),
                E(w.EditorId),
                E(w.FullName),
                ((int)w.WeaponType).ToString(),
                E(w.WeaponTypeName),
                w.Damage.ToString(),
                w.DamagePerSecond.ToString("F1"),
                w.ShotsPerSec.ToString("F2"),
                w.ClipSize.ToString(),
                w.MinRange.ToString("F0"),
                w.MaxRange.ToString("F0"),
                w.Spread.ToString("F2"),
                w.MinSpread.ToString("F2"),
                w.Drift.ToString("F2"),
                w.StrengthRequirement.ToString(),
                w.SkillRequirement.ToString(),
                w.CriticalDamage.ToString(),
                w.CriticalChance.ToString("F2"),
                FIdN(w.CriticalEffectFormId),
                w.Value.ToString(),
                w.Weight.ToString("F2"),
                w.Health.ToString(),
                FIdN(w.AmmoFormId),
                Resolve(w.AmmoFormId ?? 0, lookup),
                FIdN(w.ProjectileFormId),
                Resolve(w.ProjectileFormId ?? 0, lookup),
                FIdN(w.ImpactDataSetFormId),
                Resolve(w.ImpactDataSetFormId ?? 0, lookup),
                w.ActionPoints.ToString("F1"),
                E(w.ModelPath),
                FIdN(w.PickupSoundFormId),
                Resolve(w.PickupSoundFormId ?? 0, lookup),
                FIdN(w.PutdownSoundFormId),
                Resolve(w.PutdownSoundFormId ?? 0, lookup),
                FIdN(w.FireSound3DFormId),
                Resolve(w.FireSound3DFormId ?? 0, lookup),
                FIdN(w.FireSoundDistFormId),
                Resolve(w.FireSoundDistFormId ?? 0, lookup),
                FIdN(w.FireSound2DFormId),
                Resolve(w.FireSound2DFormId ?? 0, lookup),
                FIdN(w.DryFireSoundFormId),
                Resolve(w.DryFireSoundFormId ?? 0, lookup),
                FIdN(w.IdleSoundFormId),
                Resolve(w.IdleSoundFormId ?? 0, lookup),
                FIdN(w.EquipSoundFormId),
                Resolve(w.EquipSoundFormId ?? 0, lookup),
                FIdN(w.UnequipSoundFormId),
                Resolve(w.UnequipSoundFormId ?? 0, lookup),
                w.ProjectileData?.Speed.ToString("F1") ?? "",
                w.ProjectileData?.Gravity.ToString("F4") ?? "",
                w.ProjectileData?.Range.ToString("F0") ?? "",
                w.ProjectileData?.Force.ToString("F1") ?? "",
                FIdN(w.ProjectileData?.ExplosionFormId),
                Resolve(w.ProjectileData?.ExplosionFormId ?? 0, lookup),
                FIdN(w.ProjectileData?.ActiveSoundLoopFormId),
                Resolve(w.ProjectileData?.ActiveSoundLoopFormId ?? 0, lookup),
                E(w.ProjectileData?.ModelPath),
                Endian(w.IsBigEndian),
                w.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Quests ─────────────────────────────────

    public static string GenerateQuestsCsv(List<ReconstructedQuest> quests, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Flags,Priority,ScriptFormID,Endianness,Offset,SubIndex,SubText,SubFlags,SubTargetStage");

        foreach (var q in quests.OrderBy(q => q.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "QUEST",
                FId(q.FormId),
                E(q.EditorId),
                E(q.FullName),
                q.Flags.ToString(),
                q.Priority.ToString(),
                FIdN(q.Script),
                Endian(q.IsBigEndian),
                q.Offset.ToString(),
                "", "", "", ""));

            foreach (var stage in q.Stages)
            {
                sb.AppendLine(string.Join(",",
                    "STAGE",
                    FId(q.FormId),
                    "", "", "", "", "",
                    "", "",
                    stage.Index.ToString(),
                    E(stage.LogEntry),
                    stage.Flags.ToString(),
                    ""));
            }

            foreach (var obj in q.Objectives)
            {
                sb.AppendLine(string.Join(",",
                    "OBJECTIVE",
                    FId(q.FormId),
                    "", "", "", "", "",
                    "", "",
                    obj.Index.ToString(),
                    E(obj.DisplayText),
                    "",
                    obj.TargetStage?.ToString() ?? ""));
            }
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Notes ─────────────────────────────────

    public static string GenerateNotesCsv(List<ReconstructedNote> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,NoteType,NoteTypeName,Text,ModelPath,Endianness,Offset");

        foreach (var n in notes.OrderBy(n => n.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "NOTE",
                FId(n.FormId),
                E(n.EditorId),
                E(n.FullName),
                n.NoteType.ToString(),
                E(n.NoteTypeName),
                E(n.Text),
                E(n.ModelPath),
                Endian(n.IsBigEndian),
                n.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Terminals ─────────────────────────────────

    public static string GenerateTerminalsCsv(List<ReconstructedTerminal> terminals, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Difficulty,DifficultyName,HeaderText,Endianness,Offset,MenuItemText,MenuItemResultText,MenuItemSubTerminalFormID");

        foreach (var t in terminals.OrderBy(t => t.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "TERMINAL",
                FId(t.FormId),
                E(t.EditorId),
                E(t.FullName),
                t.Difficulty.ToString(),
                E(t.DifficultyName),
                E(t.HeaderText),
                Endian(t.IsBigEndian),
                t.Offset.ToString(),
                "", "", ""));

            foreach (var mi in t.MenuItems)
            {
                sb.AppendLine(string.Join(",",
                    "MENUITEM",
                    FId(t.FormId),
                    "", "", "", "", "",
                    "", "",
                    E(mi.Text),
                    FIdN(mi.ResultScript),
                    FIdN(mi.SubTerminal)));
            }
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Dialogue ─────────────────────────────────

    public static string GenerateDialogueCsv(List<ReconstructedDialogue> dialogues, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,TopicFormID,TopicName,QuestFormID,QuestName,SpeakerFormID,SpeakerName,PreviousInfoFormID,PromptText,InfoIndex,InfoFlags,FlagsDescription,Difficulty,LinkToTopics,AddTopics,Endianness,Offset,ResponseNumber,ResponseText,EmotionType,EmotionName,EmotionValue");

        foreach (var d in dialogues.OrderBy(d => d.EditorId ?? ""))
        {
            // Build flags description
            var flags = new List<string>();
            if (d.IsGoodbye) flags.Add("Goodbye");
            if (d.IsSayOnce) flags.Add("SayOnce");
            if (d.IsSpeechChallenge) flags.Add("SpeechChallenge");
            if ((d.InfoFlags & 0x02) != 0) flags.Add("Random");
            if ((d.InfoFlags & 0x04) != 0) flags.Add("RandomEnd");
            var flagsDesc = string.Join(";", flags);

            // Build semicolon-separated FormID lists
            var linkToStr = d.LinkToTopics.Count > 0
                ? string.Join(";", d.LinkToTopics.Select(id => $"0x{id:X8}"))
                : "";
            var addStr = d.AddTopics.Count > 0
                ? string.Join(";", d.AddTopics.Select(id => $"0x{id:X8}"))
                : "";

            sb.AppendLine(string.Join(",",
                "DIALOGUE",
                FId(d.FormId),
                E(d.EditorId),
                FIdN(d.TopicFormId),
                Resolve(d.TopicFormId ?? 0, lookup),
                FIdN(d.QuestFormId),
                Resolve(d.QuestFormId ?? 0, lookup),
                FIdN(d.SpeakerFormId),
                Resolve(d.SpeakerFormId ?? 0, lookup),
                FIdN(d.PreviousInfo),
                E(d.PromptText),
                d.InfoIndex.ToString(),
                $"0x{d.InfoFlags:X2}",
                E(flagsDesc),
                d.Difficulty > 0 ? d.DifficultyName : "",
                E(linkToStr),
                E(addStr),
                Endian(d.IsBigEndian),
                d.Offset.ToString(),
                "", "", "", "", ""));

            foreach (var r in d.Responses)
            {
                sb.AppendLine(string.Join(",",
                    "RESPONSE",
                    FId(d.FormId),
                    "", "", "", "", "", "", "", "",
                    "", "", "", "", "", "", "",
                    "", "",
                    r.ResponseNumber.ToString(),
                    E(r.Text),
                    r.EmotionType.ToString(),
                    E(r.EmotionName),
                    r.EmotionValue.ToString()));
            }
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Factions ─────────────────────────────────

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

    // ───────────────────────────────── Game Settings ─────────────────────────────────

    public static string GenerateGameSettingsCsv(List<ReconstructedGameSetting> settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,EditorID,FormID,ValueType,Value,Endianness,Offset");

        foreach (var gs in settings.OrderBy(g => g.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "GMST",
                E(gs.EditorId),
                FId(gs.FormId),
                gs.ValueType.ToString(),
                E(gs.DisplayValue),
                Endian(gs.IsBigEndian),
                gs.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Containers ─────────────────────────────────

    public static string GenerateContainersCsv(List<ReconstructedContainer> containers, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Respawns,ModelPath,ScriptFormID,Endianness,Offset,ItemFormID,ItemName,Count");

        foreach (var c in containers.OrderBy(c => c.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "CONTAINER",
                FId(c.FormId),
                E(c.EditorId),
                E(c.FullName),
                c.Respawns.ToString(),
                E(c.ModelPath),
                FIdN(c.Script),
                Endian(c.IsBigEndian),
                c.Offset.ToString(),
                "", "", ""));

            foreach (var item in c.Contents)
            {
                sb.AppendLine(string.Join(",",
                    "ITEM",
                    FId(c.FormId),
                    "", "", "", "", "",
                    "", "",
                    FId(item.ItemFormId),
                    Resolve(item.ItemFormId, lookup),
                    item.Count.ToString()));
            }
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Armor ─────────────────────────────────

    public static string GenerateArmorCsv(List<ReconstructedArmor> armor)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,DT,DR,Value,Weight,Health,ModelPath,Endianness,Offset");

        foreach (var a in armor.OrderBy(a => a.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "ARMOR",
                FId(a.FormId),
                E(a.EditorId),
                E(a.FullName),
                a.DamageThreshold.ToString("F1"),
                a.DamageResistance.ToString(),
                a.Value.ToString(),
                a.Weight.ToString("F2"),
                a.Health.ToString(),
                E(a.ModelPath),
                Endian(a.IsBigEndian),
                a.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Ammo ─────────────────────────────────

    public static string GenerateAmmoCsv(List<ReconstructedAmmo> ammo, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Speed,Value,Weight,ClipRounds,Flags,ProjectileFormID,ProjectileName,ModelPath,ProjectileModelPath,Endianness,Offset");

        foreach (var a in ammo.OrderBy(a => a.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "AMMO",
                FId(a.FormId),
                E(a.EditorId),
                E(a.FullName),
                a.Speed.ToString("F2"),
                a.Value.ToString(),
                a.Weight.ToString("F2"),
                a.ClipRounds.ToString(),
                a.Flags.ToString(),
                FIdN(a.ProjectileFormId),
                Resolve(a.ProjectileFormId ?? 0, lookup),
                E(a.ModelPath),
                E(a.ProjectileModelPath),
                Endian(a.IsBigEndian),
                a.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Consumables ─────────────────────────────────

    public static string GenerateConsumablesCsv(List<ReconstructedConsumable> consumables,
        Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Value,Weight,AddictionFormID,AddictionName,AddictionChance,ModelPath,Endianness,Offset,EffectFormID,EffectName");

        foreach (var c in consumables.OrderBy(c => c.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "CONSUMABLE",
                FId(c.FormId),
                E(c.EditorId),
                E(c.FullName),
                c.Value.ToString(),
                c.Weight.ToString("F2"),
                FIdN(c.AddictionFormId),
                Resolve(c.AddictionFormId ?? 0, lookup),
                c.AddictionChance.ToString("F2"),
                E(c.ModelPath),
                Endian(c.IsBigEndian),
                c.Offset.ToString(),
                "", ""));

            foreach (var effectId in c.EffectFormIds)
            {
                sb.AppendLine(string.Join(",",
                    "EFFECT",
                    FId(c.FormId),
                    "", "", "", "", "", "", "", "",
                    "", "",
                    FId(effectId),
                    Resolve(effectId, lookup)));
            }
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Misc Items ─────────────────────────────────

    public static string GenerateMiscItemsCsv(List<ReconstructedMiscItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Value,Weight,ModelPath,Endianness,Offset");

        foreach (var m in items.OrderBy(m => m.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "MISC",
                FId(m.FormId),
                E(m.EditorId),
                E(m.FullName),
                m.Value.ToString(),
                m.Weight.ToString("F2"),
                E(m.ModelPath),
                Endian(m.IsBigEndian),
                m.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Keys ─────────────────────────────────

    public static string GenerateKeysCsv(List<ReconstructedKey> keys)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Value,Weight,ModelPath,Endianness,Offset");

        foreach (var k in keys.OrderBy(k => k.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "KEY",
                FId(k.FormId),
                E(k.EditorId),
                E(k.FullName),
                k.Value.ToString(),
                k.Weight.ToString("F2"),
                E(k.ModelPath),
                Endian(k.IsBigEndian),
                k.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Perks ─────────────────────────────────

    public static string GeneratePerksCsv(List<ReconstructedPerk> perks, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Description,Ranks,MinLevel,IsPlayable,IsTrait,IconPath,Endianness,Offset,EntryRank,EntryPriority,EntryType,EntryTypeName,EntryAbilityFormID,EntryAbilityName");

        foreach (var p in perks.OrderBy(p => p.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "PERK",
                FId(p.FormId),
                E(p.EditorId),
                E(p.FullName),
                E(p.Description),
                p.Ranks.ToString(),
                p.MinLevel.ToString(),
                p.IsPlayable.ToString(),
                p.IsTrait.ToString(),
                E(p.IconPath),
                Endian(p.IsBigEndian),
                p.Offset.ToString(),
                "", "", "", "", "", ""));

            foreach (var entry in p.Entries)
            {
                sb.AppendLine(string.Join(",",
                    "ENTRY",
                    FId(p.FormId),
                    "", "", "", "", "", "", "", "",
                    "", "",
                    entry.Rank.ToString(),
                    entry.Priority.ToString(),
                    entry.Type.ToString(),
                    E(entry.TypeName),
                    FIdN(entry.AbilityFormId),
                    Resolve(entry.AbilityFormId ?? 0, lookup)));
            }
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Spells ─────────────────────────────────

    public static string GenerateSpellsCsv(List<ReconstructedSpell> spells, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Type,TypeName,Cost,Level,Flags,Endianness,Offset,EffectFormID,EffectName");

        foreach (var s in spells.OrderBy(s => s.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "SPELL",
                FId(s.FormId),
                E(s.EditorId),
                E(s.FullName),
                ((int)s.Type).ToString(),
                E(s.TypeName),
                s.Cost.ToString(),
                s.Level.ToString(),
                s.Flags.ToString(),
                Endian(s.IsBigEndian),
                s.Offset.ToString(),
                "", ""));

            foreach (var effectId in s.EffectFormIds)
            {
                sb.AppendLine(string.Join(",",
                    "EFFECT",
                    FId(s.FormId),
                    "", "", "", "", "", "", "",
                    "", "",
                    FId(effectId),
                    Resolve(effectId, lookup)));
            }
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Races ─────────────────────────────────

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

    // ───────────────────────────────── Creatures ─────────────────────────────────

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

    // ───────────────────────────────── Books ─────────────────────────────────

    public static string GenerateBooksCsv(List<ReconstructedBook> books)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Value,Weight,TeachesSkill,SkillTaught,Text,ModelPath,Endianness,Offset");

        foreach (var b in books.OrderBy(b => b.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "BOOK",
                FId(b.FormId),
                E(b.EditorId),
                E(b.FullName),
                b.Value.ToString(),
                b.Weight.ToString("F2"),
                b.TeachesSkill.ToString(),
                b.SkillTaught.ToString(),
                E(b.Text),
                E(b.ModelPath),
                Endian(b.IsBigEndian),
                b.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Dialog Topics ─────────────────────────────────

    public static string GenerateDialogTopicsCsv(List<ReconstructedDialogTopic> topics, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,TopicType,TopicTypeName,Flags,IsRumors,IsTopLevel,QuestFormID,QuestName,ResponseCount,Priority,DummyPrompt,Endianness,Offset");

        foreach (var t in topics.OrderBy(t => t.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "TOPIC",
                FId(t.FormId),
                E(t.EditorId),
                E(t.FullName),
                t.TopicType.ToString(),
                E(t.TopicTypeName),
                t.Flags.ToString(),
                t.IsRumors.ToString(),
                t.IsTopLevel.ToString(),
                FIdN(t.QuestFormId),
                Resolve(t.QuestFormId ?? 0, lookup),
                t.ResponseCount.ToString(),
                t.Priority != 0f ? t.Priority.ToString("F1") : "",
                E(t.DummyPrompt),
                Endian(t.IsBigEndian),
                t.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Worldspaces ─────────────────────────────────

    public static string GenerateWorldspacesCsv(List<ReconstructedWorldspace> worldspaces,
        Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,ParentWorldspaceFormID,ClimateFormID,WaterFormID,CellCount,Endianness,Offset");

        foreach (var ws in worldspaces.OrderBy(w => w.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "WORLDSPACE",
                FId(ws.FormId),
                E(ws.EditorId),
                E(ws.FullName),
                FIdN(ws.ParentWorldspaceFormId),
                FIdN(ws.ClimateFormId),
                FIdN(ws.WaterFormId),
                ws.Cells.Count.ToString(),
                Endian(ws.IsBigEndian),
                ws.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Cells ─────────────────────────────────

    public static string GenerateCellsCsv(List<ReconstructedCell> cells, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,CellFormID,CellEditorID,CellName,GridX,GridY,IsInterior,HasWater,Flags,HasHeightmap,PlacedObjectCount,Endianness,Offset,ObjectFormID,ObjectEditorID,ObjectRecordType,BaseFormID,BaseEditorID,X,Y,Z,RotX,RotY,RotZ,Scale,OwnerFormID");

        foreach (var cell in cells.OrderBy(c => c.GridX ?? int.MaxValue).ThenBy(c => c.GridY ?? int.MaxValue))
        {
            sb.AppendLine(string.Join(",",
                "CELL",
                FId(cell.FormId),
                E(cell.EditorId),
                E(cell.FullName),
                cell.GridX?.ToString() ?? "",
                cell.GridY?.ToString() ?? "",
                cell.IsInterior.ToString(),
                cell.HasWater.ToString(),
                cell.Flags.ToString(),
                (cell.Heightmap != null).ToString(),
                cell.PlacedObjects.Count.ToString(),
                Endian(cell.IsBigEndian),
                cell.Offset.ToString(),
                "", "", "", "", "", "", "", "", "", "", "", "", ""));

            foreach (var obj in cell.PlacedObjects.OrderBy(o => o.RecordType).ThenBy(o => o.BaseEditorId ?? ""))
            {
                sb.AppendLine(string.Join(",",
                    "OBJ",
                    FId(cell.FormId),
                    "", "", "", "", "", "", "", "", "",
                    "", "",
                    FId(obj.FormId),
                    E(obj.BaseEditorId),
                    E(obj.RecordType),
                    FId(obj.BaseFormId),
                    Resolve(obj.BaseFormId, lookup),
                    obj.X.ToString("F2"),
                    obj.Y.ToString("F2"),
                    obj.Z.ToString("F2"),
                    obj.RotX.ToString("F4"),
                    obj.RotY.ToString("F4"),
                    obj.RotZ.ToString("F4"),
                    obj.Scale.ToString("F4"),
                    FIdN(obj.OwnerFormId)));
            }
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Map Markers ─────────────────────────────────

    public static string GenerateMapMarkersCsv(List<PlacedReference> markers, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,MarkerName,MarkerType,MarkerTypeName,BaseFormID,BaseEditorID,X,Y,Z,Endianness,Offset");

        foreach (var m in markers.OrderBy(m => m.MarkerName ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "MARKER",
                FId(m.FormId),
                E(m.MarkerName),
                m.MarkerType.HasValue ? ((ushort)m.MarkerType.Value).ToString() : "",
                E(m.MarkerType?.ToString()),
                FId(m.BaseFormId),
                E(m.BaseEditorId ?? Resolve(m.BaseFormId, lookup)),
                m.X.ToString("F2"),
                m.Y.ToString("F2"),
                m.Z.ToString("F2"),
                Endian(m.IsBigEndian),
                m.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Leveled Lists ─────────────────────────────────

    public static string GenerateLeveledListsCsv(List<ReconstructedLeveledList> lists, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,ListType,ChanceNone,Flags,FlagsDescription,GlobalFormID,GlobalEditorID,EntryCount,Endianness,Offset,EntryLevel,EntryFormID,EntryEditorID,EntryCount");

        foreach (var list in lists.OrderBy(l => l.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "LIST",
                FId(list.FormId),
                E(list.EditorId),
                E(list.ListType),
                list.ChanceNone.ToString(),
                list.Flags.ToString(),
                E(list.FlagsDescription),
                FIdN(list.GlobalFormId),
                list.GlobalFormId.HasValue ? Resolve(list.GlobalFormId.Value, lookup) : "",
                list.Entries.Count.ToString(),
                Endian(list.IsBigEndian),
                list.Offset.ToString(),
                "", "", "", ""));

            foreach (var entry in list.Entries.OrderBy(e => e.Level))
            {
                sb.AppendLine(string.Join(",",
                    "ENTRY",
                    FId(list.FormId),
                    "", "", "", "", "", "", "",
                    "", "", "",
                    entry.Level.ToString(),
                    FId(entry.FormId),
                    Resolve(entry.FormId, lookup),
                    entry.Count.ToString()));
            }
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Globals ─────────────────────────────────

    public static string GenerateGlobalsCsv(List<ReconstructedGlobal> globals)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,EditorID,FormID,ValueType,Value,Endianness,Offset");

        foreach (var g in globals.OrderBy(g => g.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "GLOB",
                E(g.EditorId),
                FId(g.FormId),
                g.TypeName,
                E(g.DisplayValue),
                Endian(g.IsBigEndian),
                g.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Enchantments ─────────────────────────────────

    public static string GenerateEnchantmentsCsv(List<ReconstructedEnchantment> enchantments,
        Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Type,ChargeAmount,EnchantCost,EffectCount,Endianness,Offset");

        foreach (var e in enchantments.OrderBy(e => e.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "ENCH",
                FId(e.FormId),
                E(e.EditorId),
                E(e.FullName),
                e.TypeName,
                e.ChargeAmount.ToString(),
                e.EnchantCost.ToString(),
                e.Effects.Count.ToString(),
                Endian(e.IsBigEndian),
                e.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Base Effects ─────────────────────────────────

    public static string GenerateBaseEffectsCsv(List<ReconstructedBaseEffect> effects)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Archetype,BaseCost,ActorValue,ResistValue,Endianness,Offset");

        foreach (var e in effects.OrderBy(e => e.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "MGEF",
                FId(e.FormId),
                E(e.EditorId),
                E(e.FullName),
                e.ArchetypeName,
                e.BaseCost.ToString("F2"),
                e.ActorValue.ToString(),
                e.ResistValue.ToString(),
                Endian(e.IsBigEndian),
                e.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Weapon Mods ─────────────────────────────────

    public static string GenerateWeaponModsCsv(List<ReconstructedWeaponMod> mods)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Description,Value,Weight,ModelPath,Endianness,Offset");

        foreach (var m in mods.OrderBy(m => m.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "IMOD",
                FId(m.FormId),
                E(m.EditorId),
                E(m.FullName),
                E(m.Description),
                m.Value.ToString(),
                m.Weight.ToString("F2"),
                E(m.ModelPath),
                Endian(m.IsBigEndian),
                m.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Recipes ─────────────────────────────────

    public static string GenerateRecipesCsv(List<ReconstructedRecipe> recipes,
        Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,RequiredSkill,RequiredLevel,Category,IngredientCount,OutputCount,Endianness,Offset");

        foreach (var r in recipes.OrderBy(r => r.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "RCPE",
                FId(r.FormId),
                E(r.EditorId),
                E(r.FullName),
                r.RequiredSkill.ToString(),
                r.RequiredSkillLevel.ToString(),
                Resolve(r.CategoryFormId, lookup),
                r.Ingredients.Count.ToString(),
                r.Outputs.Count.ToString(),
                Endian(r.IsBigEndian),
                r.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Challenges ─────────────────────────────────

    public static string GenerateChallengesCsv(List<ReconstructedChallenge> challenges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Type,Threshold,Interval,Description,Endianness,Offset");

        foreach (var c in challenges.OrderBy(c => c.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "CHAL",
                FId(c.FormId),
                E(c.EditorId),
                E(c.FullName),
                c.TypeName,
                c.Threshold.ToString(),
                c.Interval.ToString(),
                E(c.Description),
                Endian(c.IsBigEndian),
                c.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Reputations ─────────────────────────────────

    public static string GenerateReputationsCsv(List<ReconstructedReputation> reputations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,PositiveValue,NegativeValue,Endianness,Offset");

        foreach (var r in reputations.OrderBy(r => r.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "REPU",
                FId(r.FormId),
                E(r.EditorId),
                E(r.FullName),
                r.PositiveValue.ToString("F2"),
                r.NegativeValue.ToString("F2"),
                Endian(r.IsBigEndian),
                r.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Projectiles ─────────────────────────────────

    public static string GenerateProjectilesCsv(List<ReconstructedProjectile> projectiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Type,Speed,Gravity,Range,ImpactForce,ExplosionFormID,Endianness,Offset");

        foreach (var p in projectiles.OrderBy(p => p.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "PROJ",
                FId(p.FormId),
                E(p.EditorId),
                E(p.FullName),
                p.TypeName,
                p.Speed.ToString("F1"),
                p.Gravity.ToString("F4"),
                p.Range.ToString("F1"),
                p.ImpactForce.ToString("F1"),
                FIdN(p.Explosion),
                Endian(p.IsBigEndian),
                p.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Explosions ─────────────────────────────────

    public static string GenerateExplosionsCsv(List<ReconstructedExplosion> explosions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Force,Damage,Radius,Endianness,Offset");

        foreach (var e in explosions.OrderBy(e => e.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "EXPL",
                FId(e.FormId),
                E(e.EditorId),
                E(e.FullName),
                e.Force.ToString("F1"),
                e.Damage.ToString("F1"),
                e.Radius.ToString("F1"),
                Endian(e.IsBigEndian),
                e.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Messages ─────────────────────────────────

    public static string GenerateMessagesCsv(List<ReconstructedMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Title,Description,IsMessageBox,IsAutoDisplay,ButtonCount,Endianness,Offset");

        foreach (var m in messages.OrderBy(m => m.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "MESG",
                FId(m.FormId),
                E(m.EditorId),
                E(m.FullName),
                E(m.Description),
                m.IsMessageBox ? "Yes" : "No",
                m.IsAutoDisplay ? "Yes" : "No",
                m.Buttons.Count.ToString(),
                Endian(m.IsBigEndian),
                m.Offset.ToString()));
        }

        return sb.ToString();
    }

    // ───────────────────────────────── Classes ─────────────────────────────────

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

    /// <summary>
    ///     Generate CSV files from string pool data extracted from runtime memory.
    ///     Returns a dictionary mapping filename to content.
    /// </summary>
    public static Dictionary<string, string> GenerateStringPoolCsvs(StringPoolSummary sp)
    {
        var files = new Dictionary<string, string>();

        if (sp.AllDialogue.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Text,Length");
            foreach (var text in sp.AllDialogue.OrderByDescending(s => s.Length))
            {
                sb.AppendLine($"{E(text)},{text.Length}");
            }

            files["string_pool_dialogue.csv"] = sb.ToString();
        }

        if (sp.AllFilePaths.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Path,Extension");
            foreach (var path in sp.AllFilePaths.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var dot = path.LastIndexOf('.');
                var ext = dot >= 0 && dot < path.Length - 1 ? path[dot..] : "";
                sb.AppendLine($"{E(path)},{E(ext)}");
            }

            files["string_pool_file_paths.csv"] = sb.ToString();
        }

        if (sp.AllEditorIds.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("EditorID");
            foreach (var id in sp.AllEditorIds.OrderBy(s => s, StringComparer.Ordinal))
            {
                sb.AppendLine(E(id));
            }

            files["string_pool_editor_ids.csv"] = sb.ToString();
        }

        if (sp.AllSettings.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,InferredType");
            foreach (var name in sp.AllSettings.OrderBy(s => s, StringComparer.Ordinal))
            {
                var inferredType = name.Length > 0
                    ? name[0] switch
                    {
                        'f' => "Float",
                        'i' => "Int",
                        'b' => "Bool",
                        's' => "String",
                        'u' => "Unsigned",
                        _ => "Unknown"
                    }
                    : "Unknown";
                sb.AppendLine($"{E(name)},{inferredType}");
            }

            files["string_pool_game_settings.csv"] = sb.ToString();
        }

        return files;
    }
}
