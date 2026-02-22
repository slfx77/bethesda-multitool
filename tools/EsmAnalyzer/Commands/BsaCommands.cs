using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Bsa;
using ImageMagick;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     General-purpose BSA archive commands: list contents and extract files/directories.
/// </summary>
public static class BsaCommands
{
    public static Command CreateBsaCommandGroup()
    {
        var group = new Command("bsa", "BSA archive utilities (list, extract)");
        group.Subcommands.Add(CreateListCommand());
        group.Subcommands.Add(CreateExtractCommand());
        return group;
    }

    private static Command CreateListCommand()
    {
        var bsaArg = new Argument<string>("bsa") { Description = "Path to the BSA archive" };

        var pathArg = new Argument<string?>("path")
        {
            Description = "Virtual path filter (file or directory prefix). Omit to list all files.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var command = new Command("list", "List files in a BSA archive");
        command.Arguments.Add(bsaArg);
        command.Arguments.Add(pathArg);

        command.SetAction(parseResult =>
        {
            var bsaPath = parseResult.GetValue(bsaArg)!;
            var filter = parseResult.GetValue(pathArg);
            RunList(bsaPath, filter);
        });

        return command;
    }

    private static Command CreateExtractCommand()
    {
        var bsaArg = new Argument<string>("bsa") { Description = "Path to the BSA archive" };

        var pathArg = new Argument<string>("path")
        {
            Description = "Virtual path to extract (file or directory prefix)"
        };

        var outputOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output directory (default: current directory)"
        };

        var pngOpt = new Option<bool>("--png")
        {
            Description = "Convert DDS files to PNG during extraction"
        };

        var flatOpt = new Option<bool>("--flat")
        {
            Description = "Output all files flat (strip directory structure)"
        };

        var command = new Command("extract", "Extract files from a BSA archive");
        command.Arguments.Add(bsaArg);
        command.Arguments.Add(pathArg);
        command.Options.Add(outputOpt);
        command.Options.Add(pngOpt);
        command.Options.Add(flatOpt);

        command.SetAction(parseResult =>
        {
            var bsaPath = parseResult.GetValue(bsaArg)!;
            var virtualPath = parseResult.GetValue(pathArg)!;
            var outputDir = parseResult.GetValue(outputOpt);
            var convertPng = parseResult.GetValue(pngOpt);
            var flat = parseResult.GetValue(flatOpt);
            RunExtract(bsaPath, virtualPath, outputDir ?? ".", convertPng, flat);
        });

        return command;
    }

    private static void RunList(string bsaPath, string? filter)
    {
        if (!File.Exists(bsaPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] BSA file not found: {bsaPath}");
            return;
        }

        var archive = BsaParser.Parse(bsaPath);
        AnsiConsole.MarkupLine(
            $"BSA: [cyan]{Path.GetFileName(bsaPath)}[/] ({archive.TotalFiles:N0} files, {archive.Platform})");

        var files = MatchFiles(archive, filter);

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine(filter != null
                ? $"[yellow]No files matching:[/] {filter}"
                : "[yellow]Archive is empty.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("[bold]Path[/]")
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned())
            .AddColumn("[bold]Compressed[/]");

        long totalSize = 0;
        foreach (var file in files)
        {
            table.AddRow(
                Markup.Escape(file.FullPath),
                $"{file.Size:N0}",
                file.CompressionToggle ? "[yellow]toggle[/]" : "[grey]default[/]");
            totalSize += file.Size;
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[green]{files.Count:N0}[/] files, [green]{totalSize:N0}[/] bytes total");
    }

    private static void RunExtract(string bsaPath, string virtualPath, string outputDir, bool convertPng, bool flat)
    {
        if (!File.Exists(bsaPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] BSA file not found: {bsaPath}");
            return;
        }

        using var extractor = new BsaExtractor(bsaPath);
        var archive = extractor.Archive;

        AnsiConsole.MarkupLine(
            $"BSA: [cyan]{Path.GetFileName(bsaPath)}[/] ({archive.TotalFiles:N0} files, {archive.Platform})");

        var files = MatchFiles(archive, virtualPath);
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No files matching:[/] {virtualPath}");
            return;
        }

        AnsiConsole.MarkupLine($"Matched: [green]{files.Count}[/] file(s)");
        Directory.CreateDirectory(outputDir);

        var extracted = 0;
        var converted = 0;
        var errors = 0;

        foreach (var file in files)
        {
            try
            {
                var data = extractor.ExtractFile(file);

                // Determine output filename
                string outputPath;
                if (flat)
                {
                    outputPath = Path.Combine(outputDir, file.Name ?? $"unknown_{file.NameHash:X16}");
                }
                else
                {
                    // Preserve path relative to the matched prefix
                    var relativePath = GetRelativePath(file.FullPath, virtualPath);
                    outputPath = Path.Combine(outputDir, relativePath);
                    var dir = Path.GetDirectoryName(outputPath);
                    if (dir != null)
                        Directory.CreateDirectory(dir);
                }

                // DDS → PNG conversion
                var isDds = file.Name?.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) == true;
                if (convertPng && isDds)
                {
                    outputPath = Path.ChangeExtension(outputPath, ".png");
                    using var image = new MagickImage(data);
                    image.Write(outputPath, MagickFormat.Png);
                    converted++;
                    AnsiConsole.MarkupLine(
                        $"  [green]PNG[/] {Markup.Escape(file.FullPath)} → {Markup.Escape(Path.GetFileName(outputPath))} ({image.Width}x{image.Height})");
                }
                else
                {
                    File.WriteAllBytes(outputPath, data);
                    AnsiConsole.MarkupLine(
                        $"  [cyan]OK[/]  {Markup.Escape(file.FullPath)} ({data.Length:N0} bytes)");
                }

                extracted++;
            }
            catch (Exception ex)
            {
                errors++;
                AnsiConsole.MarkupLine(
                    $"  [red]ERR[/] {Markup.Escape(file.FullPath)}: {Markup.Escape(ex.Message)}");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Extracted: [green]{extracted}[/] file(s) to [cyan]{Path.GetFullPath(outputDir)}[/]");
        if (converted > 0)
            AnsiConsole.MarkupLine($"Converted: [green]{converted}[/] DDS → PNG");
        if (errors > 0)
            AnsiConsole.MarkupLine($"Errors:    [red]{errors}[/]");
    }

    /// <summary>
    ///     Match files by virtual path. If the path matches a single file exactly, return just that file.
    ///     Otherwise treat it as a directory prefix and return all files under it.
    /// </summary>
    private static List<BsaFileRecord> MatchFiles(BsaArchive archive, string? filter)
    {
        if (string.IsNullOrEmpty(filter))
            return archive.AllFiles.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList();

        var normalized = filter.Replace('/', '\\').TrimEnd('\\');

        // Exact file match?
        var exact = archive.FindFile(normalized);
        if (exact != null)
            return [exact];

        // Directory prefix match
        var prefix = normalized + "\\";
        return archive.AllFiles
            .Where(f => f.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    ///     Compute the path of a file relative to the matched prefix.
    ///     If the file's folder is the prefix itself, returns just the filename.
    /// </summary>
    private static string GetRelativePath(string fullPath, string prefix)
    {
        var normalized = prefix.Replace('/', '\\').TrimEnd('\\') + "\\";
        if (fullPath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            return fullPath[normalized.Length..];

        // If prefix matches the file exactly, just use the filename
        return Path.GetFileName(fullPath);
    }
}
