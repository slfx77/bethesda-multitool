using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Recovery;
using BethesdaMultitool.Core.RuntimeBuffer;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

internal static class DmpDialogueRecoveryCommand
{
    public static Command Create()
    {
        var command = new Command("dialogue-recovery", "Check selected INFO recovery across captures");
        var path = new Argument<string>("path") { Description = "A .dmp file or directory of captures" };
        var ids = new Option<string>("--formids") { Required = true, Description = "Comma-separated INFO FormIDs in hexadecimal" };
        var output = new Option<string>("-o", "--output") { Required = true, Description = "New or empty output directory; existing evidence is never overwritten" };
        var recursive = new Option<bool>("-r", "--recursive") { Description = "Include capture subdirectories" };
        var ownership = new Option<bool>("--ownership") { Description = "Also run string ownership analysis and link candidates (additional analysis cost)" };
        command.Arguments.Add(path);
        command.Options.Add(ids);
        command.Options.Add(output);
        command.Options.Add(recursive);
        command.Options.Add(ownership);
        command.SetAction(async (result, ct) => await RunAsync(result.GetValue(path)!, result.GetValue(ids)!,
            result.GetValue(output)!, result.GetValue(recursive), result.GetValue(ownership), ct));
        return command;
    }

    internal static uint[] ParseTargets(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => uint.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) && id != 0
            ? id : throw new ArgumentException($"Invalid INFO FormID: {value}"))
        .Distinct().Order().ToArray();

    private static async Task<int> RunAsync(string input, string idText, string output, bool recursive,
        bool ownership, CancellationToken cancellationToken)
    {
        var targets = ParseTargets(idText);
        if (targets.Length == 0) throw new ArgumentException("At least one INFO FormID is required.");
        var sources = File.Exists(input) ? new[] { Path.GetFullPath(input) } : Directory.Exists(input)
            ? Directory.GetFiles(input, "*.dmp", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : throw new FileNotFoundException("Capture path does not exist.", input);
        if (sources.Length == 0 || sources.Any(p => !Path.GetExtension(p).Equals(".dmp", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Input must contain .dmp captures.");
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Output directory contains existing evidence; choose a new or empty directory.");
        Directory.CreateDirectory(output);
        var toolPath = typeof(DmpDialogueRecoveryCommand).Assembly.Location;
        var toolSha = await HashAsync(toolPath, cancellationToken);
        var started = DateTimeOffset.UtcNow;
        var reports = new List<SourceReport>();
        var status = "running";
        async Task SaveManifest() => await File.WriteAllTextAsync(Path.Combine(output, "corpus.json"),
            JsonSerializer.Serialize(new CorpusManifest(started, DateTimeOffset.UtcNow, status, toolPath, toolSha,
                targets, ownership, sources, reports,
                "Captures are independent snapshots. Prompts/candidates are not NPC responses; absence is bounded to checked mappings."),
                DialogueRecoveryJsonContext.Default.CorpusManifest),
            CancellationToken.None);
        await SaveManifest();
        try
        {
            for (var index = 0; index < sources.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                AnsiConsole.MarkupLine($"[cyan][[{index + 1}/{sources.Length}]][/] {Markup.Escape(source)}");
                var events = new List<AnalysisStageEvent>();
                var stages = new AnalysisStages(cancellationToken, progress =>
                {
                    events.Add(progress);
                    AnsiConsole.WriteLine($"  {progress.Stage}: {progress.Status} ({progress.ElapsedSeconds:F1}s; {progress.Completed}/{progress.Total?.ToString() ?? "?"})");
                });
                string? sha = null;
                SourceReport report;
                try
                {
                    sha = await HashAsync(source, cancellationToken);
                    var length = new FileInfo(source).Length;
                    var analyzer = new MinidumpAnalyzer();
                    var analyzed = await CliProgressRunner.RunWithProgressAsync("Analyzing capture...",
                        (progress, ct) => analyzer.AnalyzeAsync(source, progress, true, false, ct), cancellationToken);
                    if (analyzed.MinidumpInfo is not { IsValid: true } || analyzed.EsmRecords == null)
                        throw new InvalidDataException("No valid minidump/ESM analysis was produced.");
                    UnifiedAnalysisResult? semantic = null;
                    try
                    {
                        stages.Run("parse-records", _ =>
                        {
                            semantic = SemanticFileLoader.LoadFromAnalysisResult(
                                source, analyzed, AnalysisFileType.Minidump,
                                new SemanticFileLoadOptions
                                {
                                    RetainParserContext = true,
                                    // Recovery reports describe this capture without introducing corpus authority.
                                    ApplyDefaultCellWorldspaceAuthority = false
                                }, cancellationToken);
                        });
                        var parsed = semantic!.Records;
                        var inspector = new DialogueProvenanceInspector(semantic.ParserContext!, parsed.Dialogues);
                        var strings = ownership ? RuntimeStringReportHelper.Extract(analyzed, semantic.Accessor!, stages: stages) : null;
                        var rows = stages.Run("targeted-info-recovery", _ => DialogueCorpusRecovery.BuildRows(targets, parsed.Dialogues,
                            info => inspector.InspectInfo(info, includeHex: true), strings?.OwnershipAnalysis, cancellationToken));
                        var unchanged = sha == await HashAsync(source, cancellationToken);
                        report = new SourceReport(source, sha, length, unchanged ? "completed" : "input-changed", unchanged,
                            rows, events, null);
                    }
                    finally
                    {
                        // AnalysisStages can observe cancellation after the loader returns its owned mapping.
                        semantic?.Dispose();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    report = new SourceReport(source, sha, null, "failed", null, [], events, error.Message);
                    AnsiConsole.MarkupLine($"[red]Capture failed:[/] {Markup.Escape(error.Message)}");
                }
                var reportName = $"{index + 1:D3}-{Path.GetFileNameWithoutExtension(source)}-{sha?[..12] ?? "unknown"}.json";
                await File.WriteAllTextAsync(Path.Combine(output, reportName), JsonSerializer.Serialize(report, DialogueRecoveryJsonContext.Default.SourceReport), cancellationToken);
                reports.Add(report with { ReportFile = reportName });
                await SaveManifest();
            }
            status = reports.All(r => r.Status == "completed") ? "completed" : "completed-with-errors";
            return status == "completed" ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = "cancelled";
            throw;
        }
        finally { await SaveManifest(); }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    internal sealed record CorpusManifest(DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, string Status,
        string ToolAssembly, string ToolSha256, uint[] Targets, bool OwnershipRequested,
        string[] PlannedSources, IReadOnlyList<SourceReport> Sources, string Boundary);

    internal sealed record SourceReport(string SourcePath, string? SourceSha256, long? SourceBytes, string Status,
        bool? SourceUnchanged, IReadOnlyList<DialogueRecoveryRow> Infos, IReadOnlyList<AnalysisStageEvent> Stages, string? Error)
    {
        public string? ReportFile { get; init; }
    }
}
