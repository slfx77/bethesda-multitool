using System.Globalization;
using System.Text;

namespace Xbox360MemoryCarver.Core.Formats.Scda;

/// <summary>
///     Formats SCDA records for text output.
/// </summary>
public static class ScdaFormatter
{
    /// <summary>
    ///     Format a grouped quest script with all stages.
    /// </summary>
    public static string FormatGroupedScript(string questName, List<ScdaRecord> stages)
    {
        var sb = new StringBuilder();

        AppendGroupHeader(sb, questName, stages);

        var stageNum = 0;
        foreach (var stage in stages)
        {
            stageNum++;
            AppendStageRecord(sb, stage, stageNum);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Format a single ungrouped script.
    /// </summary>
    public static string FormatSingleScript(ScdaRecord record)
    {
        var sb = new StringBuilder();

        sb.AppendLine("; Ungrouped script");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"; SCDA offset: 0x{record.Offset:X8}, size: {record.BytecodeLength} bytes");

        AppendFormIdReferences(sb, record.FormIdReferences);
        sb.AppendLine();
        AppendSourceText(sb, record);
        AppendBytecode(sb, record.Bytecode);

        return sb.ToString();
    }

    private static void AppendGroupHeader(StringBuilder sb, string questName, List<ScdaRecord> stages)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"; Quest: {questName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"; Stage count: {stages.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"; Total bytecode: {stages.Sum(s => s.BytecodeLength)} bytes");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"; Offset range: 0x{stages[0].Offset:X8} - 0x{stages[^1].Offset:X8}");
        sb.AppendLine();
    }

    private static void AppendStageRecord(StringBuilder sb, ScdaRecord stage, int stageNum)
    {
        sb.AppendLine("; ═══════════════════════════════════════════════════════════════");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"; Stage {stageNum} - SCDA at 0x{stage.Offset:X8} ({stage.BytecodeLength} bytes)");
        sb.AppendLine("; ═══════════════════════════════════════════════════════════════");

        if (stage.FormIdReferences.Count > 0)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"; SCRO References: {string.Join(", ", stage.FormIdReferences.Select((id, i) => $"#{i + 1}=0x{id:X8}"))}");

        AppendSourceTextInline(sb, stage);
        sb.AppendLine();
        sb.AppendLine("; Bytecode (hex):");
        sb.Append(";   ").AppendLine(Convert.ToHexString(stage.Bytecode));
        sb.AppendLine();
    }

    private static void AppendFormIdReferences(StringBuilder sb, List<uint> formIds)
    {
        if (formIds.Count == 0) return;

        sb.AppendLine(CultureInfo.InvariantCulture, $"; SCRO References ({formIds.Count}):");
        for (var j = 0; j < formIds.Count; j++)
            sb.AppendLine(CultureInfo.InvariantCulture, $";   SCRO#{j + 1} = FormID 0x{formIds[j]:X8}");
    }

    private static void AppendSourceText(StringBuilder sb, ScdaRecord record)
    {
        if (record.HasAssociatedSctx)
        {
            sb.AppendLine("; === Original Source ===");
            sb.AppendLine(record.SourceText);
            sb.AppendLine();
        }
    }

    private static void AppendSourceTextInline(StringBuilder sb, ScdaRecord stage)
    {
        if (stage.HasAssociatedSctx)
        {
            sb.AppendLine();
            sb.AppendLine("; Source:");
            foreach (var line in stage.SourceText!.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                sb.Append(";   ").AppendLine(line.Trim());
        }
    }

    private static void AppendBytecode(StringBuilder sb, byte[] bytecode)
    {
        sb.AppendLine("; === Bytecode (hex) ===");
        sb.AppendLine(Convert.ToHexString(bytecode));
    }
}
