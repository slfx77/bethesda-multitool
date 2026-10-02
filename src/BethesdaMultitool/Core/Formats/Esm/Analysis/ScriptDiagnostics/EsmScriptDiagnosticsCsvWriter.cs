using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Scripts;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>Renders an <see cref="EsmScriptDiagnosticsResult" /> into its CSV reports and Markdown summary.</summary>
internal static class EsmScriptDiagnosticsCsvWriter
{
    /// <summary>
    ///     The report subdirectory that holds one source file per script block with SCTX. The CSV names a file
    ///     relative to the report directory with a <c>/</c> separator on every OS.
    /// </summary>
    internal const string SourceTextDirectoryName = "scripts";

    /// <summary>
    ///     The name of a block's source file, <c>{RecordType}_{FormId:X8}_block{NN:D2}{ext}</c>, where
    ///     <c>ext</c> is <see cref="ScriptExportFileNamer.DefaultExtension" /> for the game (<c>.gek</c> for
    ///     Fallout 3 and New Vegas, <c>.txt</c> otherwise). Null when the block has no SCTX or its SCTX is empty.
    ///     The name depends only on the block, so every target row of one block names the same file. A record
    ///     signature character outside <c>[A-Za-z0-9_]</c> becomes <c>_</c>, so the name can never leave the
    ///     directory.
    /// </summary>
    internal static string? SourceTextFileName(EsmScriptDiagnosticBlockRow row, BethesdaGame game)
    {
        if (row.SourceTextBytes is not { Length: > 0 })
        {
            return null;
        }

        var safeType = new StringBuilder(row.RecordType.Length);
        foreach (var c in row.RecordType)
        {
            safeType.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        var recordType = safeType.ToString();
        var extension = ScriptExportFileNamer.DefaultExtension(game);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{recordType}_{row.FormId:X8}_block{row.BlockIndex:D2}{extension}");
    }

    /// <summary>
    ///     The <c>source_text_file</c> cell: <see cref="SourceTextFileName" /> under
    ///     <see cref="SourceTextDirectoryName" />, joined with <c>/</c>; empty when the block has no source file.
    /// </summary>
    internal static string SourceTextRelativePath(EsmScriptDiagnosticBlockRow row, BethesdaGame game)
    {
        var fileName = SourceTextFileName(row, game);
        return fileName is null ? string.Empty : $"{SourceTextDirectoryName}/{fileName}";
    }

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
            "target,relation,record_type,form_id,editor_id,condition_index,function_name,function_index,type,comparison_value,parameter1,parameter1_label,parameter2,parameter2_label,run_on,reference_storage,semantic_reference_label,raw_bytes,comparison_kind,comparison_raw_bits,comparison_global_form_id,comparison_global_label,parameter3,reference_storage_is_semantic,semantic_reference_form_id,ctda_body_length,layout_status");
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
                row.NumericComparisonValue is { } numericComparison
                    ? numericComparison.ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                Csv($"0x{row.Parameter1:X8}"),
                Csv(row.Parameter1Label),
                Csv($"0x{row.Parameter2:X8}"),
                Csv(row.Parameter2Label),
                row.RunOn?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Csv(row.ReferenceStorage is { } referenceStorage
                    ? $"0x{referenceStorage:X8}"
                    : string.Empty),
                Csv(row.SemanticReferenceLabel),
                Csv(row.RawBytes),
                Csv(row.ComparisonKind),
                Csv($"0x{row.ComparisonRawBits:X8}"),
                Csv(row.ComparisonGlobalFormId is { } globalFormId ? $"0x{globalFormId:X8}" : string.Empty),
                Csv(row.ComparisonGlobalLabel),
                row.Parameter3?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.ReferenceStorageIsSemantic,
                Csv(row.SemanticReferenceFormId is { } semanticReference
                    ? $"0x{semanticReference:X8}"
                    : string.Empty),
                row.BodyLength,
                Csv(row.LayoutStatus)));
        }

        return sb.ToString();
    }

    /// <summary>
    ///     <c>target_result_scripts.csv</c>. The legacy columns end at <c>source_text_preview</c> (the first
    ///     180 characters). Appended after them: <c>source_text_length</c>, the owning TERM menu item
    ///     (<c>owner_item_index</c>, <c>owner_item_text</c>; empty for other records), <c>source_text</c>, the
    ///     whole SCTX with its line endings as stored (quoted, so a cell can span lines), and
    ///     <c>source_text_file</c>, the block's verbatim source file relative to the report directory
    ///     (<see cref="SourceTextRelativePath" />; empty when the block has no SCTX).
    /// </summary>
    public static string BuildScriptBlocksCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,relation,record_type,form_id,editor_id,block_index,subrecord_order,order_status,scda_length,schr_compiled_size,schr_reference_count,actual_reference_slots,compiled_size_matches,ref_count_matches,walked_to_end,has_diagnostics,diagnostics,source_text_preview,source_text_length,owner_item_index,owner_item_text,source_text,source_text_file");
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
                Csv(row.SourceTextPreview),
                row.SourceText.Length,
                row.OwnerMenuItemIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Csv(row.OwnerMenuItemText),
                Csv(row.SourceText),
                Csv(SourceTextRelativePath(row, result.Game))));
        }

        return sb.ToString();
    }

    /// <summary>
    ///     <c>target_terminal_items.csv</c>: one row per TERM menu item. <c>condition_indexes</c> are the
    ///     record-level CTDA ordinals (<c>condition_index</c> in <c>target_conditions.csv</c>) joined by
    ///     <c>|</c>, and <c>condition_expressions</c> the describer's line for each, in the same order, joined by
    ///     <c> | </c>. <c>script_block_index</c> is the <c>block_index</c> of the item's rows in
    ///     <c>target_result_scripts.csv</c> (<c>|</c>-joined if an item carries more than one).
    /// </summary>
    public static string BuildTerminalItemsCsv(EsmScriptDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "target,relation,form_id,editor_id,item_index,item_text,result_text,flags_raw,flag_names,display_note_form_id,display_note_label,sub_terminal_form_id,sub_terminal_label,condition_indexes,condition_expressions,script_block_index,source_text_length");
        foreach (var row in result.TerminalItems)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Relation),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                row.ItemIndex,
                Csv(row.ItemText),
                Csv(row.ResultText),
                Csv(row.FlagsRaw is { } flags ? $"0x{flags:X2}" : string.Empty),
                Csv(row.FlagNames),
                Csv(row.DisplayNoteFormId is { } note ? $"0x{note:X8}" : string.Empty),
                Csv(row.DisplayNoteLabel),
                Csv(row.SubTerminalFormId is { } child ? $"0x{child:X8}" : string.Empty),
                Csv(row.SubTerminalLabel),
                Csv(string.Join('|', row.ConditionIndexes.Select(i => i.ToString(CultureInfo.InvariantCulture)))),
                Csv(string.Join(" | ", row.ConditionExpressions)),
                Csv(string.Join('|', row.ScriptBlockIndexes.Select(i => i.ToString(CultureInfo.InvariantCulture)))),
                row.SourceTextLength?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
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
        sb.AppendLine($"- Game: {result.Game}");
        sb.AppendLine($"- Targets: {FormatTargets(result)}");
        if (result.ExplicitRecordFormIds.Count > 0)
        {
            sb.AppendLine($"- Explicit records: {FormatFormIdList(result.ExplicitRecordFormIds)}");
        }

        if (result.MissingExplicitRecordFormIds.Count > 0)
        {
            sb.AppendLine($"- Explicit records not found: {FormatFormIdList(result.MissingExplicitRecordFormIds)}");
        }

        sb.AppendLine($"- Target matches: {result.TargetMatches.Count:N0}");
        sb.AppendLine($"- Related records: {result.Records.Count:N0}");
        sb.AppendLine($"- Related INFO rows: {result.Dialogue.Count:N0}");
        sb.AppendLine($"- Script blocks: {result.ScriptBlocks.Count:N0}");
        sb.AppendLine($"- Script structural failures: {structuralFailures:N0}");
        sb.AppendLine($"- Non-canonical script block order: {nonCanonical:N0}");
        sb.AppendLine($"- Null/missing SCRO refs: {missingRefs:N0}");
        if (result.TerminalItems.Count > 0)
        {
            sb.AppendLine($"- Terminal menu items: {result.TerminalItems.Count:N0}");
        }

        var sourceFiles = result.ScriptBlocks
            .Select(row => SourceTextFileName(row, result.Game))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (sourceFiles > 0)
        {
            sb.AppendLine(
                $"- Script source files: {sourceFiles:N0} in {SourceTextDirectoryName}/ (verbatim SCTX bytes)");
        }

        if (result.ScriptBlocks.Count > 0 && structuralFailures == 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                "Target SCDA bytecode walks cleanly; prioritize SCRO remap/content, package state, and result-script attachment/order.");
        }

        return sb.ToString();
    }

    private static string FormatTargets(EsmScriptDiagnosticsResult result)
    {
        if (result.Targets.Count > 0)
        {
            return string.Join(", ", result.Targets);
        }

        return result.ExplicitRecordFormIds.Count > 0 ? "(none; explicit records only)" : "(none)";
    }

    private static string FormatFormIdList(IEnumerable<uint> formIds)
    {
        return string.Join(", ", formIds.Select(id => $"0x{id:X8}"));
    }

    private static string Csv(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}
