// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Security.Cryptography;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

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
        bsaCommand.Subcommands.Add(CreateValidateCommand());
        bsaCommand.Subcommands.Add(CreateCompareCommand());

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

    private static Command CreateValidateCommand()
    {
        var command = new Command("validate", "Validate BSA round-trip (extract -> repack -> compare)");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA file" };
        var keepTempOption = new Option<bool>("--keep-temp")
            { Description = "Keep temporary files after validation" };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Verbose output" };

        command.Arguments.Add(inputArg);
        command.Options.Add(keepTempOption);
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var keepTemp = parseResult.GetValue(keepTempOption);
            var verbose = parseResult.GetValue(verboseOption);
            await RunValidateAsync(input, keepTemp, verbose);
        });

        return command;
    }

    private static Command CreateCompareCommand()
    {
        var command = new Command("compare", "Compare two BSA archives in detail (header, hashes, structure)");

        var file1Arg = new Argument<string>("file1") { Description = "First BSA file (original)" };
        var file2Arg = new Argument<string>("file2") { Description = "Second BSA file (to compare)" };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Show all file hashes" };

        command.Arguments.Add(file1Arg);
        command.Arguments.Add(file2Arg);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, _) =>
        {
            var file1 = parseResult.GetValue(file1Arg)!;
            var file2 = parseResult.GetValue(file2Arg)!;
            var verbose = parseResult.GetValue(verboseOption);
            RunCompare(file1, file2, verbose);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void RunCompare(string file1Path, string file2Path, bool verbose)
    {
        if (!File.Exists(file1Path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", file1Path);
            return;
        }

        if (!File.Exists(file2Path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", file2Path);
            return;
        }

        try
        {
            var archive1 = BsaParser.Parse(file1Path);
            var archive2 = BsaParser.Parse(file2Path);

            AnsiConsole.MarkupLine("[cyan]BSA Comparison[/]");
            AnsiConsole.MarkupLine("[dim]File 1:[/] {0}", Path.GetFileName(file1Path));
            AnsiConsole.MarkupLine("[dim]File 2:[/] {0}", Path.GetFileName(file2Path));
            AnsiConsole.WriteLine();

            // Header comparison table
            var table = new Table();
            table.AddColumn("Property");
            table.AddColumn("File 1");
            table.AddColumn("File 2");
            table.AddColumn("Match");
            table.Border = TableBorder.Rounded;

            void AddRow(string prop, object val1, object val2)
            {
                var match = val1.Equals(val2);
                var matchStr = match ? "[green]✓[/]" : "[red]✗[/]";
                var val1Str = val1?.ToString() ?? "(null)";
                var val2Str = val2?.ToString() ?? "(null)";
                if (!match)
                {
                    val1Str = $"[yellow]{val1Str}[/]";
                    val2Str = $"[yellow]{val2Str}[/]";
                }

                table.AddRow(prop, val1Str, val2Str, matchStr);
            }

            AddRow("Version", archive1.Header.Version, archive2.Header.Version);
            AddRow("ArchiveFlags", $"0x{(uint)archive1.Header.ArchiveFlags:X4}",
                $"0x{(uint)archive2.Header.ArchiveFlags:X4}");
            AddRow("FolderCount", archive1.Header.FolderCount, archive2.Header.FolderCount);
            AddRow("FileCount", archive1.Header.FileCount, archive2.Header.FileCount);
            AddRow("TotalFolderNameLength", archive1.Header.TotalFolderNameLength,
                archive2.Header.TotalFolderNameLength);
            AddRow("TotalFileNameLength", archive1.Header.TotalFileNameLength, archive2.Header.TotalFileNameLength);
            AddRow("FileFlags", $"0x{(ushort)archive1.Header.FileFlags:X4}",
                $"0x{(ushort)archive2.Header.FileFlags:X4}");
            AddRow("DefaultCompressed", archive1.Header.DefaultCompressed, archive2.Header.DefaultCompressed);
            AddRow("EmbedFileNames", archive1.Header.EmbedFileNames, archive2.Header.EmbedFileNames);

            AnsiConsole.MarkupLine("[bold]Header Comparison:[/]");
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // File size comparison
            var size1 = new FileInfo(file1Path).Length;
            var size2 = new FileInfo(file2Path).Length;
            var sizeDiff = size2 - size1;
            AnsiConsole.MarkupLine("[bold]File Size:[/]");
            AnsiConsole.MarkupLine("  File 1: {0}", FormatSize(size1));
            AnsiConsole.MarkupLine("  File 2: {0}", FormatSize(size2));
            if (sizeDiff != 0)
            {
                var sign = sizeDiff > 0 ? "+" : "";
                AnsiConsole.MarkupLine("  [yellow]Difference: {0}{1} bytes ({2}{3})[/]",
                    sign, sizeDiff, sign, FormatSize(Math.Abs(sizeDiff)));
            }
            else
            {
                AnsiConsole.MarkupLine("  [green]Same size[/]");
            }

            AnsiConsole.WriteLine();

            // Compare folder hashes and order
            AnsiConsole.MarkupLine("[bold]Folder Hash Order:[/]");
            var folders1 = archive1.Folders.Select(f => (f.Name, f.NameHash)).ToList();
            var folders2 = archive2.Folders.Select(f => (f.Name, f.NameHash)).ToList();

            var folderOrderMatch = folders1.Count == folders2.Count &&
                                   folders1.Zip(folders2).All(p => p.First.NameHash == p.Second.NameHash);
            if (folderOrderMatch)
            {
                AnsiConsole.MarkupLine("  [green]✓ Folder order matches[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("  [red]✗ Folder order differs[/]");
                if (verbose)
                {
                    for (var i = 0; i < Math.Max(folders1.Count, folders2.Count); i++)
                    {
                        var f1 = i < folders1.Count ? folders1[i] : (null, 0UL);
                        var f2 = i < folders2.Count ? folders2[i] : (null, 0UL);
                        var match = f1.NameHash == f2.NameHash;
                        var marker = match ? "[green]=[/]" : "[red]≠[/]";
                        AnsiConsole.MarkupLine("    {0} [{1}] {2:X16} vs {3:X16}",
                            marker, i, f1.NameHash, f2.NameHash);
                    }
                }
            }

            AnsiConsole.WriteLine();

            // Compare first few bytes of raw data
            AnsiConsole.MarkupLine("[bold]Raw Header Bytes (first 36):[/]");
            var bytes1 = File.ReadAllBytes(file1Path).Take(36).ToArray();
            var bytes2 = File.ReadAllBytes(file2Path).Take(36).ToArray();
            var hexMatch = bytes1.SequenceEqual(bytes2);

            if (hexMatch)
            {
                AnsiConsole.MarkupLine("  [green]✓ Headers are byte-identical[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("  [red]✗ Headers differ[/]");
                AnsiConsole.MarkupLine("  File 1: {0}", Convert.ToHexString(bytes1));
                AnsiConsole.MarkupLine("  File 2: {0}", Convert.ToHexString(bytes2));

                // Show byte-by-byte diff
                for (var i = 0; i < 36; i++)
                {
                    if (bytes1[i] != bytes2[i])
                    {
                        AnsiConsole.MarkupLine("    [yellow]Offset {0}: 0x{1:X2} vs 0x{2:X2}[/]",
                            i, bytes1[i], bytes2[i]);
                    }
                }
            }

            // Summary
            AnsiConsole.WriteLine();
            var allMatch = archive1.Header.Version == archive2.Header.Version &&
                           archive1.Header.ArchiveFlags == archive2.Header.ArchiveFlags &&
                           archive1.Header.FolderCount == archive2.Header.FolderCount &&
                           archive1.Header.FileCount == archive2.Header.FileCount &&
                           archive1.Header.TotalFolderNameLength == archive2.Header.TotalFolderNameLength &&
                           archive1.Header.TotalFileNameLength == archive2.Header.TotalFileNameLength &&
                           archive1.Header.FileFlags == archive2.Header.FileFlags;

            if (allMatch && hexMatch)
            {
                AnsiConsole.MarkupLine("[green]✓ BSA headers are structurally identical[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠ BSA headers have structural differences[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error comparing BSAs:[/] {0}", ex.Message);
        }
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
                    AnsiConsole.MarkupLine("  [red]•[/] {0}: {1}", failure.SourcePath,
                        failure.Error ?? "Unknown error");
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

    private static async Task RunValidateAsync(string input, bool keepTemp, bool verbose)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"bsa_validate_{Guid.NewGuid():N}");
        var extractDir = Path.Combine(tempDir, "extracted");
        var repackedPath = Path.Combine(tempDir, "repacked.bsa");

        try
        {
            AnsiConsole.MarkupLine("[cyan]BSA Round-Trip Validation[/]");
            AnsiConsole.MarkupLine("[dim]Input:[/] {0}", input);
            AnsiConsole.WriteLine();

            // Step 1: Parse and analyze original
            AnsiConsole.MarkupLine("[yellow]Step 1:[/] Parsing original BSA...");
            var originalArchive = BsaParser.Parse(input);

            AnsiConsole.MarkupLine("  Version: {0}", originalArchive.Header.Version);
            AnsiConsole.MarkupLine("  Platform: {0}", originalArchive.Platform);
            AnsiConsole.MarkupLine("  Compressed: {0}", originalArchive.Header.DefaultCompressed);
            AnsiConsole.MarkupLine("  Folders: {0:N0}", originalArchive.Header.FolderCount);
            AnsiConsole.MarkupLine("  Files: {0:N0}", originalArchive.Header.FileCount);
            AnsiConsole.WriteLine();

            // Step 2: Extract all files
            AnsiConsole.MarkupLine("[yellow]Step 2:[/] Extracting files...");
            Directory.CreateDirectory(extractDir);

            using var extractor = new BsaExtractor(input);
            var extractResults = await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Extracting[/]", maxValue: originalArchive.TotalFiles);
                    var progress = new Progress<(int current, int total, string fileName)>(p =>
                    {
                        task.Value = p.current;
                    });
                    return await extractor.ExtractAllAsync(extractDir, true, progress);
                });

            var extractedCount = extractResults.Count(r => r.Success);
            var failedCount = extractResults.Count(r => !r.Success);
            AnsiConsole.MarkupLine("  Extracted: {0:N0} files", extractedCount);
            if (failedCount > 0)
            {
                AnsiConsole.MarkupLine("  [red]Failed: {0:N0} files[/]", failedCount);
                foreach (var failed in extractResults.Where(r => !r.Success))
                {
                    AnsiConsole.MarkupLine("    [red]• {0}: {1}[/]", failed.SourcePath,
                        failed.Error ?? "Unknown error");
                }
            }

            AnsiConsole.WriteLine();

            // Step 3: Build content hashes of extracted files
            AnsiConsole.MarkupLine("[yellow]Step 3:[/] Computing content hashes of extracted files...");
            var extractedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var result in extractResults.Where(r => r.Success))
            {
                var relativePath = Path.GetRelativePath(extractDir, result.OutputPath)
                    .Replace('/', '\\').ToLowerInvariant();
                var hash = await ComputeFileHashAsync(result.OutputPath);
                extractedHashes[relativePath] = hash;
            }

            AnsiConsole.MarkupLine("  Hashed: {0:N0} files", extractedHashes.Count);
            AnsiConsole.WriteLine();

            // Step 4: Repack using BsaWriter
            AnsiConsole.MarkupLine("[yellow]Step 4:[/] Repacking BSA...");

            using var writer = new BsaWriter(
                originalArchive.Header.DefaultCompressed,
                originalArchive.Header.FileFlags);

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Adding files[/]", maxValue: extractedHashes.Count);
                    var count = 0;

                    foreach (var (relativePath, _) in extractedHashes)
                    {
                        var fullPath = Path.Combine(extractDir, relativePath);
                        var data = await File.ReadAllBytesAsync(fullPath);
                        writer.AddFile(relativePath, data);
                        task.Value = ++count;
                    }
                });

            writer.Write(repackedPath);
            AnsiConsole.MarkupLine("  Repacked to: {0}", repackedPath);
            AnsiConsole.WriteLine();

            // Step 5: Parse repacked BSA and compare
            AnsiConsole.MarkupLine("[yellow]Step 5:[/] Comparing original vs repacked...");
            var repackedArchive = BsaParser.Parse(repackedPath);

            var issues = new List<string>();

            // Compare header fields
            if (originalArchive.Header.Version != repackedArchive.Header.Version)
            {
                issues.Add($"Version mismatch: {originalArchive.Header.Version} vs {repackedArchive.Header.Version}");
            }

            // Account for extraction failures in file count comparison
            var expectedFileCount = originalArchive.Header.FileCount - (uint)failedCount;
            if (expectedFileCount != repackedArchive.Header.FileCount)
            {
                issues.Add(
                    $"File count mismatch: expected {expectedFileCount} (original {originalArchive.Header.FileCount} - {failedCount} failed), got {repackedArchive.Header.FileCount}");
            }

            // Folder count might differ if all files in a folder failed to extract
            var expectedMinFolderCount = extractedHashes.Select(kv =>
            {
                var lastSlash = kv.Key.LastIndexOf('\\');
                return lastSlash >= 0 ? kv.Key[..lastSlash] : "";
            }).Distinct().Count();
            if (repackedArchive.Header.FolderCount < expectedMinFolderCount)
            {
                issues.Add(
                    $"Folder count too low: {repackedArchive.Header.FolderCount} vs expected at least {expectedMinFolderCount}");
            }

            // Compare file content by extracting repacked and hashing
            AnsiConsole.MarkupLine("[yellow]Step 6:[/] Verifying repacked content...");
            var repackExtractDir = Path.Combine(tempDir, "repacked_extracted");
            Directory.CreateDirectory(repackExtractDir);

            using var repackExtractor = new BsaExtractor(repackedPath);
            var repackExtractResults = await repackExtractor.ExtractAllAsync(repackExtractDir, true);

            var repackedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in repackExtractResults.Where(r => r.Success))
            {
                var relativePath = Path.GetRelativePath(repackExtractDir, result.OutputPath)
                    .Replace('/', '\\').ToLowerInvariant();
                var hash = await ComputeFileHashAsync(result.OutputPath);
                repackedHashes[relativePath] = hash;
            }

            // Compare hashes
            var missingInRepacked = extractedHashes.Keys.Except(repackedHashes.Keys).ToList();
            var extraInRepacked = repackedHashes.Keys.Except(extractedHashes.Keys).ToList();
            var hashMismatches = new List<string>();

            foreach (var (path, originalHash) in extractedHashes)
            {
                if (repackedHashes.TryGetValue(path, out var repackedHash) && originalHash != repackedHash)
                {
                    hashMismatches.Add(path);
                }
            }

            if (missingInRepacked.Count > 0)
            {
                issues.Add($"Missing in repacked: {missingInRepacked.Count} files");
                if (verbose)
                {
                    foreach (var path in missingInRepacked.Take(10))
                    {
                        issues.Add($"  - {path}");
                    }
                }
            }

            if (extraInRepacked.Count > 0)
            {
                issues.Add($"Extra in repacked: {extraInRepacked.Count} files");
                if (verbose)
                {
                    foreach (var path in extraInRepacked.Take(10))
                    {
                        issues.Add($"  + {path}");
                    }
                }
            }

            if (hashMismatches.Count > 0)
            {
                issues.Add($"Content hash mismatches: {hashMismatches.Count} files");
                if (verbose)
                {
                    foreach (var path in hashMismatches.Take(10))
                    {
                        issues.Add($"  ≠ {path}");
                    }
                }
            }

            AnsiConsole.WriteLine();

            // Report results
            var extractionIssues = failedCount > 0;
            var roundTripIssues = issues.Count > 0;

            if (!roundTripIssues && !extractionIssues)
            {
                AnsiConsole.MarkupLine("[green]✓ Validation PASSED[/]");
                AnsiConsole.MarkupLine("  Round-trip produces identical content.");
                AnsiConsole.MarkupLine("  Files: {0:N0}", extractedHashes.Count);
            }
            else if (!roundTripIssues && extractionIssues)
            {
                AnsiConsole.MarkupLine("[yellow]⚠ Validation PARTIAL[/]");
                AnsiConsole.MarkupLine("  Round-trip OK for {0:N0}/{1:N0} files.", extractedHashes.Count,
                    originalArchive.Header.FileCount);
                AnsiConsole.MarkupLine("  [yellow]{0:N0} file(s) failed extraction (extractor bug - see above)[/]",
                    failedCount);
            }
            else if (roundTripIssues && extractionIssues)
            {
                AnsiConsole.MarkupLine("[red]✗ Validation FAILED[/]");
                AnsiConsole.MarkupLine("  {0} round-trip issues found:", issues.Count);
                foreach (var issue in issues)
                {
                    AnsiConsole.MarkupLine("[red]  • {0}[/]", issue);
                }

                AnsiConsole.MarkupLine("  [yellow]Plus {0} extraction failures (see above)[/]", failedCount);
            }
            else
            {
                AnsiConsole.MarkupLine("[red]✗ Validation FAILED[/]");
                AnsiConsole.MarkupLine("  {0} issues found:", issues.Count);
                foreach (var issue in issues)
                {
                    AnsiConsole.MarkupLine("[red]  • {0}[/]", issue);
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error during validation:[/] {0}", ex.Message);
            if (verbose)
            {
                AnsiConsole.WriteException(ex);
            }
        }
        finally
        {
            // Cleanup
            if (!keepTemp && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                    AnsiConsole.MarkupLine("[dim]Cleaned up temp files.[/]");
                }
                catch
                {
                    AnsiConsole.MarkupLine("[yellow]Warning:[/] Could not clean up temp dir: {0}", tempDir);
                }
            }
            else if (keepTemp)
            {
                AnsiConsole.MarkupLine("[dim]Temp files kept at:[/] {0}", tempDir);
            }
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }
}
