using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal static class NpcRuntimeFaceGenProbeCaptureComparer
{
    internal static RuntimeFaceGenProbeCapture LoadCapture(string captureDir)
    {
        var fullDir = Path.GetFullPath(captureDir);
        var captureJsonPath = Path.Combine(fullDir, "capture.json");
        var npcCsvPath = Path.Combine(fullDir, "npc_texture_coeffs.csv");
        var raceCsvPath = Path.Combine(fullDir, "race_texture_coeffs.csv");
        var mergedCsvPath = Path.Combine(fullDir, "texture_coeffs.csv");

        if (!File.Exists(captureJsonPath))
        {
            throw new FileNotFoundException("capture.json not found", captureJsonPath);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(captureJsonPath));
        var root = document.RootElement;
        var target = root.GetProperty("target");
        var descriptors = root.GetProperty("descriptors");

        return new RuntimeFaceGenProbeCapture
        {
            CaptureDirectory = fullDir,
            CaptureDirectoryName = Path.GetFileName(fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            BaseNpcFormId = ParseHexString(target.GetProperty("baseNpcFormId").GetString()),
            RaceFormId = ParseHexString(target.GetProperty("raceFormId").GetString()),
            IsFemale = target.GetProperty("female").GetBoolean(),
            MatchedFormType = target.TryGetProperty("matchedFormType", out var matchedFormType)
                ? matchedFormType.GetString()
                : null,
            NpcTextureDescriptor = ParseDescriptor(descriptors.GetProperty("npcTexture")),
            RaceTextureDescriptor = ParseDescriptor(descriptors.GetProperty("raceTexture")),
            NpcTextureCoefficients = LoadCoefficientCsv(npcCsvPath),
            RaceTextureCoefficients = LoadCoefficientCsv(raceCsvPath),
            MergedTextureCoefficients = LoadCoefficientCsv(mergedCsvPath)
        };
    }

    internal static RuntimeFaceGenProbeComparisonResult Compare(
        NpcAppearance appearance,
        RuntimeFaceGenProbeCapture capture,
        IReadOnlyDictionary<uint, RaceScanEntry>? races = null)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(capture);

        return new RuntimeFaceGenProbeComparisonResult
        {
            CaptureDirectory = capture.CaptureDirectory,
            CaptureDirectoryName = capture.CaptureDirectoryName,
            FormId = capture.BaseNpcFormId,
            RaceFormId = capture.RaceFormId,
            IsFemale = capture.IsFemale,
            EditorId = appearance.EditorId,
            FullName = appearance.FullName,
            Npc = CompareArray("npc", capture.NpcTextureCoefficients, appearance.NpcFaceGenTextureCoeffs),
            Race = CompareArray("race", capture.RaceTextureCoefficients, appearance.RaceFaceGenTextureCoeffs),
            Merged = CompareArray("merged", capture.MergedTextureCoefficients, appearance.FaceGenTextureCoeffs),
            RaceMatches = RankRaceMatches(capture, races)
        };
    }

    internal static void WriteArtifacts(string rootDir, RuntimeFaceGenProbeComparisonResult result)
    {
        var captureDir = Path.Combine(rootDir, result.CaptureDirectoryName);
        Directory.CreateDirectory(captureDir);

        if (!string.IsNullOrWhiteSpace(result.FailureReason))
        {
            File.WriteAllText(
                Path.Combine(captureDir, "summary.txt"),
                $"form_id=0x{result.FormId:X8}{Environment.NewLine}failure={result.FailureReason}{Environment.NewLine}",
                Encoding.UTF8);
            return;
        }

        WriteArrayCsv(Path.Combine(captureDir, "npc_compare.csv"), result.Npc);
        WriteArrayCsv(Path.Combine(captureDir, "race_compare.csv"), result.Race);
        WriteArrayCsv(Path.Combine(captureDir, "merged_compare.csv"), result.Merged);
        WriteRaceMatchesCsv(Path.Combine(captureDir, "race_matches.csv"), result.RaceMatches);
        File.WriteAllText(Path.Combine(captureDir, "summary.txt"), BuildSummaryText(result), Encoding.UTF8);
    }

    internal static void WriteSummaryCsv(
        IEnumerable<RuntimeFaceGenProbeComparisonResult> results,
        string summaryPath)
    {
        var fullPath = Path.GetFullPath(summaryPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            "capture_dir,form_id,race_form_id,editor_id,full_name,compared,failure_reason," +
            "npc_runtime_count,npc_current_count,npc_mean_abs_delta,npc_rmse_delta,npc_max_abs_delta,npc_mean_signed_delta," +
            "race_runtime_count,race_current_count,race_mean_abs_delta,race_rmse_delta,race_max_abs_delta,race_mean_signed_delta," +
            "merged_runtime_count,merged_current_count,merged_mean_abs_delta,merged_rmse_delta,merged_max_abs_delta,merged_mean_signed_delta," +
            "best_race_match_form_id,best_race_match_editor_id,best_race_match_sex,best_race_match_relation,best_race_match_mae,best_race_match_max_abs_delta");

        foreach (var result in results.OrderBy(r => r.FormId).ThenBy(r => r.CaptureDirectoryName))
        {
            var bestRaceMatch = result.RaceMatches?.OrderBy(match => match.Comparison.MeanAbsoluteDelta).FirstOrDefault();
            sb.Append(Csv(result.CaptureDirectoryName)).Append(',');
            sb.Append(Csv(result.FormId.ToString("X8", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(result.RaceFormId.ToString("X8", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(result.EditorId)).Append(',');
            sb.Append(Csv(result.FullName)).Append(',');
            sb.Append(Csv(result.Compared ? "true" : "false")).Append(',');
            sb.Append(Csv(result.FailureReason)).Append(',');
            AppendArraySummary(sb, result.Npc);
            AppendArraySummary(sb, result.Race);
            AppendArraySummary(sb, result.Merged);
            sb.Append(Csv(bestRaceMatch?.RaceFormId.ToString("X8", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(bestRaceMatch?.EditorId)).Append(',');
            sb.Append(Csv(bestRaceMatch?.CandidateSex)).Append(',');
            sb.Append(Csv(bestRaceMatch?.RelationToRuntimeRace)).Append(',');
            sb.Append(Csv(bestRaceMatch?.Comparison.MeanAbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(bestRaceMatch?.Comparison.MaxAbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture))).AppendLine();
        }

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
    }

    private static RuntimeFaceGenProbeDescriptor ParseDescriptor(JsonElement element)
    {
        return new RuntimeFaceGenProbeDescriptor(
            element.TryGetProperty("descriptorAddress", out var descriptorAddress) ? descriptorAddress.GetString() : null,
            element.TryGetProperty("valuesPointer", out var valuesPointer) ? valuesPointer.GetString() : null,
            element.TryGetProperty("count", out var count) ? count.GetUInt32() : 0u,
            element.TryGetProperty("stride", out var stride) ? stride.GetUInt32() : 0u,
            element.TryGetProperty("valid", out var valid) && valid.GetBoolean());
    }

    private static float[] LoadCoefficientCsv(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            return [];
        }

        var values = new List<float>();
        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(',', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (float.TryParse(parts[1], NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return [.. values];
    }

    private static RuntimeFaceGenProbeArrayComparison CompareArray(
        string label,
        float[] runtimeValues,
        float[]? currentValues)
    {
        var safeRuntimeValues = runtimeValues ?? [];
        var safeCurrentValues = currentValues ?? [];
        var comparedCount = Math.Min(safeRuntimeValues.Length, safeCurrentValues.Length);
        var rows = new List<RuntimeFaceGenProbeArrayComparisonRow>(comparedCount);

        double absSum = 0d;
        double squaredSum = 0d;
        double signedSum = 0d;
        double maxAbsDelta = 0d;
        var exactMatchCount = 0;

        for (var index = 0; index < comparedCount; index++)
        {
            var runtimeValue = safeRuntimeValues[index];
            var currentValue = safeCurrentValues[index];
            var delta = currentValue - runtimeValue;
            var absDelta = Math.Abs(delta);

            rows.Add(new RuntimeFaceGenProbeArrayComparisonRow(index, runtimeValue, currentValue, delta, absDelta));

            absSum += absDelta;
            squaredSum += delta * delta;
            signedSum += delta;
            maxAbsDelta = Math.Max(maxAbsDelta, absDelta);
            if (absDelta <= 1e-6)
            {
                exactMatchCount++;
            }
        }

        var denominator = comparedCount == 0 ? 1 : comparedCount;
        return new RuntimeFaceGenProbeArrayComparison(
            label,
            safeRuntimeValues.Length,
            safeCurrentValues.Length,
            comparedCount,
            comparedCount == 0 ? 0d : absSum / denominator,
            comparedCount == 0 ? 0d : Math.Sqrt(squaredSum / denominator),
            maxAbsDelta,
            comparedCount == 0 ? 0d : signedSum / denominator,
            exactMatchCount,
            rows);
    }

    private static IReadOnlyList<RuntimeFaceGenProbeRaceMatch> RankRaceMatches(
        RuntimeFaceGenProbeCapture capture,
        IReadOnlyDictionary<uint, RaceScanEntry>? races)
    {
        if (races == null || races.Count == 0 || capture.RaceTextureCoefficients.Length == 0)
        {
            return [];
        }

        races.TryGetValue(capture.RaceFormId, out var runtimeRace);
        var matches = new List<RuntimeFaceGenProbeRaceMatch>(races.Count * 2);

        foreach (var (raceFormId, race) in races)
        {
            AddRaceMatch(matches, capture, runtimeRace, raceFormId, race, race.MaleFaceGenTexture, false);
            AddRaceMatch(matches, capture, runtimeRace, raceFormId, race, race.FemaleFaceGenTexture, true);
        }

        return matches
            .OrderBy(match => match.Comparison.MeanAbsoluteDelta)
            .ThenBy(match => match.Comparison.MaxAbsoluteDelta)
            .ThenBy(match => match.EditorId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DescribeRaceRelation(
        uint runtimeRaceFormId,
        RaceScanEntry? runtimeRace,
        uint candidateRaceFormId,
        RaceScanEntry candidateRace)
    {
        if (candidateRaceFormId == runtimeRaceFormId)
        {
            return "runtime";
        }

        if (runtimeRace?.YoungerRaceFormId == candidateRaceFormId)
        {
            return "runtime.younger";
        }

        if (runtimeRace?.OlderRaceFormId == candidateRaceFormId)
        {
            return "runtime.older";
        }

        if (candidateRace.YoungerRaceFormId == runtimeRaceFormId)
        {
            return "candidate.younger=runtime";
        }

        if (candidateRace.OlderRaceFormId == runtimeRaceFormId)
        {
            return "candidate.older=runtime";
        }

        return "other";
    }

    private static void AddRaceMatch(
        List<RuntimeFaceGenProbeRaceMatch> matches,
        RuntimeFaceGenProbeCapture capture,
        RaceScanEntry? runtimeRace,
        uint raceFormId,
        RaceScanEntry race,
        float[]? coefficients,
        bool candidateIsFemale)
    {
        if (coefficients == null || coefficients.Length == 0)
        {
            return;
        }

        matches.Add(new RuntimeFaceGenProbeRaceMatch(
            raceFormId,
            race.EditorId,
            candidateIsFemale ? "female" : "male",
            DescribeRaceRelation(capture.RaceFormId, runtimeRace, raceFormId, race),
            CompareArray("race_candidate", capture.RaceTextureCoefficients, coefficients),
            FitUniformScale(capture.RaceTextureCoefficients, coefficients)));
    }

    private static uint ParseHexString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0u;
        }

        return uint.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static void WriteArrayCsv(string path, RuntimeFaceGenProbeArrayComparison? comparison)
    {
        var sb = new StringBuilder();
        sb.AppendLine("index,runtime_value,current_value,delta,abs_delta");

        if (comparison != null)
        {
            foreach (var row in comparison.Rows)
            {
                sb.Append(row.Index.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(row.RuntimeValue.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(row.CurrentValue.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(row.Delta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(row.AbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture)).AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteRaceMatchesCsv(
        string path,
        IReadOnlyList<RuntimeFaceGenProbeRaceMatch>? matches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("race_form_id,editor_id,candidate_sex,relation,compared_count,mean_abs_delta,rmse_delta,max_abs_delta,mean_signed_delta,exact_match_count,fit_scale,fit_mean_abs_delta,fit_rmse_delta,fit_max_abs_delta,fit_mean_signed_delta");

        if (matches != null)
        {
            foreach (var match in matches)
            {
                sb.Append(match.RaceFormId.ToString("X8", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(Csv(match.EditorId)).Append(',');
                sb.Append(Csv(match.CandidateSex)).Append(',');
                sb.Append(Csv(match.RelationToRuntimeRace)).Append(',');
                sb.Append(match.Comparison.ComparedCount.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(match.Comparison.MeanAbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(match.Comparison.RootMeanSquareDelta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(match.Comparison.MaxAbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(match.Comparison.MeanSignedDelta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(match.Comparison.ExactMatchCount.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(match.Fit.Scale.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(match.Fit.Comparison.MeanAbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(match.Fit.Comparison.RootMeanSquareDelta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(match.Fit.Comparison.MaxAbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(match.Fit.Comparison.MeanSignedDelta.ToString("F6", CultureInfo.InvariantCulture)).AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string BuildSummaryText(RuntimeFaceGenProbeComparisonResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"capture_dir={result.CaptureDirectory}");
        sb.AppendLine($"form_id=0x{result.FormId:X8}");
        sb.AppendLine($"race_form_id=0x{result.RaceFormId:X8}");
        sb.AppendLine($"editor_id={result.EditorId ?? string.Empty}");
        sb.AppendLine($"full_name={result.FullName ?? string.Empty}");
        sb.AppendLine($"female={result.IsFemale}");
        AppendSummaryBlock(sb, result.Npc);
        AppendSummaryBlock(sb, result.Race);
        AppendSummaryBlock(sb, result.Merged);

        if (result.Merged != null)
        {
            sb.AppendLine("top_merged_abs_deltas:");
            foreach (var row in result.Merged.Rows.OrderByDescending(row => row.AbsoluteDelta).Take(10))
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  [{row.Index:D2}] runtime={row.RuntimeValue:F6} current={row.CurrentValue:F6} delta={row.Delta:+0.000000;-0.000000;0.000000}");
            }
        }

        if (result.RaceMatches is { Count: > 0 })
        {
            sb.AppendLine("best_race_matches:");
            foreach (var match in result.RaceMatches.Take(5))
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  0x{match.RaceFormId:X8} {match.EditorId ?? string.Empty} sex={match.CandidateSex} relation={match.RelationToRuntimeRace} mae={match.Comparison.MeanAbsoluteDelta:F6} max={match.Comparison.MaxAbsoluteDelta:F6}");
            }

            sb.AppendLine("best_fitted_race_matches:");
            foreach (var match in result.RaceMatches.OrderBy(match => match.Fit.Comparison.MeanAbsoluteDelta).Take(5))
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  0x{match.RaceFormId:X8} {match.EditorId ?? string.Empty} sex={match.CandidateSex} relation={match.RelationToRuntimeRace} scale={match.Fit.Scale:F6} fitted_mae={match.Fit.Comparison.MeanAbsoluteDelta:F6} fitted_max={match.Fit.Comparison.MaxAbsoluteDelta:F6}");
            }
        }

        return sb.ToString();
    }

    private static RuntimeFaceGenProbeScaledFit FitUniformScale(
        float[] runtimeValues,
        float[] candidateValues)
    {
        var count = Math.Min(runtimeValues.Length, candidateValues.Length);
        if (count == 0)
        {
            return new RuntimeFaceGenProbeScaledFit(
                0d,
                new RuntimeFaceGenProbeArrayComparison(
                    "race_candidate_scaled",
                    runtimeValues.Length,
                    candidateValues.Length,
                    0,
                    0d,
                    0d,
                    0d,
                    0d,
                    0,
                    []));
        }

        double numerator = 0d;
        double denominator = 0d;
        for (var index = 0; index < count; index++)
        {
            numerator += candidateValues[index] * runtimeValues[index];
            denominator += candidateValues[index] * candidateValues[index];
        }

        var scale = denominator <= 0d ? 0d : numerator / denominator;
        var scaledValues = new float[count];
        for (var index = 0; index < count; index++)
        {
            scaledValues[index] = (float)(candidateValues[index] * scale);
        }

        return new RuntimeFaceGenProbeScaledFit(
            scale,
            CompareArray("race_candidate_scaled", runtimeValues, scaledValues));
    }

    private static void AppendSummaryBlock(StringBuilder sb, RuntimeFaceGenProbeArrayComparison? comparison)
    {
        if (comparison == null)
        {
            return;
        }

        sb.AppendLine($"{comparison.Label}_runtime_count={comparison.RuntimeCount}");
        sb.AppendLine($"{comparison.Label}_current_count={comparison.CurrentCount}");
        sb.AppendLine($"{comparison.Label}_compared_count={comparison.ComparedCount}");
        sb.AppendLine($"{comparison.Label}_mean_abs_delta={comparison.MeanAbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"{comparison.Label}_rmse_delta={comparison.RootMeanSquareDelta.ToString("F6", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"{comparison.Label}_max_abs_delta={comparison.MaxAbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"{comparison.Label}_mean_signed_delta={comparison.MeanSignedDelta.ToString("F6", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"{comparison.Label}_exact_match_count={comparison.ExactMatchCount}");
    }

    private static void AppendArraySummary(
        StringBuilder sb,
        RuntimeFaceGenProbeArrayComparison? comparison,
        bool isLast = false)
    {
        if (comparison == null)
        {
            sb.Append("0,0,,,,");
            if (isLast)
            {
                sb.AppendLine();
            }
            else
            {
                sb.Append(',');
            }
            return;
        }

        sb.Append(comparison.RuntimeCount.ToString(CultureInfo.InvariantCulture)).Append(',');
        sb.Append(comparison.CurrentCount.ToString(CultureInfo.InvariantCulture)).Append(',');
        sb.Append(comparison.MeanAbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(comparison.RootMeanSquareDelta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(comparison.MaxAbsoluteDelta.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(comparison.MeanSignedDelta.ToString("F6", CultureInfo.InvariantCulture));

        if (isLast)
        {
            sb.AppendLine();
        }
        else
        {
            sb.Append(',');
        }
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}

internal sealed record RuntimeFaceGenProbeCapture
{
    public required string CaptureDirectory { get; init; }
    public required string CaptureDirectoryName { get; init; }
    public required uint BaseNpcFormId { get; init; }
    public required uint RaceFormId { get; init; }
    public required bool IsFemale { get; init; }
    public string? MatchedFormType { get; init; }
    public required RuntimeFaceGenProbeDescriptor NpcTextureDescriptor { get; init; }
    public required RuntimeFaceGenProbeDescriptor RaceTextureDescriptor { get; init; }
    public required float[] NpcTextureCoefficients { get; init; }
    public required float[] RaceTextureCoefficients { get; init; }
    public required float[] MergedTextureCoefficients { get; init; }
}

internal sealed record RuntimeFaceGenProbeDescriptor(
    string? DescriptorAddress,
    string? ValuesPointer,
    uint Count,
    uint Stride,
    bool Valid);

internal sealed record RuntimeFaceGenProbeArrayComparisonRow(
    int Index,
    float RuntimeValue,
    float CurrentValue,
    double Delta,
    double AbsoluteDelta);

internal sealed record RuntimeFaceGenProbeArrayComparison(
    string Label,
    int RuntimeCount,
    int CurrentCount,
    int ComparedCount,
    double MeanAbsoluteDelta,
    double RootMeanSquareDelta,
    double MaxAbsoluteDelta,
    double MeanSignedDelta,
    int ExactMatchCount,
    IReadOnlyList<RuntimeFaceGenProbeArrayComparisonRow> Rows);

internal sealed record RuntimeFaceGenProbeRaceMatch(
    uint RaceFormId,
    string? EditorId,
    string CandidateSex,
    string RelationToRuntimeRace,
    RuntimeFaceGenProbeArrayComparison Comparison,
    RuntimeFaceGenProbeScaledFit Fit);

internal sealed record RuntimeFaceGenProbeScaledFit(
    double Scale,
    RuntimeFaceGenProbeArrayComparison Comparison);

internal sealed record RuntimeFaceGenProbeComparisonResult
{
    public required string CaptureDirectory { get; init; }
    public required string CaptureDirectoryName { get; init; }
    public required uint FormId { get; init; }
    public required uint RaceFormId { get; init; }
    public required bool IsFemale { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public RuntimeFaceGenProbeArrayComparison? Npc { get; init; }
    public RuntimeFaceGenProbeArrayComparison? Race { get; init; }
    public RuntimeFaceGenProbeArrayComparison? Merged { get; init; }
    public IReadOnlyList<RuntimeFaceGenProbeRaceMatch>? RaceMatches { get; init; }
    public string? FailureReason { get; init; }

    public bool Compared => string.IsNullOrWhiteSpace(FailureReason) && Merged != null;
}
