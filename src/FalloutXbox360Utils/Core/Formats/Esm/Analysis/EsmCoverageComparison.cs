using System.Globalization;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Analysis;

public sealed record EsmCoverageComparisonResult(
    string BaselineSource,
    string CandidateSource,
    int BaselineScriptBlocks,
    int CandidateScriptBlocks,
    IReadOnlyList<EsmScriptBytecodeComparisonRow> CandidateIssues)
{
    public bool HasCandidateStructuralFailures => CandidateIssues.Count > 0;
}

public sealed record EsmScriptBytecodeComparisonRow(
    string RecordType,
    uint FormId,
    int BlockIndex,
    int ScdaLength,
    string CandidateIssues,
    string BaselineIssues,
    bool IsGeneratedOnlyFailure,
    string Diagnostics);

public static class EsmCoverageComparison
{
    public static EsmCoverageComparisonResult Compare(
        EsmCoverageResult baseline,
        EsmCoverageResult candidate)
    {
        return CompareScriptRows(
            baseline.SourcePath,
            baseline.ScriptBytecode,
            candidate.SourcePath,
            candidate.ScriptBytecode);
    }

    public static EsmCoverageComparisonResult CompareCoverageDirectories(
        string baselineDirectory,
        string candidateDirectory)
    {
        var baselineRows = ReadScriptBytecodeCoverageCsv(
            Path.Combine(baselineDirectory, "script_bytecode_coverage.csv"));
        var candidateRows = ReadScriptBytecodeCoverageCsv(
            Path.Combine(candidateDirectory, "script_bytecode_coverage.csv"));

        return CompareScriptRows(baselineDirectory, baselineRows, candidateDirectory, candidateRows);
    }

    public static EsmCoverageComparisonResult CompareScriptRows(
        string baselineSource,
        IReadOnlyList<EsmScriptBytecodeCoverageRow> baselineRows,
        string candidateSource,
        IReadOnlyList<EsmScriptBytecodeCoverageRow> candidateRows)
    {
        var baselineIssues = baselineRows
            .Where(HasStructuralIssue)
            .ToDictionary(RowKey, BuildIssueText);

        var candidateIssues = candidateRows
            .Where(HasStructuralIssue)
            .Select(row =>
            {
                var key = RowKey(row);
                baselineIssues.TryGetValue(key, out var baselineIssueText);
                var candidateIssueText = BuildIssueText(row);
                return new EsmScriptBytecodeComparisonRow(
                    row.RecordType,
                    row.FormId,
                    row.BlockIndex,
                    row.ScdaLength,
                    candidateIssueText,
                    baselineIssueText ?? string.Empty,
                    string.IsNullOrEmpty(baselineIssueText) ||
                    !string.Equals(baselineIssueText, candidateIssueText, StringComparison.Ordinal),
                    row.Diagnostics);
            })
            .OrderByDescending(r => r.IsGeneratedOnlyFailure)
            .ThenBy(r => r.RecordType, StringComparer.Ordinal)
            .ThenBy(r => r.FormId)
            .ThenBy(r => r.BlockIndex)
            .ToList();

        return new EsmCoverageComparisonResult(
            baselineSource,
            candidateSource,
            baselineRows.Count,
            candidateRows.Count,
            candidateIssues);
    }

