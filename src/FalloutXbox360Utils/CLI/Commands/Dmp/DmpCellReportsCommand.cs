using System.CommandLine;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Generate per-worldspace cell reports for one or more DMP files. Each worldspace's
///     cells are written to a separate file; interior cells (no worldspace link) go in a
///     dedicated <c>cell_report_Interior.txt</c>.
/// </summary>
internal static class DmpCellReportsCommand
{
    public static Command Create()
    {
        var command = new Command("cell-reports",
            "Generate per-worldspace cell reports for DMP files (each worldspace -> its own file)");

        var pathArg = new Argument<string>("path")
        {
            Description = "Path to a .dmp file or a directory containing .dmp files"
        };
        var outputOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output directory (default: TestOutput/dmp_cell_reports/)"
        };

        command.Arguments.Add(pathArg);
        command.Options.Add(outputOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathArg)!;
            var output = parseResult.GetValue(outputOpt)
                         ?? Path.Combine("TestOutput", "dmp_cell_reports");
            await RunAsync(path, output, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(string path, string outputDir, CancellationToken cancellationToken)
    {
        List<string> dmpFiles;
        if (Directory.Exists(path))
        {
            dmpFiles = Directory.GetFiles(path, "*.dmp")
                .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        else if (File.Exists(path))
        {
            dmpFiles = [path];
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Path not found: {Markup.Escape(path)}");
            return;
        }

        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found in:[/] {Markup.Escape(path)}");
            return;
        }

        Directory.CreateDirectory(outputDir);

        AnsiConsole.MarkupLine(
            $"[blue]Generating cell reports for {dmpFiles.Count} DMP file(s) -> " +
            $"{Markup.Escape(outputDir)}[/]");
        AnsiConsole.WriteLine();

        var dmpsProcessed = 0;
        var totalFilesWritten = 0;
        foreach (var dmpFile in dmpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(dmpFile);
            AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(fileName)}[/]");

            try
            {
                using var loaded = await SemanticFileLoader.LoadAsync(
                    dmpFile,
                    new SemanticFileLoadOptions { FileType = AnalysisFileType.Minidump },
                    cancellationToken);

                var cells = loaded.Records.Cells;
                if (cells.Count == 0)
                {
                    AnsiConsole.MarkupLine("  [dim]no cells in this DMP, skipping[/]");
                    continue;
                }

                var reports = GeckWorldWriter.GenerateCellsReportsByWorldspace(cells, loaded.Resolver);
                if (reports.Count == 0)
                {
                    AnsiConsole.MarkupLine("  [dim]no cell reports produced, skipping[/]");
                    continue;
                }

                var dmpStem = Path.GetFileNameWithoutExtension(dmpFile);
                var dmpOutDir = Path.Combine(outputDir, dmpStem);
                Directory.CreateDirectory(dmpOutDir);

                foreach (var (label, content) in reports.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
                {
                    var safeLabel = SanitizeFilenameComponent(label);
                    var outFile = Path.Combine(dmpOutDir, $"cell_report_{safeLabel}.txt");
                    await File.WriteAllTextAsync(outFile, content, cancellationToken);
                    totalFilesWritten++;
                    AnsiConsole.MarkupLine(
                        $"  -> {Markup.Escape(Path.GetFileName(outFile))} " +
                        $"({content.Length:N0} bytes)");
                }

                dmpsProcessed++;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]ERROR: {Markup.Escape(ex.Message)}[/]");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[green]Wrote {totalFilesWritten} report(s) across {dmpsProcessed} DMP(s) " +
            $"to {Markup.Escape(outputDir)}[/]");
    }

    private static string SanitizeFilenameComponent(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            chars[i] = Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c;
        }

        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
    }
}
