using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis;

/// <summary>Renders an <see cref="EsmScriptDiagnosticsResult" /> into its CSV reports and Markdown summary.</summary>
internal static class EsmScriptDiagnosticsCsvWriter
{
    public static string BuildTargetMatchesCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,record_type,form_id,editor_id,full_name,match_reason");
        foreach (var row in result.TargetMatches)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                Csv(row.FullName),
                Csv(row.MatchReason)));
        }

        return sb.ToString();
    }

    public static string BuildRecordsCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,relation,record_type,form_id,editor_id,full_name,interesting_subrecords");
        foreach (var row in result.Records)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Relation),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                Csv(row.FullName),
                Csv(row.InterestingSubrecords)));
        }

        return sb.ToString();
    }

    public static string BuildDialogueCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,info_form_id,info_editor_id,topic_form_id,topic_label,quest_form_id,speaker_form_id,previous_info,link_to_topics,link_from_topics,add_topics,follow_up_infos,info_flags,response_count,has_result_script,response_preview");
        foreach (var row in result.Dialogue)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv($"0x{row.InfoFormId:X8}"),
                Csv(row.InfoEditorId),
                Csv(row.TopicFormId == 0 ? string.Empty : $"0x{row.TopicFormId:X8}"),
                Csv(row.TopicLabel),
                Csv(row.QuestFormId == 0 ? string.Empty : $"0x{row.QuestFormId:X8}"),
                Csv(row.SpeakerFormId == 0 ? string.Empty : $"0x{row.SpeakerFormId:X8}"),
                Csv(row.PreviousInfo == 0 ? string.Empty : $"0x{row.PreviousInfo:X8}"),
                Csv(row.LinkToTopics),
                Csv(row.LinkFromTopics),
                Csv(row.AddTopics),
                Csv(row.FollowUpInfos),
                Csv(row.InfoFlags),
                row.ResponseCount,
                row.HasResultScript,
                Csv(row.ResponsePreview)));
        }

        return sb.ToString();
    }

    public static string BuildDialogueAuditCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,info_form_id,topic_form_id,topic_label,quest_form_id,speaker_form_id,root_classification,has_incoming_topic_edge,has_explicit_root_link,is_terminal_return_candidate,has_goodbye_for_speaker_quest,raw_tclt_bytes,link_to_topics,follow_up_infos,response_preview");
        foreach (var row in result.DialogueAudit)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv($"0x{row.InfoFormId:X8}"),
                Csv(row.TopicFormId == 0 ? string.Empty : $"0x{row.TopicFormId:X8}"),
                Csv(row.TopicLabel),
                Csv(row.QuestFormId == 0 ? string.Empty : $"0x{row.QuestFormId:X8}"),
                Csv(row.SpeakerFormId == 0 ? string.Empty : $"0x{row.SpeakerFormId:X8}"),
                Csv(row.RootClassification),
                row.HasIncomingTopicEdge,
                row.HasExplicitRootLink,
                row.IsTerminalReturnCandidate,
                row.HasGoodbyeForSpeakerQuest,
                Csv(row.RawTcltBytes),
                Csv(row.LinkToTopics),
                Csv(row.FollowUpInfos),
                Csv(row.ResponsePreview)));
        }

        return sb.ToString();
    }

    public static string BuildConditionsCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,relation,record_type,form_id,editor_id,condition_index,function_name,function_index,type,comparison_value,parameter1,parameter1_label,parameter2,parameter2_label,run_on,reference,reference_label,raw_bytes");
        foreach (var row in result.Conditions)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Relation),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                row.ConditionIndex,
                Csv(row.FunctionName),
                Csv($"0x{row.FunctionIndex:X4}"),
                row.Type,
                row.ComparisonValue.ToString(CultureInfo.InvariantCulture),
                Csv($"0x{row.Parameter1:X8}"),
                Csv(row.Parameter1Label),
                Csv($"0x{row.Parameter2:X8}"),
                Csv(row.Parameter2Label),
                row.RunOn,
                Csv(row.Reference == 0 ? string.Empty : $"0x{row.Reference:X8}"),
                Csv(row.ReferenceLabel),
                Csv(row.RawBytes)));
        }

        return sb.ToString();
    }

    public static string BuildScriptBlocksCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,relation,record_type,form_id,editor_id,block_index,subrecord_order,order_status,scda_length,schr_compiled_size,schr_reference_count,actual_reference_slots,compiled_size_matches,ref_count_matches,walked_to_end,has_diagnostics,diagnostics,source_text_preview");
        foreach (var row in result.ScriptBlocks)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Relation),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                row.BlockIndex,
                Csv(row.SubrecordOrder),
                Csv(row.OrderStatus),
                row.ScdaLength,
                row.SchrCompiledSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.SchrReferenceCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.ActualReferenceSlots,
                row.CompiledSizeMatches,
                row.RefCountMatches,
                row.WalkedToEnd,
                row.HasDiagnostics,
                Csv(row.Diagnostics),
                Csv(row.SourceTextPreview)));
        }

        return sb.ToString();
    }

    public static string BuildScriptReferencesCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,parent_record_type,parent_form_id,block_index,slot_index,reference_kind,raw_value,resolved_form_id,status,resolved_record_type,resolved_editor_id,resolved_full_name");
        foreach (var row in result.ScriptReferences)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.ParentRecordType),
                Csv($"0x{row.ParentFormId:X8}"),
                row.BlockIndex,
                row.SlotIndex,
                Csv(row.ReferenceKind),
                Csv($"0x{row.RawValue:X8}"),
                Csv(row.ResolvedFormId == 0 ? string.Empty : $"0x{row.ResolvedFormId:X8}"),
                Csv(row.Status),
                Csv(row.ResolvedRecordType),
                Csv(row.ResolvedEditorId),
                Csv(row.ResolvedFullName)));
        }

        return sb.ToString();
    }

    public static string BuildSummary(EsmScriptDiagnosticsResult result)
    {
        var missingRefs = result.ScriptReferences.Count(r => r.Status is "Null" or "Missing");
        var structuralFailures = result.ScriptBlocks.Count(r =>
            !r.CompiledSizeMatches || !r.RefCountMatches || !r.WalkedToEnd || r.HasDiagnostics);
        var nonCanonical = result.ScriptBlocks.Count(r => r.OrderStatus != "canonical");

        var sb = new StringBuilder();
        sb.AppendLine($"# Script Diagnostics: {Path.GetFileName(result.SourcePath)}");
        sb.AppendLine();
        sb.AppendLine($"- Targets: {string.Join(", ", result.Targets)}");
        sb.AppendLine($"- Target matches: {result.TargetMatches.Count:N0}");
        sb.AppendLine($"- Related records: {result.Records.Count:N0}");
        sb.AppendLine($"- Related INFO rows: {result.Dialogue.Count:N0}");
        sb.AppendLine($"- Script blocks: {result.ScriptBlocks.Count:N0}");
        sb.AppendLine($"- Script structural failures: {structuralFailures:N0}");
        sb.AppendLine($"- Non-canonical script block order: {nonCanonical:N0}");
        sb.AppendLine($"- Null/missing SCRO refs: {missingRefs:N0}");

        if (result.ScriptBlocks.Count > 0 && structuralFailures == 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                "Target SCDA bytecode walks cleanly; prioritize SCRO remap/content, package state, and result-script attachment/order.");
        }

        return sb.ToString();
    }

    private static string Csv(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}