    public static void WriteReport(EsmCoverageComparisonResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "script_bytecode_comparison.csv"),
            BuildComparisonCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "summary.md"),
            BuildSummary(result), Encoding.UTF8);
    }

    private static bool HasStructuralIssue(EsmScriptBytecodeCoverageRow row)
    {
        return !row.CompiledSizeMatches
               || !row.RefCountMatches
               || !row.WalkedToEnd
               || row.HasDiagnostics;
    }

    private static (string RecordType, uint FormId, int BlockIndex) RowKey(EsmScriptBytecodeCoverageRow row)
    {
        return (row.RecordType, row.FormId, row.BlockIndex);
    }

    private static string BuildIssueText(EsmScriptBytecodeCoverageRow row)
    {
        var issues = new List<string>();
        if (!row.CompiledSizeMatches)
        {
            issues.Add("compiled-size");
        }

        if (!row.RefCountMatches)
        {
            issues.Add("ref-count");
        }

        if (!row.WalkedToEnd)
        {
            issues.Add("walk");
        }

        if (row.HasDiagnostics)
        {
            issues.Add("diagnostics");
        }

        return string.Join('|', issues);
    }

    private static IReadOnlyList<EsmScriptBytecodeCoverageRow> ReadScriptBytecodeCoverageCsv(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Coverage script_bytecode_coverage.csv was not found.", path);
        }

        var rows = new List<EsmScriptBytecodeCoverageRow>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = SplitCsvLine(line);
            if (fields.Count < 17)
            {
                continue;
            }

            rows.Add(new EsmScriptBytecodeCoverageRow(
                fields[0],
                ParseFormId(fields[1]),
                ParseInt(fields[2]),
                ParseInt(fields[3]),
                ParseNullableUInt(fields[4]),
                ParseNullableUInt(fields[5]),
                ParseInt(fields[6]),
                ParseNullableUInt(fields[7]),
                ParseInt(fields[8]),
                ParseBool(fields[9]),
                ParseBool(fields[10]),
                ParseBool(fields[11]),
                ParseBool(fields[12]),
                ParseInt(fields[13]),
                ParseInt(fields[14]),
                ParseBool(fields[15]),
                fields[16]));
        }

        return rows;
    }

    private static string BuildComparisonCsv(EsmCoverageComparisonResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "record_type,form_id,block_index,scda_length,candidate_issues,baseline_issues,is_generated_only_failure,diagnostics");
        foreach (var row in result.CandidateIssues)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                row.BlockIndex,
                row.ScdaLength,
                Csv(row.CandidateIssues),
                Csv(row.BaselineIssues),
                row.IsGeneratedOnlyFailure,
                Csv(row.Diagnostics)));
        }

        return sb.ToString();
    }

    private static string BuildSummary(EsmCoverageComparisonResult result)
    {
        var generatedOnly = result.CandidateIssues.Count(r => r.IsGeneratedOnlyFailure);
        var sb = new StringBuilder();
        sb.AppendLine("# ESM Coverage Comparison");
        sb.AppendLine();
        sb.AppendLine($"- Baseline: {result.BaselineSource}");
        sb.AppendLine($"- Candidate: {result.CandidateSource}");
        sb.AppendLine($"- Baseline SCDA blocks: {result.BaselineScriptBlocks:N0}");
        sb.AppendLine($"- Candidate SCDA blocks: {result.CandidateScriptBlocks:N0}");
        sb.AppendLine($"- Candidate structural issue blocks: {result.CandidateIssues.Count:N0}");
        sb.AppendLine($"- Generated-only structural issue blocks: {generatedOnly:N0}");
        sb.AppendLine();
        sb.AppendLine(result.HasCandidateStructuralFailures
            ? "Candidate SCDA has structural failures; inspect script_bytecode_comparison.csv before chasing refs or package state."
            : "Candidate SCDA bytecode walks cleanly; raw bytecode structure is unlikely to be the root cause.");

        if (result.CandidateIssues.Count == 0)
        {
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine("| Record | FormID | Block | Size | Candidate Issues | Baseline Issues | Generated Only |");
        sb.AppendLine("|---|---|---:|---:|---|---|---|");
        foreach (var row in result.CandidateIssues.Take(25))
        {
            sb.AppendLine(
                $"| {row.RecordType} | 0x{row.FormId:X8} | {row.BlockIndex} | {row.ScdaLength} | {row.CandidateIssues} | {row.BaselineIssues} | {row.IsGeneratedOnlyFailure} |");
        }

        return sb.ToString();
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                fields.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        fields.Add(sb.ToString());
        return fields;
    }

    private static uint ParseFormId(string value)
    {
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static uint? ParseNullableUInt(string value)
    {
        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool ParseBool(string value)
    {
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static string Csv(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}
