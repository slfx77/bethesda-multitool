using System.CommandLine;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Semantic;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Esm;

internal static class EsmReportsCommand
{
    internal static Command Create()
    {
        var command = new Command("reports", "Generate GECK-style reports from a plugin or explicit load order");
        var input = new Argument<string>("esm-input") { Description = "Path to ESM/ESP file" };
        var output = new Option<string?>("-o", "--output")
        {
            Description = "Output directory (default: ./esm_reports/); load-order reports require an empty directory"
        };
        var loadOrder = LoadOrderOptions.CreateOption();
        var allowMissing = LoadOrderOptions.CreateAllowMissingMastersOption();
        command.Arguments.Add(input);
        command.Options.Add(output);
        command.Options.Add(loadOrder);
        command.Options.Add(allowMissing);
        command.SetAction(async (parse, cancellationToken) =>
        {
            try
            {
                var sourcePath = Path.GetFullPath(parse.GetValue(input)!);
                var outputDirectory = Path.GetFullPath(parse.GetValue(output) ?? "./esm_reports");
                var specifications = parse.GetValue(loadOrder) ?? [];
                Dictionary<string, string> reports;
                if (specifications.Length > 0)
                {
                    // A stale report from an earlier source (for example dialogue_tree.txt) must not
                    // appear to belong to this explicit order. Never delete an existing directory.
                    if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                    {
                        throw new IOException("Load-order reports require an empty output directory to avoid mixing source namespaces.");
                    }
                    var order = PluginLoadOrder.Open(LoadOrderOptions.ResolvePaths(sourcePath, specifications),
                        parse.GetValue(allowMissing));
                    AnsiConsole.MarkupLine($"[blue]Loading {order.Entries.Count} plugins in the supplied order...[/]");
                    var view = await LoadOrderReportView.LoadAsync(order, cancellationToken);
                    reports = LoadOrderReportWriter.Generate(view, CliConsoles.ToolVersion);
                    AnsiConsole.MarkupLine("[yellow]Static winning-record reports; see report_sources.json for exclusions and namespace details.[/]");
                }
                else
                {
                    if (parse.GetValue(allowMissing))
                    {
                        throw new ArgumentException("--allow-missing-masters requires --load-order.");
                    }
                    if (File.Exists(Path.Combine(outputDirectory, "report_sources.json")) ||
                        File.Exists(Path.Combine(outputDirectory, "record_provenance.csv")))
                    {
                        throw new IOException("Single-file reports cannot overwrite a load-order report directory; choose a separate output directory.");
                    }
                    using var loaded = await CliSemanticLoader.TryLoadAsync(sourcePath, "Parsing records...",
                        new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile }, cancellationToken);
                    if (loaded == null) { return 1; }
                    reports = GeckReportGenerator.GenerateAllReports(new ReportDataSources(
                        loaded.Records, loaded.RawResult.FormIdMap,
                        loaded.RawResult.EsmRecords?.AssetStrings, loaded.RawResult.EsmRecords?.RuntimeEditorIds));
                }

                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(outputDirectory);
                foreach (var (filename, content) in reports)
                {
                    await File.WriteAllTextAsync(Path.Combine(outputDirectory, filename), content, cancellationToken);
                }
                AnsiConsole.MarkupLine($"[green]Generated {reports.Count} reports to {Markup.Escape(outputDirectory)}.[/]");
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
            catch (Exception exception)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(exception.Message)}");
                return 1;
            }
        });
        return command;
    }
}
