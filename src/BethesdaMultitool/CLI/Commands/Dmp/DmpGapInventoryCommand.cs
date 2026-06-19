using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Recovery;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     Inventories uncovered minidump regions using the shared recoverable-gap scanner.
/// </summary>
public static class DmpGapInventoryCommand
{
    private const int DefaultScanBytes = 64 * 1024;
    private const int DefaultTopPerDump = 250;

    public static Command Create()
    {
        var inputArg = new Argument<string>("path")
        {
            Description = "Path to a .dmp file or directory containing .dmp files"
        };
        var outputOpt = new Option<string>("-o", "--output")
        {
            Description = "Output directory",
            DefaultValueFactory = _ => Path.Combine("artifacts", "gap-inventory")
        };
        var recursiveOpt = new Option<bool>("-r", "--recursive")
        {
            Description = "Search input directory recursively"
        };
        var topOpt = new Option<int>("--top")
        {
            Description = "Maximum candidates to write per dump; use 0 for all candidates",
            DefaultValueFactory = _ => DefaultTopPerDump
        };
        var minSizeOpt = new Option<int>("--min-size")
        {
            Description = "Minimum gap size to scan",
            DefaultValueFactory = _ => 32
        };
        var scanBytesOpt = new Option<int>("--scan-bytes")
        {
            Description = "Maximum bytes to scan per gap",
            DefaultValueFactory = _ => DefaultScanBytes
        };
        var fastOpt = new Option<bool>("--fast")
        {
            Description = "Use lightweight DMP analysis before coverage; faster but less faithful to GUI coverage"
        };

        var command = new Command(
            "gap-inventory",
            "Report validated recoverable data in uncovered DMP regions");
        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(recursiveOpt);
        command.Options.Add(topOpt);
        command.Options.Add(minSizeOpt);
        command.Options.Add(scanBytesOpt);
        command.Options.Add(fastOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            var recursive = parseResult.GetValue(recursiveOpt);
            var top = parseResult.GetValue(topOpt);
            var minSize = parseResult.GetValue(minSizeOpt);
            var scanBytes = parseResult.GetValue(scanBytesOpt);
            var fast = parseResult.GetValue(fastOpt);
            await ExecuteAsync(input, output, recursive, top, minSize, scanBytes, fast, cancellationToken);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string input,
        string outputDir,
        bool recursive,
        int topPerDump,
        int minSize,
        int maxScanBytes,
        bool fast,
        CancellationToken cancellationToken)
    {
        var dumps = DiscoverDumps(input, recursive);
        if (dumps.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found at:[/] {Markup.Escape(input)}");
            return;
        }

        Directory.CreateDirectory(outputDir);
        var summaryPath = Path.Combine(outputDir, "gap_inventory_summary.csv");
        var candidatesPath = Path.Combine(outputDir, "gap_inventory_candidates.csv");
        var markdownPath = Path.Combine(outputDir, "gap_inventory_top.md");

        var summaryRows = new List<DumpInventorySummary>();
        var candidateRows = new List<GapInventoryRow>();

        AnsiConsole.MarkupLine(
            $"[blue]Gap inventory:[/] {dumps.Count:N0} DMP(s), output -> {Markup.Escape(outputDir)}");

        var analyzer = new MinidumpAnalyzer();
        for (var i = 0; i < dumps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dump = dumps[i];
            var fileName = Path.GetFileName(dump);
            AnsiConsole.MarkupLine($"[cyan][[{i + 1}/{dumps.Count}]][/] {Markup.Escape(fileName)}");

            try
            {
                var result = await AnalyzeDumpAsync(analyzer, dump, fast, cancellationToken);
                using var mmf = MemoryMappedFile.CreateFromFile(
                    dump, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read);
                using var stream = File.OpenRead(dump);
                var rttiReader = result.MinidumpInfo is { IsValid: true }
                    ? new RttiReader(result.MinidumpInfo, stream)
                    : null;

                var coverage = CoverageAnalyzer.Analyze(result, accessor);
                if (coverage.Error != null)
                {
                    AnsiConsole.MarkupLine(
                        $"  [red]coverage failed:[/] {Markup.Escape(coverage.Error)}");
                    summaryRows.Add(DumpInventorySummary.Failed(fileName, coverage.Error));
                    continue;
                }

                var recovery = DmpGapRecoveryScanner.Scan(
                    result,
                    coverage,
                    new MmfMemoryAccessor(accessor),
                    rttiReader,
                    new DmpGapRecoveryOptions
                    {
                        DiscoverCandidates = true,
                        MinGapSize = Math.Max(0, minSize),
                        MaxScanBytesPerGap = Math.Max(1024, maxScanBytes)
                    });

                var inventory = BuildInventory(fileName, coverage, recovery, topPerDump);
                summaryRows.Add(inventory.Summary);
                candidateRows.AddRange(inventory.Candidates);

                AnsiConsole.MarkupLine(
                    $"  [green]{inventory.Candidates.Count:N0}[/] candidate(s); " +
                    $"raw={recovery.Summary.RawRecordCandidates:N0}, " +
                    $"tesform={recovery.Summary.RuntimeTesFormCandidates:N0}, " +
                    $"dialogue={recovery.Summary.RuntimeDialogueCandidates:N0}, " +
                    $"placed={recovery.Summary.RuntimePlacedReferenceCandidates:N0}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]failed:[/] {Markup.Escape(ex.Message)}");
                summaryRows.Add(DumpInventorySummary.Failed(fileName, ex.Message));
            }
        }

        await File.WriteAllTextAsync(summaryPath, WriteSummaryCsv(summaryRows), cancellationToken);
        await File.WriteAllTextAsync(candidatesPath, WriteCandidatesCsv(candidateRows), cancellationToken);
        await File.WriteAllTextAsync(markdownPath, WriteMarkdown(summaryRows, candidateRows), cancellationToken);

        AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(summaryPath)}");
        AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(candidatesPath)}");
        AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(markdownPath)}");
    }

    private static async Task<AnalysisResult> AnalyzeDumpAsync(
        MinidumpAnalyzer analyzer,
        string dump,
        bool fast,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(dump);
        using var mmf = MemoryMappedFile.CreateFromFile(dump, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        return await analyzer.AnalyzeAsync(
            dump,
            accessor,
            fileInfo.Length,
            includeMetadata: !fast,
            cancellationToken: cancellationToken);
    }

    private static DumpInventory BuildInventory(
        string dumpName,
        CoverageResult coverage,
        DmpGapRecoveryResult recovery,
        int topPerDump)
    {
        var candidates = recovery.Candidates
            .Select(c => GapInventoryRow.FromCandidate(dumpName, c))
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.FileOffset)
            .ToList();

        if (topPerDump > 0)
        {
            candidates = candidates.Take(topPerDump).ToList();
        }

        var summary = new DumpInventorySummary
        {
            Dump = dumpName,
            FileSize = coverage.FileSize,
            TotalRegionBytes = coverage.TotalRegionBytes,
            RecognizedBytes = coverage.TotalRecognizedBytes,
            GapBytes = coverage.TotalGapBytes,
            GapCount = coverage.Gaps.Count,
            RecognizedPercent = coverage.RecognizedPercent,
            GapPercent = coverage.GapPercent,
            ScannedGaps = recovery.Summary.ScannedGaps,
            DirectEvidenceGaps = recovery.Summary.DirectEvidenceGaps,
            RawRecordGaps = recovery.Summary.RawRecordGaps,
            TesFormGaps = recovery.Summary.TesFormGaps,
            RawRecordCandidates = recovery.Summary.RawRecordCandidates,
            RuntimeTesFormCandidates = recovery.Summary.RuntimeTesFormCandidates,
            RuntimeDialogueCandidates = recovery.Summary.RuntimeDialogueCandidates,
            RuntimePlacedReferenceCandidates = recovery.Summary.RuntimePlacedReferenceCandidates,
            DialogueTextHits = recovery.Summary.DialogueTextHits,
            EsmSignatureHits = recovery.Summary.EsmSignatureHits,
            TotalCandidates = recovery.Candidates.Count,
            ReportedCandidates = candidates.Count,
            MaxConfidence = recovery.Candidates.Count > 0
                ? recovery.Candidates.Max(c => c.Confidence)
                : 0,
            ZeroFillBytes = SumClassBytes(coverage, GapClassification.ZeroFill),
            AsciiTextBytes = SumClassBytes(coverage, GapClassification.AsciiText),
            StringPoolBytes = SumClassBytes(coverage, GapClassification.StringPool),
            PointerDenseBytes = SumClassBytes(coverage, GapClassification.PointerDense),
            AssetManagementBytes = SumClassBytes(coverage, GapClassification.AssetManagement),
            RecordSignatureBytes = SumClassBytes(coverage, GapClassification.RecordSignature),
            BinaryDataBytes = SumClassBytes(coverage, GapClassification.BinaryData)
        };

        return new DumpInventory(summary, candidates);
    }

    private static long SumClassBytes(CoverageResult coverage, GapClassification classification)
    {
        return coverage.Gaps
            .Where(g => g.Classification == classification)
            .Sum(g => g.Size);
    }

    private static List<string> DiscoverDumps(string input, bool recursive)
    {
        if (File.Exists(input))
        {
            return Path.GetExtension(input).Equals(".dmp", StringComparison.OrdinalIgnoreCase)
                ? [input]
                : [];
        }

        if (!Directory.Exists(input))
        {
            return [];
        }

        return Directory
            .GetFiles(input, "*.dmp", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string WriteSummaryCsv(List<DumpInventorySummary> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "dump,status,error,file_size,total_region_bytes,recognized_bytes,gap_bytes,recognized_percent,gap_percent,gap_count,scanned_gaps,total_candidates,reported_candidates,direct_evidence_gaps,raw_record_gaps,tesform_gaps,raw_record_candidates,runtime_tesform_candidates,runtime_dialogue_candidates,runtime_placed_reference_candidates,esm_signature_hits,dialogue_text_hits,max_confidence,zerofill_bytes,asciitext_bytes,stringpool_bytes,pointerdense_bytes,assetmanagement_bytes,recordsignature_bytes,binarydata_bytes");

        foreach (var row in rows)
        {
            AppendCsvLine(
                sb,
                Csv(row.Dump),
                Csv(row.Status),
                Csv(row.Error),
                row.FileSize.ToString(),
                row.TotalRegionBytes.ToString(),
                row.RecognizedBytes.ToString(),
                row.GapBytes.ToString(),
                row.RecognizedPercent.ToString("F2"),
                row.GapPercent.ToString("F2"),
                row.GapCount.ToString(),
                row.ScannedGaps.ToString(),
                row.TotalCandidates.ToString(),
                row.ReportedCandidates.ToString(),
                row.DirectEvidenceGaps.ToString(),
                row.RawRecordGaps.ToString(),
                row.TesFormGaps.ToString(),
                row.RawRecordCandidates.ToString(),
                row.RuntimeTesFormCandidates.ToString(),
                row.RuntimeDialogueCandidates.ToString(),
                row.RuntimePlacedReferenceCandidates.ToString(),
                row.EsmSignatureHits.ToString(),
                row.DialogueTextHits.ToString(),
                row.MaxConfidence.ToString(),
                row.ZeroFillBytes.ToString(),
                row.AsciiTextBytes.ToString(),
                row.StringPoolBytes.ToString(),
                row.PointerDenseBytes.ToString(),
                row.AssetManagementBytes.ToString(),
                row.RecordSignatureBytes.ToString(),
                row.BinaryDataBytes.ToString());
        }

        return sb.ToString();
    }

    private static string WriteCandidatesCsv(List<GapInventoryRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "dump,kind,record_type,form_type,class_name,form_id,file_offset,virtual_address,length,confidence,disposition,gap_offset,gap_size,gap_classification,gap_context,evidence,decoded_text,topic_form_id,quest_form_id");

        foreach (var row in rows
                     .OrderBy(r => r.Dump, StringComparer.OrdinalIgnoreCase)
                     .ThenByDescending(r => r.Confidence)
                     .ThenBy(r => r.FileOffset))
        {
            AppendCsvLine(
                sb,
                Csv(row.Dump),
                Csv(row.Kind),
                Csv(row.RecordType),
                Csv(row.FormType),
                Csv(row.ClassName),
                Csv(row.FormId),
                FormatHex(row.FileOffset),
                row.VirtualAddress.HasValue ? FormatHex(row.VirtualAddress.Value) : "",
                row.Length.ToString(),
                row.Confidence.ToString(),
                Csv(row.Disposition),
                FormatHex(row.GapOffset),
                row.GapSize.ToString(),
                Csv(row.GapClassification),
                Csv(row.GapContext),
                Csv(row.Evidence),
                Csv(row.DecodedText),
                Csv(row.TopicFormId),
                Csv(row.QuestFormId));
        }

        return sb.ToString();
    }

    private static string WriteMarkdown(
        List<DumpInventorySummary> summaries,
        List<GapInventoryRow> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DMP Gap Inventory");
        sb.AppendLine();
        sb.AppendLine("| Dump | Gap % | Gaps | Candidates | Raw | Runtime | Dialogue | Placed |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in summaries.OrderBy(r => r.Dump, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(
                $"| {EscapeMd(row.Dump)} | {row.GapPercent:F1}% | {row.GapCount:N0} | " +
                $"{row.TotalCandidates:N0} | {row.RawRecordCandidates:N0} | " +
                $"{row.RuntimeTesFormCandidates:N0} | {row.RuntimeDialogueCandidates:N0} | " +
                $"{row.RuntimePlacedReferenceCandidates:N0} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Top Candidates");
        sb.AppendLine();
        sb.AppendLine("| Dump | Kind | Record | Form ID | VA | Length | Confidence | Disposition | Evidence |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---|---|");
        foreach (var row in candidates
                     .OrderByDescending(r => r.Confidence)
                     .ThenBy(r => r.FileOffset)
                     .Take(100))
        {
            var va = row.VirtualAddress.HasValue ? FormatHex(row.VirtualAddress.Value) : "";
            sb.AppendLine(
                $"| {EscapeMd(row.Dump)} | {EscapeMd(row.Kind)} | {EscapeMd(row.RecordType)} | " +
                $"{EscapeMd(row.FormId)} | {va} | {row.Length:N0} | {row.Confidence} | " +
                $"{EscapeMd(row.Disposition)} | {EscapeMd(Truncate(row.Evidence, 140))} |");
        }

        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        value = value.Replace("\r", " ").Replace("\n", " ");
        if (!value.Contains(',') && !value.Contains('"'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static void AppendCsvLine(StringBuilder sb, params string[] fields)
    {
        sb.AppendLine(string.Join(',', fields));
    }

    private static string FormatHex(long value)
    {
        return $"0x{value:X8}";
    }

    private static string FormatHex(uint value)
    {
        return $"0x{value:X8}";
    }

    private static string EscapeMd(string? value)
    {
        return string.IsNullOrEmpty(value)
            ? ""
            : value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value ?? "";
        }

        return value[..max];
    }

    private sealed record DumpInventory(DumpInventorySummary Summary, List<GapInventoryRow> Candidates);

    private sealed class DumpInventorySummary
    {
        public string Dump { get; init; } = "";
        public string Status { get; init; } = "ok";
        public string Error { get; init; } = "";
        public long FileSize { get; init; }
        public long TotalRegionBytes { get; init; }
        public long RecognizedBytes { get; init; }
        public long GapBytes { get; init; }
        public double RecognizedPercent { get; init; }
        public double GapPercent { get; init; }
        public int GapCount { get; init; }
        public int ScannedGaps { get; init; }
        public int TotalCandidates { get; init; }
        public int ReportedCandidates { get; init; }
        public int DirectEvidenceGaps { get; init; }
        public int RawRecordGaps { get; init; }
        public int TesFormGaps { get; init; }
        public int RawRecordCandidates { get; init; }
        public int RuntimeTesFormCandidates { get; init; }
        public int RuntimeDialogueCandidates { get; init; }
        public int RuntimePlacedReferenceCandidates { get; init; }
        public int EsmSignatureHits { get; init; }
        public int DialogueTextHits { get; init; }
        public int MaxConfidence { get; init; }
        public long ZeroFillBytes { get; init; }
        public long AsciiTextBytes { get; init; }
        public long StringPoolBytes { get; init; }
        public long PointerDenseBytes { get; init; }
        public long AssetManagementBytes { get; init; }
        public long RecordSignatureBytes { get; init; }
        public long BinaryDataBytes { get; init; }

        public static DumpInventorySummary Failed(string dump, string error)
        {
            return new DumpInventorySummary
            {
                Dump = dump,
                Status = "failed",
                Error = error
            };
        }
    }

    private sealed class GapInventoryRow
    {
        public string Dump { get; init; } = "";
        public string Kind { get; init; } = "";
        public string RecordType { get; init; } = "";
        public string FormType { get; init; } = "";
        public string ClassName { get; init; } = "";
        public string FormId { get; init; } = "";
        public long FileOffset { get; init; }
        public long? VirtualAddress { get; init; }
        public long Length { get; init; }
        public int Confidence { get; init; }
        public string Disposition { get; init; } = "";
        public long GapOffset { get; init; }
        public long GapSize { get; init; }
        public string GapClassification { get; init; } = "";
        public string GapContext { get; init; } = "";
        public string Evidence { get; init; } = "";
        public string DecodedText { get; init; } = "";
        public string TopicFormId { get; init; } = "";
        public string QuestFormId { get; init; } = "";

        public static GapInventoryRow FromCandidate(
            string dumpName,
            DmpGapRecoveryCandidate candidate)
        {
            return new GapInventoryRow
            {
                Dump = dumpName,
                Kind = candidate.Kind.ToString(),
                RecordType = candidate.RecordType ?? "",
                FormType = candidate.FormType.HasValue ? $"0x{candidate.FormType.Value:X2}" : "",
                ClassName = candidate.ClassName ?? "",
                FormId = candidate.FormId.HasValue ? FormatHex(candidate.FormId.Value) : "",
                FileOffset = candidate.FileOffset,
                VirtualAddress = candidate.VirtualAddress,
                Length = candidate.Length,
                Confidence = candidate.Confidence,
                Disposition = candidate.Disposition.ToString(),
                GapOffset = candidate.GapFileOffset,
                GapSize = candidate.GapSize,
                GapClassification = candidate.GapClassification,
                GapContext = candidate.GapContext,
                Evidence = candidate.Evidence,
                DecodedText = candidate.DecodedText ?? "",
                TopicFormId = candidate.TopicFormId.HasValue ? FormatHex(candidate.TopicFormId.Value) : "",
                QuestFormId = candidate.QuestFormId.HasValue ? FormatHex(candidate.QuestFormId.Value) : ""
            };
        }
    }
}
