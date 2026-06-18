// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Bsa.Ba2;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Bsa;

/// <summary>
///     CLI command for BA2 archive operations (Fallout 4 / Fallout 76). Mirrors the
///     <see cref="BsaCommand" /> shape for the BA2 format: list / info / extract.
/// </summary>
public static class Ba2Command
{
    public static Command Create()
    {
        var ba2Command = new Command("ba2", "BA2 archive operations (Fallout 4 / Fallout 76)");
        ba2Command.Subcommands.Add(CreateListCommand());
        ba2Command.Subcommands.Add(CreateInfoCommand());
        ba2Command.Subcommands.Add(CreateExtractCommand());
        return ba2Command;
    }

    private static Command CreateInfoCommand()
    {
        var command = new Command("info", "Display BA2 archive information");
        var inputArg = new Argument<string>("input") { Description = "Path to BA2 file" };
        command.Arguments.Add(inputArg);
        command.SetAction((parseResult, _) =>
        {
            RunInfo(parseResult.GetValue(inputArg)!);
            return Task.CompletedTask;
        });
        return command;
    }

    private static Command CreateListCommand()
    {
        var command = new Command("list", "List files in a BA2 archive");
        var inputArg = new Argument<string>("input") { Description = "Path to BA2 file" };
        var filterOption = new Option<string?>("-f", "--filter")
            { Description = "Filter by extension (e.g., .nif, .dds)" };
        var pathOption = new Option<string?>("-d", "--path") { Description = "Filter by path substring" };
        command.Arguments.Add(inputArg);
        command.Options.Add(filterOption);
        command.Options.Add(pathOption);
        command.SetAction((parseResult, _) =>
        {
            RunList(parseResult.GetValue(inputArg)!, parseResult.GetValue(filterOption),
                parseResult.GetValue(pathOption));
            return Task.CompletedTask;
        });
        return command;
    }

    private static Command CreateExtractCommand()
    {
        var command = new Command("extract", "Extract files from a BA2 archive");
        var inputArg = new Argument<string>("input") { Description = "Path to BA2 file" };
        var outputOption = new Option<string>("-o", "--output")
            { Description = "Output directory", Required = true };
        var filterOption = new Option<string?>("-f", "--filter")
            { Description = "Filter by extension (e.g., .dds)" };
        var pathOption = new Option<string?>("-d", "--path") { Description = "Filter by path substring" };
        var overwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite existing files" };
        command.Arguments.Add(inputArg);
        command.Options.Add(outputOption);
        command.Options.Add(filterOption);
        command.Options.Add(pathOption);
        command.Options.Add(overwriteOption);
        command.SetAction(async (parseResult, ct) =>
        {
            await RunExtractAsync(
                parseResult.GetValue(inputArg)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(filterOption),
                parseResult.GetValue(pathOption),
                parseResult.GetValue(overwriteOption),
                ct);
        });
        return command;
    }

    private static void RunInfo(string input)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        try
        {
            using var extractor = new Ba2Extractor(input);
            var header = extractor.Archive.Header;

            AnsiConsole.MarkupLine("[bold cyan]BA2 Archive Info[/]");
            AnsiConsole.WriteLine();

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("Property");
            table.AddColumn("Value");
            table.AddRow("File", Path.GetFileName(input));
            table.AddRow("Magic", FalloutXbox360Utils.Core.Formats.Bsa.Ba2.Ba2Header.Magic);
            table.AddRow("Version", header.Version.ToString());
            table.AddRow("Type", $"{header.TypeTag} ({header.Type})");
            table.AddRow("Files", header.FileCount.ToString("N0"));
            table.AddRow("Compression", header.CompressionFormat.ToString());
            table.AddRow("Name Table", header.HasNameTable ? "[green]Yes[/]" : "No");
            AnsiConsole.Write(table);

            var extStats = extractor.GetExtensionStats();
            if (extStats.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]File Extensions:[/]");
                var extTable = new Table { Border = TableBorder.Simple };
                extTable.AddColumn("Extension");
                extTable.AddColumn(new TableColumn("Count").RightAligned());
                foreach (var (ext, count) in extStats.Take(15))
                {
                    extTable.AddRow(ext, count.ToString("N0"));
                }

                if (extStats.Count > 15)
                {
                    extTable.AddRow("...", $"({extStats.Count - 15} more)");
                }

                AnsiConsole.Write(extTable);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error parsing BA2:[/] {0}", ex.Message);
        }
    }

    private static void RunList(string input, string? filter, string? pathFilter)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        try
        {
            var archive = Ba2Parser.Parse(input);
            var files = FilterFiles(archive.AllFiles, filter, pathFilter).ToList();

            AnsiConsole.MarkupLine("[bold cyan]BA2 Files[/] ({0:N0} of {1:N0})", files.Count, archive.TotalFiles);
            foreach (var file in files.Take(2000))
            {
                AnsiConsole.WriteLine(file.FullPath);
            }

            if (files.Count > 2000)
            {
                AnsiConsole.MarkupLine("[grey]... ({0:N0} more — narrow with --filter / --path)[/]", files.Count - 2000);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error parsing BA2:[/] {0}", ex.Message);
        }
    }

    private static async Task RunExtractAsync(
        string input, string output, string? filter, string? pathFilter, bool overwrite, CancellationToken ct)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        try
        {
            using var extractor = new Ba2Extractor(input);
            var files = FilterFiles(extractor.Archive.AllFiles, filter, pathFilter).ToList();
            if (files.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No files matched the filter.[/]");
                return;
            }

            Directory.CreateDirectory(output);
            var extracted = 0;
            var failed = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await extractor.ExtractFileToDiskAsync(file, output, overwrite).ConfigureAwait(false);
                    extracted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    AnsiConsole.MarkupLine("[red]Failed:[/] {0} ({1})", file.FullPath, ex.Message);
                }
            }

            AnsiConsole.MarkupLine("[green]Extracted[/] {0:N0} file(s) to {1}{2}",
                extracted, output, failed > 0 ? $" ([red]{failed} failed[/])" : string.Empty);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error extracting BA2:[/] {0}", ex.Message);
        }
    }

    private static IEnumerable<Ba2FileRecord> FilterFiles(
        IEnumerable<Ba2FileRecord> files, string? filter, string? pathFilter)
    {
        if (!string.IsNullOrEmpty(filter))
        {
            var ext = filter.StartsWith('.') ? filter : $".{filter}";
            files = files.Where(f => f.FullPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(pathFilter))
        {
            files = files.Where(f => f.FullPath.Contains(pathFilter, StringComparison.OrdinalIgnoreCase));
        }

        return files;
    }
}
