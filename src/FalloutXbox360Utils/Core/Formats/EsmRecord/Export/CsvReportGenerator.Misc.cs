using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Export;

public static partial class CsvReportGenerator
{
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
}
