using System.Text;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>Renders an <see cref="EsmScriptProvenanceReport" /> into its CSV files.</summary>
internal static class EsmScriptProvenanceCsvWriter
{
    public static string BuildSourceVsEmittedRefsCsv(EsmScriptProvenanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,record_type,emitted_form_id,emitted_editor_id,block_index,slot_index,match_strategy,source_origin,source_form_id,source_kind,source_raw_value,source_label,emitted_kind,emitted_raw_value,emitted_label,classification");
        foreach (var row in report.SourceVsEmittedRefs)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.RecordType),
                Csv($"0x{row.EmittedFormId:X8}"),
                Csv(row.EmittedEditorId),
                row.BlockIndex,
                row.SlotIndex,
                Csv(row.MatchStrategy),
                Csv(row.SourceOrigin),
                Csv(row.SourceFormId == 0 ? string.Empty : $"0x{row.SourceFormId:X8}"),
                Csv(row.SourceKind),
                Csv(row.SourceRawValue == 0 ? string.Empty : $"0x{row.SourceRawValue:X8}"),
                Csv(row.SourceLabel),
                Csv(row.EmittedKind),
                Csv(row.EmittedRawValue == 0 ? string.Empty : $"0x{row.EmittedRawValue:X8}"),
                Csv(row.EmittedLabel),
                Csv(row.Classification)));
        }

        return sb.ToString();
    }

    public static string BuildResultScriptProvenanceCsv(EsmScriptProvenanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,emitted_info_form_id,source_info_form_id,match_strategy,source_block_count,emitted_block_count,source_scda_hashes,emitted_scda_hashes,source_sctx_preview,emitted_sctx_preview,source_reference_counts,emitted_reference_counts,classification");
        foreach (var row in report.ResultScripts)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv($"0x{row.EmittedInfoFormId:X8}"),
                Csv(row.SourceInfoFormId == 0 ? string.Empty : $"0x{row.SourceInfoFormId:X8}"),
                Csv(row.MatchStrategy),
                row.SourceBlockCount,
                row.EmittedBlockCount,
                Csv(row.SourceScdaHashes),
                Csv(row.EmittedScdaHashes),
                Csv(row.SourceSctxPreview),
                Csv(row.EmittedSctxPreview),
                Csv(row.SourceReferenceCounts),
                Csv(row.EmittedReferenceCounts),
                Csv(row.Classification)));
        }

        return sb.ToString();
    }

    public static string BuildBytecodeEndianProbeCsv(EsmScriptProvenanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,origin,record_type,form_id,editor_id,block_index,byte_length,first_bytes,little_endian_opcode,big_endian_opcode,little_endian_walked_to_end,little_endian_has_diagnostics,little_endian_diagnostics,big_endian_walked_to_end,big_endian_has_diagnostics,big_endian_diagnostics,classification");
        foreach (var row in report.BytecodeEndianProbes)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Origin),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                row.BlockIndex,
                row.ByteLength,
                Csv(row.FirstBytes),
                Csv(row.LittleEndianOpcode),
                Csv(row.BigEndianOpcode),
                row.LittleEndianWalkedToEnd,
                row.LittleEndianHasDiagnostics,
                Csv(row.LittleEndianDiagnostics),
                row.BigEndianWalkedToEnd,
                row.BigEndianHasDiagnostics,
                Csv(row.BigEndianDiagnostics),
                Csv(row.Classification)));
        }

        return sb.ToString();
    }

    public static string BuildStateTraceCsv(EsmScriptProvenanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,category,relation,record_type,form_id,editor_id,linked_form_id,linked_label,detail");
        foreach (var row in report.StateTrace)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Target),
                Csv(row.Category),
                Csv(row.Relation),
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                Csv(row.EditorId),
                Csv(row.LinkedFormId == 0 ? string.Empty : $"0x{row.LinkedFormId:X8}"),
                Csv(row.LinkedLabel),
                Csv(row.Detail)));
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
