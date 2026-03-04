using System.Text;
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
            "RowType,FormID,EditorID,TopicFormID,TopicName,TopicDisplayName,QuestFormID,QuestName,QuestDisplayName,SpeakerFormID,SpeakerName,SpeakerDisplayName,SpeakerAnimFormID,SpeakerAnimName,PreviousInfoFormID,PromptText,InfoIndex,InfoFlags,FlagsDescription,Difficulty,LinkToTopics,AddTopics,Endianness,Offset,ResponseNumber,ResponseText,EmotionType,EmotionName,EmotionValue");

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
                Fmt.FIdN(d.SpeakerAnimationFormId),
                resolver.ResolveCsv(d.SpeakerAnimationFormId ?? 0),
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
                    "", "", "", "", "", "", "", "", "", "", "", "", "",
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
}
