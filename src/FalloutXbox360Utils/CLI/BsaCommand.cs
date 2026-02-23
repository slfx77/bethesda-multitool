// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for BSA archive operations. Entry point that registers all subcommands.
/// </summary>
public static class BsaCommand
{
    public static Command Create()
    {
        var bsaCommand = new Command("bsa", "BSA archive operations");

        bsaCommand.Subcommands.Add(CreateListCommand());
        bsaCommand.Subcommands.Add(BsaExtractCommand.CreateExtractCommand());
        bsaCommand.Subcommands.Add(BsaConvertCommand.CreateConvertCommand());
        bsaCommand.Subcommands.Add(CreateInfoCommand());
        bsaCommand.Subcommands.Add(BsaValidateCommand.CreateValidateCommand());
        bsaCommand.Subcommands.Add(BsaDebugCommand.CreateCompareCommand());
        bsaCommand.Subcommands.Add(BsaFindCommand.CreateFindCommand());
        bsaCommand.Subcommands.Add(BsaFindCommand.CreateInspectCommand());
        bsaCommand.Subcommands.Add(BsaDebugCommand.CreateRawDumpCommand());
        bsaCommand.Subcommands.Add(BsaDebugCommand.CreateFileCompareCommand());

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
                    CliHelpers.FormatSize(file.Size),
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
}
