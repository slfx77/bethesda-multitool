// Copyright (c) 2026 Xbox360MemoryCarver Contributors
// Licensed under the MIT License.

using System.CommandLine;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.Bsa;

namespace Xbox360MemoryCarver.CLI;

/// <summary>
///     CLI command for BSA archive operations.
/// </summary>
public static class BsaCommand
{
    public static Command Create()
    {
        var bsaCommand = new Command("bsa", "BSA archive operations");

        bsaCommand.Subcommands.Add(CreateListCommand());
        bsaCommand.Subcommands.Add(CreateExtractCommand());
        bsaCommand.Subcommands.Add(CreateInfoCommand());

        return bsaCommand;
    }

    private static Command CreateInfoCommand()
    {
        var command = new Command("info", "Display BSA archive information");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA file" };

        command.Arguments.Add(inputArg);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            RunInfo(input);
            return Task.CompletedTask;
        });

        return command;
    }

    private static Command CreateListCommand()
    {
        var command = new Command("list", "List files in a BSA archive");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA file" };
        var filterOption = new Option<string?>("-f", "--filter")
        { Description = "Filter by extension (e.g., .nif, .dds)" };
        var folderOption = new Option<string?>("-d", "--folder") { Description = "Filter by folder path" };

        command.Arguments.Add(inputArg);
        command.Options.Add(filterOption);
        command.Options.Add(folderOption);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var filter = parseResult.GetValue(filterOption);
            var folder = parseResult.GetValue(folderOption);
            RunList(input, filter, folder);
            return Task.CompletedTask;
        });

        return command;
    }

    private static Command CreateExtractCommand()
    {
        var command = new Command("extract", "Extract files from a BSA archive");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA file" };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory",
            Required = true
        };
        var filterOption = new Option<string?>("-f", "--filter")
        { Description = "Filter by extension (e.g., .nif, .dds)" };
        var folderOption = new Option<string?>("-d", "--folder") { Description = "Filter by folder path" };
        var overwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite existing files" };
        var convertOption = new Option<bool>("-c", "--convert")
        { Description = "Convert Xbox 360 formats to PC (DDX->DDS, XMA->OGG, NIF endian)" };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Verbose output" };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOption);
        command.Options.Add(filterOption);
        command.Options.Add(folderOption);
        command.Options.Add(overwriteOption);
        command.Options.Add(convertOption);
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOption)!;
            var filter = parseResult.GetValue(filterOption);
            var folder = parseResult.GetValue(folderOption);
            var overwrite = parseResult.GetValue(overwriteOption);
            var convert = parseResult.GetValue(convertOption);
            var verbose = parseResult.GetValue(verboseOption);
            await RunExtractAsync(input, output, filter, folder, overwrite, convert, verbose);
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
            var archive = BsaParser.Parse(input);

            AnsiConsole.MarkupLine("[bold cyan]BSA Archive Info[/]");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Property");
            table.AddColumn("Value");
            table.Border = TableBorder.Rounded;

            table.AddRow("File", Path.GetFileName(input));
            table.AddRow("Version", archive.Header.Version.ToString());
            table.AddRow("Platform", archive.Header.IsXbox360 ? "[yellow]Xbox 360[/]" : "[green]PC[/]");
            table.AddRow("Folders", archive.Header.FolderCount.ToString("N0"));
            table.AddRow("Files", archive.Header.FileCount.ToString("N0"));
            table.AddRow("Compressed", archive.Header.DefaultCompressed ? "[green]Yes[/]" : "No");
            table.AddRow("Embed Names", archive.Header.EmbedFileNames ? "Yes" : "No");

            // Archive flags
            var flags = new List<string>();
            if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.IncludeDirectoryNames))
            {
                flags.Add("DirNames");
            }

            if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.IncludeFileNames))
            {
                flags.Add("FileNames");
            }

            if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.CompressedArchive))
            {
                flags.Add("Compressed");
            }

            if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.Xbox360Archive))
            {
                flags.Add("Xbox360");
            }

            if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.EmbedFileNames))
            {
                flags.Add("EmbedNames");
            }

            if (archive.Header.ArchiveFlags.HasFlag(BsaArchiveFlags.XMemCodec))
            {
                flags.Add("XMem");
            }

            table.AddRow("Flags", string.Join(", ", flags));

            // File type flags
            var fileTypes = new List<string>();
            if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Meshes))
            {
                fileTypes.Add("Meshes");
            }

            if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Textures))
            {
                fileTypes.Add("Textures");
            }

            if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Menus))
            {
                fileTypes.Add("Menus");
            }

            if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Sounds))
            {
                fileTypes.Add("Sounds");
            }

            if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Voices))
            {
                fileTypes.Add("Voices");
            }

            if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Shaders))
            {
                fileTypes.Add("Shaders");
            }

            if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Trees))
            {
                fileTypes.Add("Trees");
            }

            if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Fonts))
            {
                fileTypes.Add("Fonts");
            }

            if (archive.Header.FileFlags.HasFlag(BsaFileFlags.Misc))
            {
                fileTypes.Add("Misc");
            }

            table.AddRow("Content Types", string.Join(", ", fileTypes));

            AnsiConsole.Write(table);

            // Extension statistics
            using var extractor = new BsaExtractor(input);
            var extStats = extractor.GetExtensionStats();

            if (extStats.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]File Extensions:[/]");

                var extTable = new Table();
                extTable.AddColumn("Extension");
                extTable.AddColumn(new TableColumn("Count").RightAligned());
                extTable.Border = TableBorder.Simple;

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

            // Folder statistics (top 10)
            var folderStats = extractor.GetFolderStats();
            if (folderStats.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Top Folders:[/]");

                var folderTable = new Table();
                folderTable.AddColumn("Folder");
                folderTable.AddColumn(new TableColumn("Files").RightAligned());
                folderTable.Border = TableBorder.Simple;

                foreach (var (folder, count) in folderStats.Take(10))
                {
                    folderTable.AddRow(folder, count.ToString("N0"));
                }

                if (folderStats.Count > 10)
                {
                    folderTable.AddRow("...", $"({folderStats.Count - 10} more)");
                }

                AnsiConsole.Write(folderTable);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error parsing BSA:[/] {0}", ex.Message);
        }
    }

    private static void RunList(string input, string? filter, string? folder)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        try
        {
            var archive = BsaParser.Parse(input);

            var files = archive.AllFiles.AsEnumerable();

            // Apply filters
            if (!string.IsNullOrEmpty(filter))
            {
                var ext = filter.StartsWith('.') ? filter : $".{filter}";
                files = files.Where(f => f.Name?.EndsWith(ext, StringComparison.OrdinalIgnoreCase) == true);
            }

            if (!string.IsNullOrEmpty(folder))
            {
                files = files.Where(f => f.Folder?.Name?.Contains(folder, StringComparison.OrdinalIgnoreCase) == true);
            }

            var fileList = files.ToList();

            AnsiConsole.MarkupLine("[cyan]BSA:[/] {0} ([yellow]{1}[/])", Path.GetFileName(input), archive.Platform);
            AnsiConsole.MarkupLine("[cyan]Files:[/] {0:N0} (of {1:N0} total)", fileList.Count, archive.TotalFiles);
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Path");
            table.AddColumn(new TableColumn("Size").RightAligned());
            table.AddColumn("Compressed");
            table.Border = TableBorder.Simple;

            var defaultCompressed = archive.Header.DefaultCompressed;

            foreach (var file in fileList.Take(100))
            {
                var isCompressed = defaultCompressed != file.CompressionToggle;
                var compressedStr = isCompressed ? "[green]Yes[/]" : "";
                table.AddRow(
                    file.FullPath,
                    FormatSize(file.Size),
                    compressedStr
                );
            }

            if (fileList.Count > 100)
            {
                table.AddRow($"... and {fileList.Count - 100:N0} more files", "", "");
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", ex.Message);
        }
    }

    private static async Task RunExtractAsync(string input, string output, string? filter, string? folder,
        bool overwrite, bool convert = false, bool verbose = false)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        try
        {
            using var extractor = new BsaExtractor(input);
            var archive = extractor.Archive;

            AnsiConsole.MarkupLine("[cyan]BSA:[/] {0} ([yellow]{1}[/])", Path.GetFileName(input), archive.Platform);
            AnsiConsole.MarkupLine("[cyan]Output:[/] {0}", output);

            // Initialize converters if requested
            if (convert)
            {
                var ddxAvailable = extractor.EnableDdxConversion(true, verbose);
                var xmaAvailable = extractor.EnableXmaConversion(true);
                var nifAvailable = extractor.EnableNifConversion(true, verbose);

                AnsiConsole.MarkupLine("[cyan]Conversion:[/] DDX->DDS: {0}, XMA->OGG: {1}, NIF: {2}",
                    ddxAvailable ? "[green]Yes[/]" : "[yellow]No (DDXConv not found)[/]",
                    xmaAvailable ? "[green]Yes[/]" : "[yellow]No (FFmpeg not found)[/]",
                    nifAvailable ? "[green]Yes[/]" : "[red]No[/]");
            }

            // Build filter predicate
            Func<BsaFileRecord, bool> predicate = _ => true;

            if (!string.IsNullOrEmpty(filter) || !string.IsNullOrEmpty(folder))
            {
                predicate = file =>
                {
                    if (!string.IsNullOrEmpty(filter))
                    {
                        var ext = filter.StartsWith('.') ? filter : $".{filter}";
                        if (file.Name?.EndsWith(ext, StringComparison.OrdinalIgnoreCase) != true)
                        {
                            return false;
                        }
                    }

                    if (!string.IsNullOrEmpty(folder))
                    {
                        if (file.Folder?.Name?.Contains(folder, StringComparison.OrdinalIgnoreCase) != true)
                        {
                            return false;
                        }
                    }

                    return true;
                };
            }

            var filesToExtract = archive.AllFiles.Where(predicate).ToList();
            AnsiConsole.MarkupLine("[cyan]Files to extract:[/] {0:N0}", filesToExtract.Count);
            AnsiConsole.WriteLine();

            if (filesToExtract.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No files match the filter criteria.[/]");
                return;
            }

            Directory.CreateDirectory(output);

            var results = await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Extracting files[/]", maxValue: filesToExtract.Count);

                    var progress = new Progress<(int current, int total, string fileName)>(p =>
                    {
                        task.Value = p.current;
                        task.Description = $"[green]Extracting:[/] {Path.GetFileName(p.fileName)}";
                    });

                    return await extractor.ExtractFilteredAsync(output, predicate, overwrite, progress);
                });

            // Summary
            var succeeded = results.Count(r => r.Success);
            var failed = results.Count(r => !r.Success);
            var converted = results.Count(r => r.WasConverted);
            var totalSize = results.Where(r => r.Success).Sum(r => r.ExtractedSize);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]✓ Extracted:[/] {0:N0} files ({1})", succeeded, FormatSize(totalSize));

            if (converted > 0)
            {
                var ddxConverted = results.Count(r => r.ConversionType == "DDX->DDS");
                var xmaConverted = results.Count(r => r.ConversionType == "XMA->OGG");
                var nifConverted = results.Count(r => r.ConversionType == "NIF BE->LE");

                var parts = new List<string>();
                if (ddxConverted > 0)
                {
                    parts.Add($"{ddxConverted} DDX -> DDS");
                }

                if (xmaConverted > 0)
                {
                    parts.Add($"{xmaConverted} XMA -> OGG");
                }

                if (nifConverted > 0)
                {
                    parts.Add($"{nifConverted} NIF");
                }

                AnsiConsole.MarkupLine("[blue]↻ Converted:[/] {0}", string.Join(", ", parts));
            }

            if (failed > 0)
            {
                AnsiConsole.MarkupLine("[red]✗ Failed:[/] {0:N0} files", failed);

                foreach (var failure in results.Where(r => !r.Success).Take(10))
                {
                    AnsiConsole.MarkupLine("  [red]•[/] {0}: {1}", failure.SourcePath, failure.Error);
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", ex.Message);
        }
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    private static string FormatSize(uint bytes)
    {
        return FormatSize((long)bytes);
    }
}
