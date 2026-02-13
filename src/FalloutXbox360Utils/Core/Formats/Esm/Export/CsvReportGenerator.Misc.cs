using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class CsvMiscWriter
{
    public static string GenerateQuestsCsv(List<QuestRecord> quests, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Flags,Priority,ScriptFormID,Endianness,Offset,SubIndex,SubText,SubFlags,SubTargetStage");

        foreach (var q in quests.OrderBy(q => q.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "QUEST",
                Fmt.FId(q.FormId),
                Fmt.CsvEscape(q.EditorId),
                Fmt.CsvEscape(q.FullName),
                q.Flags.ToString(),
                q.Priority.ToString(),
                Fmt.FIdN(q.Script),
                Fmt.Endian(q.IsBigEndian),
                q.Offset.ToString(),
                "", "", "", ""));

            foreach (var stage in q.Stages)
            {
                sb.AppendLine(string.Join(",",
                    "STAGE",
                    Fmt.FId(q.FormId),
                    "", "", "", "", "",
                    "", "",
                    stage.Index.ToString(),
                    Fmt.CsvEscape(stage.LogEntry),
                    stage.Flags.ToString(),
                    ""));
            }

            foreach (var obj in q.Objectives)
            {
                sb.AppendLine(string.Join(",",
                    "OBJECTIVE",
                    Fmt.FId(q.FormId),
                    "", "", "", "", "",
                    "", "",
                    obj.Index.ToString(),
                    Fmt.CsvEscape(obj.DisplayText),
                    "",
                    obj.TargetStage?.ToString() ?? ""));
            }
        }

        return sb.ToString();
    }

    public static string GenerateDialogueCsv(List<DialogueRecord> dialogues, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,TopicFormID,TopicName,TopicDisplayName,QuestFormID,QuestName,QuestDisplayName,SpeakerFormID,SpeakerName,SpeakerDisplayName,PreviousInfoFormID,PromptText,InfoIndex,InfoFlags,FlagsDescription,Difficulty,LinkToTopics,AddTopics,Endianness,Offset,ResponseNumber,ResponseText,EmotionType,EmotionName,EmotionValue");

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
                Fmt.FId(d.FormId),
                Fmt.CsvEscape(d.EditorId),
                Fmt.FIdN(d.TopicFormId),
                resolver.ResolveCsv(d.TopicFormId ?? 0),
                resolver.ResolveDisplayNameCsv(d.TopicFormId ?? 0),
                Fmt.FIdN(d.QuestFormId),
                resolver.ResolveCsv(d.QuestFormId ?? 0),
                resolver.ResolveDisplayNameCsv(d.QuestFormId ?? 0),
                Fmt.FIdN(d.SpeakerFormId),
                resolver.ResolveCsv(d.SpeakerFormId ?? 0),
                resolver.ResolveDisplayNameCsv(d.SpeakerFormId ?? 0),
                Fmt.FIdN(d.PreviousInfo),
                Fmt.CsvEscape(d.PromptText),
                d.InfoIndex.ToString(),
                $"0x{d.InfoFlags:X2}",
                Fmt.CsvEscape(flagsDesc),
                d.Difficulty > 0 ? d.DifficultyName : "",
                Fmt.CsvEscape(linkToStr),
                Fmt.CsvEscape(addStr),
                Fmt.Endian(d.IsBigEndian),
                d.Offset.ToString(),
                "", "", "", "", ""));

            foreach (var r in d.Responses)
            {
                sb.AppendLine(string.Join(",",
                    "RESPONSE",
                    Fmt.FId(d.FormId),
                    "", "", "", "", "", "", "", "", "", "", "",
                    "", "", "", "", "", "", "",
                    "", "",
                    r.ResponseNumber.ToString(),
                    Fmt.CsvEscape(r.Text),
                    r.EmotionType.ToString(),
                    Fmt.CsvEscape(r.EmotionName),
                    r.EmotionValue.ToString()));
            }
        }

        return sb.ToString();
    }

    public static string GenerateDialogTopicsCsv(List<DialogTopicRecord> topics, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,TopicType,TopicTypeName,Flags,IsRumors,IsTopLevel,QuestFormID,QuestName,QuestDisplayName,ResponseCount,Priority,DummyPrompt,Endianness,Offset");

        foreach (var t in topics.OrderBy(t => t.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "TOPIC",
                Fmt.FId(t.FormId),
                Fmt.CsvEscape(t.EditorId),
                Fmt.CsvEscape(t.FullName),
                t.TopicType.ToString(),
                Fmt.CsvEscape(t.TopicTypeName),
                t.Flags.ToString(),
                t.IsRumors.ToString(),
                t.IsTopLevel.ToString(),
                Fmt.FIdN(t.QuestFormId),
                resolver.ResolveCsv(t.QuestFormId ?? 0),
                resolver.ResolveDisplayNameCsv(t.QuestFormId ?? 0),
                t.ResponseCount.ToString(),
                t.Priority is not 0f ? t.Priority.ToString("F1") : "",
                Fmt.CsvEscape(t.DummyPrompt),
                Fmt.Endian(t.IsBigEndian),
                t.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateCellsCsv(List<CellRecord> cells, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,CellFormID,CellEditorID,CellName,GridX,GridY,IsInterior,HasWater,Flags,HasHeightmap,PlacedObjectCount,Endianness,Offset,ObjectFormID,ObjectEditorID,ObjectRecordType,BaseFormID,BaseEditorID,BaseDisplayName,X,Y,Z,RotX,RotY,RotZ,Scale,OwnerFormID,BoundsX1,BoundsY1,BoundsZ1,BoundsX2,BoundsY2,BoundsZ2,ModelPath");

        foreach (var cell in cells.OrderBy(c => c.GridX ?? int.MaxValue).ThenBy(c => c.GridY ?? int.MaxValue))
        {
            sb.AppendLine(string.Join(",",
                "CELL",
                Fmt.FId(cell.FormId),
                Fmt.CsvEscape(cell.EditorId),
                Fmt.CsvEscape(cell.FullName),
                cell.GridX?.ToString() ?? "",
                cell.GridY?.ToString() ?? "",
                cell.IsInterior.ToString(),
                cell.HasWater.ToString(),
                cell.Flags.ToString(),
                (cell.Heightmap != null).ToString(),
                cell.PlacedObjects.Count.ToString(),
                Fmt.Endian(cell.IsBigEndian),
                cell.Offset.ToString(),
                "", "", "", "", "", "", "", "", "", "", "", "", "", "",
                "", "", "", "", "", "", ""));

            foreach (var obj in cell.PlacedObjects.OrderBy(o => o.RecordType).ThenBy(o => o.BaseEditorId ?? ""))
            {
                sb.AppendLine(string.Join(",",
                    "OBJ",
                    Fmt.FId(cell.FormId),
                    "", "", "", "", "", "", "", "", "",
                    "", "",
                    Fmt.FId(obj.FormId),
                    Fmt.CsvEscape(obj.BaseEditorId),
                    Fmt.CsvEscape(obj.RecordType),
                    Fmt.FId(obj.BaseFormId),
                    resolver.ResolveCsv(obj.BaseFormId),
                    resolver.ResolveDisplayNameCsv(obj.BaseFormId),
                    obj.X.ToString("F2"),
                    obj.Y.ToString("F2"),
                    obj.Z.ToString("F2"),
                    obj.RotX.ToString("F4"),
                    obj.RotY.ToString("F4"),
                    obj.RotZ.ToString("F4"),
                    obj.Scale.ToString("F4"),
                    Fmt.FIdN(obj.OwnerFormId),
                    obj.Bounds?.X1.ToString() ?? "",
                    obj.Bounds?.Y1.ToString() ?? "",
                    obj.Bounds?.Z1.ToString() ?? "",
                    obj.Bounds?.X2.ToString() ?? "",
                    obj.Bounds?.Y2.ToString() ?? "",
                    obj.Bounds?.Z2.ToString() ?? "",
                    Fmt.CsvEscape(obj.ModelPath)));
            }
        }

        return sb.ToString();
    }

    public static string GenerateWorldspacesCsv(List<WorldspaceRecord> worldspaces,
        FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,ParentWorldspaceFormID,ClimateFormID,WaterFormID,CellCount,Endianness,Offset,DefaultLandHeight,DefaultWaterHeight,BoundsMinX,BoundsMinY,BoundsMaxX,BoundsMaxY,MapWidth,MapHeight,MapNWCellX,MapNWCellY,MapSECellX,MapSECellY");

        foreach (var ws in worldspaces.OrderBy(w => w.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "WORLDSPACE",
                Fmt.FId(ws.FormId),
                Fmt.CsvEscape(ws.EditorId),
                Fmt.CsvEscape(ws.FullName),
                Fmt.FIdN(ws.ParentWorldspaceFormId),
                Fmt.FIdN(ws.ClimateFormId),
                Fmt.FIdN(ws.WaterFormId),
                ws.Cells.Count.ToString(),
                Fmt.Endian(ws.IsBigEndian),
                ws.Offset.ToString(),
                ws.DefaultLandHeight?.ToString("F1") ?? "",
                ws.DefaultWaterHeight?.ToString("F1") ?? "",
                ws.BoundsMinX?.ToString("F0") ?? "",
                ws.BoundsMinY?.ToString("F0") ?? "",
                ws.BoundsMaxX?.ToString("F0") ?? "",
                ws.BoundsMaxY?.ToString("F0") ?? "",
                ws.MapUsableWidth?.ToString() ?? "",
                ws.MapUsableHeight?.ToString() ?? "",
                ws.MapNWCellX?.ToString() ?? "",
                ws.MapNWCellY?.ToString() ?? "",
                ws.MapSECellX?.ToString() ?? "",
                ws.MapSECellY?.ToString() ?? ""));
        }

        return sb.ToString();
    }

    public static string GeneratePerksCsv(List<PerkRecord> perks, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Description,Ranks,MinLevel,IsPlayable,IsTrait,IconPath,Endianness,Offset,EntryRank,EntryPriority,EntryType,EntryTypeName,EntryAbilityFormID,EntryAbilityName,EntryAbilityDisplayName");

        foreach (var p in perks.OrderBy(p => p.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "PERK",
                Fmt.FId(p.FormId),
                Fmt.CsvEscape(p.EditorId),
                Fmt.CsvEscape(p.FullName),
                Fmt.CsvEscape(p.Description),
                p.Ranks.ToString(),
                p.MinLevel.ToString(),
                p.IsPlayable.ToString(),
                p.IsTrait.ToString(),
                Fmt.CsvEscape(p.IconPath),
                Fmt.Endian(p.IsBigEndian),
                p.Offset.ToString(),
                "", "", "", "", "", "", ""));

            foreach (var entry in p.Entries)
            {
                sb.AppendLine(string.Join(",",
                    "ENTRY",
                    Fmt.FId(p.FormId),
                    "", "", "", "", "", "", "", "",
                    "", "",
                    entry.Rank.ToString(),
                    entry.Priority.ToString(),
                    entry.Type.ToString(),
                    Fmt.CsvEscape(entry.TypeName),
                    Fmt.FIdN(entry.AbilityFormId),
                    resolver.ResolveCsv(entry.AbilityFormId ?? 0),
                    resolver.ResolveDisplayNameCsv(entry.AbilityFormId ?? 0)));
            }
        }

        return sb.ToString();
    }

    public static string GenerateSpellsCsv(List<SpellRecord> spells, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Type,TypeName,Cost,Level,Flags,Endianness,Offset,EffectFormID,EffectName,EffectDisplayName");

        foreach (var s in spells.OrderBy(s => s.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "SPELL",
                Fmt.FId(s.FormId),
                Fmt.CsvEscape(s.EditorId),
                Fmt.CsvEscape(s.FullName),
                ((int)s.Type).ToString(),
                Fmt.CsvEscape(s.TypeName),
                s.Cost.ToString(),
                s.Level.ToString(),
                s.Flags.ToString(),
                Fmt.Endian(s.IsBigEndian),
                s.Offset.ToString(),
                "", "", ""));

            foreach (var effectId in s.EffectFormIds)
            {
                sb.AppendLine(string.Join(",",
                    "EFFECT",
                    Fmt.FId(s.FormId),
                    "", "", "", "", "", "", "",
                    "", "",
                    Fmt.FId(effectId),
                    resolver.ResolveCsv(effectId),
                    resolver.ResolveDisplayNameCsv(effectId)));
            }
        }

        return sb.ToString();
    }

    public static string GenerateEnchantmentsCsv(List<EnchantmentRecord> enchantments,
        FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Type,ChargeAmount,EnchantCost,EffectCount,Endianness,Offset");

        foreach (var e in enchantments.OrderBy(e => e.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "ENCH",
                Fmt.FId(e.FormId),
                Fmt.CsvEscape(e.EditorId),
                Fmt.CsvEscape(e.FullName),
                e.TypeName,
                e.ChargeAmount.ToString(),
                e.EnchantCost.ToString(),
                e.Effects.Count.ToString(),
                Fmt.Endian(e.IsBigEndian),
                e.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateBaseEffectsCsv(List<BaseEffectRecord> effects)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Archetype,BaseCost,ActorValue,ResistValue,Endianness,Offset");

        foreach (var e in effects.OrderBy(e => e.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "MGEF",
                Fmt.FId(e.FormId),
                Fmt.CsvEscape(e.EditorId),
                Fmt.CsvEscape(e.FullName),
                e.ArchetypeName,
                e.BaseCost.ToString("F2"),
                e.ActorValue.ToString(),
                e.ResistValue.ToString(),
                Fmt.Endian(e.IsBigEndian),
                e.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateChallengesCsv(List<ChallengeRecord> challenges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Type,Threshold,Interval,Description,Endianness,Offset");

        foreach (var c in challenges.OrderBy(c => c.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "CHAL",
                Fmt.FId(c.FormId),
                Fmt.CsvEscape(c.EditorId),
                Fmt.CsvEscape(c.FullName),
                c.TypeName,
                c.Threshold.ToString(),
                c.Interval.ToString(),
                Fmt.CsvEscape(c.Description),
                Fmt.Endian(c.IsBigEndian),
                c.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateExplosionsCsv(List<ExplosionRecord> explosions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Force,Damage,Radius,Endianness,Offset");

        foreach (var e in explosions.OrderBy(e => e.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "EXPL",
                Fmt.FId(e.FormId),
                Fmt.CsvEscape(e.EditorId),
                Fmt.CsvEscape(e.FullName),
                e.Force.ToString("F1"),
                e.Damage.ToString("F1"),
                e.Radius.ToString("F1"),
                Fmt.Endian(e.IsBigEndian),
                e.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateGameSettingsCsv(List<GameSettingRecord> settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,EditorID,FormID,ValueType,Value,Endianness,Offset");

        foreach (var gs in settings.OrderBy(g => g.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "GMST",
                Fmt.CsvEscape(gs.EditorId),
                Fmt.FId(gs.FormId),
                gs.ValueType.ToString(),
                Fmt.CsvEscape(gs.DisplayValue),
                Fmt.Endian(gs.IsBigEndian),
                gs.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateGlobalsCsv(List<GlobalRecord> globals)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,EditorID,FormID,ValueType,Value,Endianness,Offset");

        foreach (var g in globals.OrderBy(g => g.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "GLOB",
                Fmt.CsvEscape(g.EditorId),
                Fmt.FId(g.FormId),
                g.TypeName,
                Fmt.CsvEscape(g.DisplayValue),
                Fmt.Endian(g.IsBigEndian),
                g.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateLeveledListsCsv(List<LeveledListRecord> lists, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,ListType,ChanceNone,Flags,FlagsDescription,GlobalFormID,GlobalEditorID,GlobalDisplayName,EntryCount,Endianness,Offset,EntryLevel,EntryFormID,EntryEditorID,EntryDisplayName,EntryCount");

        foreach (var list in lists.OrderBy(l => l.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "LIST",
                Fmt.FId(list.FormId),
                Fmt.CsvEscape(list.EditorId),
                Fmt.CsvEscape(list.ListType),
                list.ChanceNone.ToString(),
                list.Flags.ToString(),
                Fmt.CsvEscape(list.FlagsDescription),
                Fmt.FIdN(list.GlobalFormId),
                list.GlobalFormId.HasValue ? resolver.ResolveCsv(list.GlobalFormId.Value) : "",
                list.GlobalFormId.HasValue ? resolver.ResolveDisplayNameCsv(list.GlobalFormId.Value) : "",
                list.Entries.Count.ToString(),
                Fmt.Endian(list.IsBigEndian),
                list.Offset.ToString(),
                "", "", "", "", ""));

            foreach (var entry in list.Entries.OrderBy(e => e.Level))
            {
                sb.AppendLine(string.Join(",",
                    "ENTRY",
                    Fmt.FId(list.FormId),
                    "", "", "", "", "", "", "", "",
                    "", "", "",
                    entry.Level.ToString(),
                    Fmt.FId(entry.FormId),
                    resolver.ResolveCsv(entry.FormId),
                    resolver.ResolveDisplayNameCsv(entry.FormId),
                    entry.Count.ToString()));
            }
        }

        return sb.ToString();
    }

    public static string GenerateMapMarkersCsv(List<PlacedReference> markers, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,MarkerName,MarkerType,MarkerTypeName,BaseFormID,BaseEditorID,BaseDisplayName,X,Y,Z,Endianness,Offset");

        foreach (var m in markers.OrderBy(m => m.MarkerName ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "MARKER",
                Fmt.FId(m.FormId),
                Fmt.CsvEscape(m.MarkerName),
                m.MarkerType.HasValue ? ((ushort)m.MarkerType.Value).ToString() : "",
                Fmt.CsvEscape(m.MarkerType?.ToString()),
                Fmt.FId(m.BaseFormId),
                Fmt.CsvEscape(m.BaseEditorId ?? resolver.ResolveCsv(m.BaseFormId)),
                resolver.ResolveDisplayNameCsv(m.BaseFormId),
                m.X.ToString("F2"),
                m.Y.ToString("F2"),
                m.Z.ToString("F2"),
                Fmt.Endian(m.IsBigEndian),
                m.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateMessagesCsv(List<MessageRecord> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Title,Description,IsMessageBox,IsAutoDisplay,ButtonCount,Endianness,Offset");

        foreach (var m in messages.OrderBy(m => m.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "MESG",
                Fmt.FId(m.FormId),
                Fmt.CsvEscape(m.EditorId),
                Fmt.CsvEscape(m.FullName),
                Fmt.CsvEscape(m.Description),
                m.IsMessageBox ? "Yes" : "No",
                m.IsAutoDisplay ? "Yes" : "No",
                m.Buttons.Count.ToString(),
                Fmt.Endian(m.IsBigEndian),
                m.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateNotesCsv(List<NoteRecord> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,NoteType,NoteTypeName,Text,ModelPath,Endianness,Offset");

        foreach (var n in notes.OrderBy(n => n.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "NOTE",
                Fmt.FId(n.FormId),
                Fmt.CsvEscape(n.EditorId),
                Fmt.CsvEscape(n.FullName),
                n.NoteType.ToString(),
                Fmt.CsvEscape(n.NoteTypeName),
                Fmt.CsvEscape(n.Text),
                Fmt.CsvEscape(n.ModelPath),
                Fmt.Endian(n.IsBigEndian),
                n.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateProjectilesCsv(List<ProjectileRecord> projectiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Type,Speed,Gravity,Range,ImpactForce,ExplosionFormID,Endianness,Offset");

        foreach (var p in projectiles.OrderBy(p => p.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "PROJ",
                Fmt.FId(p.FormId),
                Fmt.CsvEscape(p.EditorId),
                Fmt.CsvEscape(p.FullName),
                p.TypeName,
                p.Speed.ToString("F1"),
                p.Gravity.ToString("F4"),
                p.Range.ToString("F1"),
                p.ImpactForce.ToString("F1"),
                Fmt.FIdN(p.Explosion),
                Fmt.Endian(p.IsBigEndian),
                p.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateReputationsCsv(List<ReputationRecord> reputations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,PositiveValue,NegativeValue,Endianness,Offset");

        foreach (var r in reputations.OrderBy(r => r.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "REPU",
                Fmt.FId(r.FormId),
                Fmt.CsvEscape(r.EditorId),
                Fmt.CsvEscape(r.FullName),
                r.PositiveValue.ToString("F2"),
                r.NegativeValue.ToString("F2"),
                Fmt.Endian(r.IsBigEndian),
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
                sb.AppendLine($"{Fmt.CsvEscape(text)},{text.Length}");
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
                sb.AppendLine($"{Fmt.CsvEscape(path)},{Fmt.CsvEscape(ext)}");
            }

            files["string_pool_file_paths.csv"] = sb.ToString();
        }

        if (sp.AllEditorIds.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("EditorID");
            foreach (var id in sp.AllEditorIds.OrderBy(s => s, StringComparer.Ordinal))
            {
                sb.AppendLine(Fmt.CsvEscape(id));
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
                sb.AppendLine($"{Fmt.CsvEscape(name)},{inferredType}");
            }

            files["string_pool_game_settings.csv"] = sb.ToString();
        }

        return files;
    }

    public static string GenerateTerminalsCsv(List<TerminalRecord> terminals, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Difficulty,DifficultyName,HeaderText,Endianness,Offset,MenuItemText,MenuItemResultText,MenuItemSubTerminalFormID");

        foreach (var t in terminals.OrderBy(t => t.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "TERMINAL",
                Fmt.FId(t.FormId),
                Fmt.CsvEscape(t.EditorId),
                Fmt.CsvEscape(t.FullName),
                t.Difficulty.ToString(),
                Fmt.CsvEscape(t.DifficultyName),
                Fmt.CsvEscape(t.HeaderText),
                Fmt.Endian(t.IsBigEndian),
                t.Offset.ToString(),
                "", "", ""));

            foreach (var mi in t.MenuItems)
            {
                sb.AppendLine(string.Join(",",
                    "MENUITEM",
                    Fmt.FId(t.FormId),
                    "", "", "", "", "",
                    "", "",
                    Fmt.CsvEscape(mi.Text),
                    Fmt.FIdN(mi.ResultScript),
                    Fmt.FIdN(mi.SubTerminal)));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Generate enriched asset CSV: combines FormID-based model paths (from ESM records)
    ///     with runtime string pool detections. Each row is a unique asset path, annotated with
    ///     the FormID(s) that reference it and whether it was also found in the string pool.
    /// </summary>
    public static string GenerateEnrichedAssetsCsv(
        RecordCollection records,
        List<DetectedAssetString>? assetStrings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FormID,EditorID,RecordType,AssetPath,AssetCategory,InStringPool");

        // Build FormID → RecordType lookup from all collections that contribute to modelIndex
        var formIdToType = new Dictionary<uint, string>();
        AddRecordTypes(formIdToType, records.Statics, "STAT");
        AddRecordTypes(formIdToType, records.Activators, "ACTI");
        AddRecordTypes(formIdToType, records.Doors, "DOOR");
        AddRecordTypes(formIdToType, records.Lights, "LIGH");
        AddRecordTypes(formIdToType, records.Furniture, "FURN");
        AddRecordTypes(formIdToType, records.Weapons, "WEAP");
        AddRecordTypes(formIdToType, records.Armor, "ARMO");
        AddRecordTypes(formIdToType, records.Ammo, "AMMO");
        AddRecordTypes(formIdToType, records.Consumables, "ALCH");
        AddRecordTypes(formIdToType, records.MiscItems, "MISC");
        AddRecordTypes(formIdToType, records.Books, "BOOK");
        AddRecordTypes(formIdToType, records.Containers, "CONT");

        // Build a set of normalized string-pool paths for cross-reference
        var stringPoolPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (assetStrings != null)
        {
            foreach (var asset in assetStrings)
            {
                stringPoolPaths.Add(NormalizePath(asset.Path));
            }
        }

        // Track which string-pool paths are matched to a FormID
        var matchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Emit rows for each FormID → model path mapping
        foreach (var (formId, modelPath) in records.ModelPathIndex.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            var editorId = records.FormIdToEditorId.GetValueOrDefault(formId, "");
            var recordType = formIdToType.GetValueOrDefault(formId, "");
            var normalizedPath = NormalizePath(modelPath);
            var inPool = stringPoolPaths.Contains(normalizedPath) ? "Yes" : "No";
            if (inPool == "Yes")
            {
                matchedPaths.Add(normalizedPath);
            }

            sb.AppendLine(string.Join(",",
                Fmt.FId(formId),
                Fmt.CsvEscape(editorId),
                recordType,
                Fmt.CsvEscape(modelPath),
                "Model",
                inPool));
        }

        // Emit orphan rows: string-pool asset paths with no known FormID owner
        if (assetStrings != null)
        {
            var orphans = assetStrings
                .Where(a => !matchedPaths.Contains(NormalizePath(a.Path)))
                .Select(a => (Path: GeckReportGenerator.CleanAssetPath(a.Path), a.Category))
                .DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var (path, category) in orphans)
            {
                sb.AppendLine(string.Join(",",
                    "",
                    "",
                    "",
                    Fmt.CsvEscape(path),
                    category.ToString(),
                    "Yes"));
            }
        }

        return sb.ToString();

        static void AddRecordTypes<T>(Dictionary<uint, string> map, List<T> records, string type)
            where T : class
        {
            foreach (var record in records)
            {
                // Use reflection-free approach: all these types have a FormId property
                var formId = record switch
                {
                    StaticRecord r => r.FormId,
                    ActivatorRecord r => r.FormId,
                    DoorRecord r => r.FormId,
                    LightRecord r => r.FormId,
                    FurnitureRecord r => r.FormId,
                    WeaponRecord r => r.FormId,
                    ArmorRecord r => r.FormId,
                    AmmoRecord r => r.FormId,
                    ConsumableRecord r => r.FormId,
                    MiscItemRecord r => r.FormId,
                    BookRecord r => r.FormId,
                    ContainerRecord r => r.FormId,
                    _ => 0u
                };
                if (formId != 0)
                {
                    map.TryAdd(formId, type);
                }
            }
        }

        static string NormalizePath(string path)
            => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
    }
}
