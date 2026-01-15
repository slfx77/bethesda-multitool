using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using TextureAnalyzer.Parsers;

namespace TextureAnalyzer.Commands;

/// <summary>
///     Commands for scanning folders of texture files.
/// </summary>
internal static class ScanCommands
{
    /// <summary>
    ///     Create the "scan" command for batch scanning DDX files.
    /// </summary>
    public static Command CreateScanCommand()
    {
        var command = new Command("scan", "Scan a folder for DDX files and show format breakdown (3XDO vs 3XDR)");
        var folderArg = new Argument<string>("folder") { Description = "Folder path to scan" };
        var recursiveOpt = new Option<bool>("-r", "--recursive") { Description = "Search subdirectories recursively" };
        var showFilesOpt = new Option<bool>("-v", "--verbose") { Description = "Show individual file details" };
        var filterOpt = new Option<string?>("-f", "--filter") { Description = "Filter by format: 3xdo, 3xdr, or format code (1-8)" };

        command.Arguments.Add(folderArg);
        command.Options.Add(recursiveOpt);
        command.Options.Add(showFilesOpt);
        command.Options.Add(filterOpt);

        command.SetAction(parseResult => Scan(
            parseResult.GetValue(folderArg)!,
            parseResult.GetValue(recursiveOpt),
            parseResult.GetValue(showFilesOpt),
            parseResult.GetValue(filterOpt)));

        return command;
    }

    /// <summary>
    ///     Create the "stats" command for detailed statistics.
    /// </summary>
    public static Command CreateStatsCommand()
    {
        var command = new Command("stats", "Show detailed statistics for DDX files in a folder");
        var folderArg = new Argument<string>("folder") { Description = "Folder path to scan" };
        var recursiveOpt = new Option<bool>("-r", "--recursive") { Description = "Search subdirectories recursively" };

        command.Arguments.Add(folderArg);
        command.Options.Add(recursiveOpt);

        command.SetAction(parseResult => Stats(
            parseResult.GetValue(folderArg)!,
            parseResult.GetValue(recursiveOpt)));

        return command;
    }

    private static void Scan(string folder, bool recursive, bool verbose, string? filter)
    {
        if (!Directory.Exists(folder))
        {
            AnsiConsole.MarkupLine($"[red]Folder not found:[/] {folder}");
            return;
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var ddxFiles = Directory.EnumerateFiles(folder, "*.ddx", searchOption).ToList();

        AnsiConsole.MarkupLine($"[bold]Scanning {ddxFiles.Count:N0} DDX files...[/]");
        AnsiConsole.WriteLine();

        var stats = new ScanStats();
        var matchingFiles = new List<(string Path, DdxInfo Info)>();

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning...", ctx =>
            {
                foreach (var file in ddxFiles)
                {
                    ctx.Status($"Scanning: {Path.GetFileName(file)}");
                    try
                    {
                        var data = File.ReadAllBytes(file);
                        var ddx = TextureParser.ParseDdx(data);
                        if (ddx != null)
                        {
                            stats.Total++;
                            stats.TotalBytes += data.Length;

                            if (ddx.Is3XDO)
                            {
                                stats.Count3XDO++;
                                stats.Bytes3XDO += data.Length;
                            }
                            else if (ddx.Is3XDR)
                            {
                                stats.Count3XDR++;
                                stats.Bytes3XDR += data.Length;
                            }

                            // Track format distribution
                            if (!stats.FormatCounts.ContainsKey((byte)ddx.ActualFormat))
                                stats.FormatCounts[(byte)ddx.ActualFormat] = 0;
                            stats.FormatCounts[(byte)ddx.ActualFormat]++;

                            // Track dimension distribution
                            var dimKey = $"{ddx.Width}x{ddx.Height}";
                            if (!stats.DimensionCounts.ContainsKey(dimKey))
                                stats.DimensionCounts[dimKey] = 0;
                            stats.DimensionCounts[dimKey]++;

                            // Check filter
                            var matches = filter?.ToLowerInvariant() switch
                            {
                                "3xdo" => ddx.Is3XDO,
                                "3xdr" => ddx.Is3XDR,
                                _ when int.TryParse(filter, out var fmt) => ddx.ActualFormat == (uint)fmt,
                                _ => filter == null
                            };

                            if (matches)
                            {
                                var relativePath = Path.GetRelativePath(folder, file);
                                matchingFiles.Add((relativePath, ddx));
                            }
                        }
                    }
                    catch
                    {
                        stats.Errors++;
                    }
                }
            });

        // Summary table
        var summaryTable = new Table().Border(TableBorder.Rounded);
        summaryTable.AddColumn("Type");
        summaryTable.AddColumn(new TableColumn("Count").RightAligned());
        summaryTable.AddColumn(new TableColumn("Size").RightAligned());
        summaryTable.AddColumn(new TableColumn("%").RightAligned());

        summaryTable.AddRow(
            "[green]3XDO (Morton)[/]",
            stats.Count3XDO.ToString("N0"),
            FormatBytes(stats.Bytes3XDO),
            stats.Total > 0 ? $"{100.0 * stats.Count3XDO / stats.Total:F1}%" : "0%");

        summaryTable.AddRow(
            "[yellow]3XDR (Engine)[/]",
            stats.Count3XDR.ToString("N0"),
            FormatBytes(stats.Bytes3XDR),
            stats.Total > 0 ? $"{100.0 * stats.Count3XDR / stats.Total:F1}%" : "0%");

        summaryTable.AddRow(
            "[bold]Total[/]",
            $"[bold]{stats.Total:N0}[/]",
            $"[bold]{FormatBytes(stats.TotalBytes)}[/]",
            "100%");

        AnsiConsole.Write(summaryTable);

        if (stats.Errors > 0)
            AnsiConsole.MarkupLine($"[yellow]({stats.Errors} files had parse errors)[/]");

        // Show format distribution
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Format Distribution:[/]");
        var formatTable = new Table().Border(TableBorder.Simple);
        formatTable.AddColumn("Code");
        formatTable.AddColumn("Format");
        formatTable.AddColumn(new TableColumn("Count").RightAligned());

        foreach (var (format, count) in stats.FormatCounts.OrderByDescending(x => x.Value))
        {
            var name = format switch
            {
                0x52 => "DXT1",
                0x53 => "DXT3",
                0x54 => "DXT5",
                0x71 => "ATI2 (DXN)",
                0x7B => "ATI1 (BC4)",
                0x82 => "DXT1 (base)",
                0x86 => "DXT1 (var)",
                0x88 => "DXT5 (var)",
                0x12 => "DXT1 (GPU)",
                0x13 => "DXT3 (GPU)",
                0x14 => "DXT5 (GPU)",
                _ => $"Unknown"
            };
            formatTable.AddRow(format.ToString(CultureInfo.InvariantCulture), name, count.ToString("N0", CultureInfo.InvariantCulture));
        }
        AnsiConsole.Write(formatTable);

        // Show verbose file list if requested
        if (verbose && matchingFiles.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]Files ({matchingFiles.Count:N0}):[/]");

            var fileTable = new Table().Border(TableBorder.Rounded);
            fileTable.AddColumn(new TableColumn("File").Width(50));
            fileTable.AddColumn("Magic");
            fileTable.AddColumn("Size");
            fileTable.AddColumn("Format");
            fileTable.AddColumn("Flags");

            foreach (var (path, info) in matchingFiles.Take(100))
            {
                var magicColor = info.Is3XDR ? "yellow" : "green";
                fileTable.AddRow(
                    Markup.Escape(path.Length > 50 ? "..." + path[^47..] : path),
                    $"[{magicColor}]{info.Magic}[/]",
                    $"{info.Width}x{info.Height}",
                    info.FormatName,
                    info.Tiled ? "Tiled" : "Linear");
            }

            if (matchingFiles.Count > 100)
                AnsiConsole.MarkupLine($"[dim](showing first 100 of {matchingFiles.Count:N0})[/]");

            AnsiConsole.Write(fileTable);
        }
    }

    private static void Stats(string folder, bool recursive)
    {
        if (!Directory.Exists(folder))
        {
            AnsiConsole.MarkupLine($"[red]Folder not found:[/] {folder}");
            return;
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var ddxFiles = Directory.EnumerateFiles(folder, "*.ddx", searchOption).ToList();

        AnsiConsole.MarkupLine($"[bold]Analyzing {ddxFiles.Count:N0} DDX files...[/]");
        AnsiConsole.WriteLine();

        var stats = new ScanStats();

        // Track 3XDR-specific data
        var xdrByFormat = new Dictionary<byte, int>();
        var xdrByDimension = new Dictionary<string, int>();
        var xdrByFlags = new Dictionary<byte, int>();

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Analyzing...", ctx =>
            {
                foreach (var file in ddxFiles)
                {
                    try
                    {
                        var data = File.ReadAllBytes(file);
                        var ddx = TextureParser.ParseDdx(data);
                        if (ddx != null)
                        {
                            stats.Total++;

                            if (ddx.Is3XDO) stats.Count3XDO++;
                            else if (ddx.Is3XDR)
                            {
                                stats.Count3XDR++;

                                var formatKey = (byte)ddx.ActualFormat;
                                if (!xdrByFormat.ContainsKey(formatKey))
                                    xdrByFormat[formatKey] = 0;
                                xdrByFormat[formatKey]++;

                                var dimKey = $"{ddx.Width}x{ddx.Height}";
                                if (!xdrByDimension.ContainsKey(dimKey))
                                    xdrByDimension[dimKey] = 0;
                                xdrByDimension[dimKey]++;

                                var tiledKey = (byte)(ddx.Tiled ? 1 : 0);
                                if (!xdrByFlags.ContainsKey(tiledKey))
                                    xdrByFlags[tiledKey] = 0;
                                xdrByFlags[tiledKey]++;
                            }

                            var format = (byte)ddx.ActualFormat;
                            if (!stats.FormatCounts.ContainsKey(format))
                                stats.FormatCounts[format] = 0;
                            stats.FormatCounts[format]++;

                            var dim = $"{ddx.Width}x{ddx.Height}";
                            if (!stats.DimensionCounts.ContainsKey(dim))
                                stats.DimensionCounts[dim] = 0;
                            stats.DimensionCounts[dim]++;
                        }
                    }
                    catch
                    {
                        stats.Errors++;
                    }
                }
            });

        // Overall summary
        AnsiConsole.MarkupLine($"[bold]Overall: {stats.Count3XDO:N0} 3XDO + {stats.Count3XDR:N0} 3XDR = {stats.Total:N0} files[/]");
        AnsiConsole.WriteLine();

        // 3XDR breakdown
        if (stats.Count3XDR > 0)
        {
            AnsiConsole.MarkupLine("[bold yellow]3XDR Analysis:[/]");
            AnsiConsole.WriteLine();

            // By format
            AnsiConsole.MarkupLine("[bold]By Format:[/]");
            var formatTable = new Table().Border(TableBorder.Simple);
            formatTable.AddColumn("Code");
            formatTable.AddColumn("Name");
            formatTable.AddColumn(new TableColumn("Count").RightAligned());
            formatTable.AddColumn(new TableColumn("%").RightAligned());

            foreach (var (format, count) in xdrByFormat.OrderByDescending(x => x.Value))
            {
                var name = format switch
                {
                    0x52 => "DXT1",
                    0x53 => "DXT3",
                    0x54 => "DXT5",
                    0x71 => "ATI2",
                    0x7B => "ATI1",
                    0x82 => "DXT1 (base)",
                    0x86 => "DXT1 (var)",
                    0x88 => "DXT5 (var)",
                    _ => "Unknown"
                };
                formatTable.AddRow(
                    $"0x{format:X2}",
                    name,
                    count.ToString("N0", CultureInfo.InvariantCulture),
                    $"{100.0 * count / stats.Count3XDR:F1}%");
            }
            AnsiConsole.Write(formatTable);
            AnsiConsole.WriteLine();

            // By flags
            AnsiConsole.MarkupLine("[bold]By Flags:[/]");
            var flagsTable = new Table().Border(TableBorder.Simple);
            flagsTable.AddColumn("Flags");
            flagsTable.AddColumn("Meaning");
            flagsTable.AddColumn(new TableColumn("Count").RightAligned());

            foreach (var (flags, count) in xdrByFlags.OrderByDescending(x => x.Value))
            {
                var meaning = flags != 0 ? "Tiled" : "Linear";
                flagsTable.AddRow($"0x{flags:X2}", meaning, count.ToString("N0", CultureInfo.InvariantCulture));
            }
            AnsiConsole.Write(flagsTable);
            AnsiConsole.WriteLine();

            // Top dimensions
            AnsiConsole.MarkupLine("[bold]Top Dimensions (3XDR):[/]");
            var dimTable = new Table().Border(TableBorder.Simple);
            dimTable.AddColumn("Dimension");
            dimTable.AddColumn(new TableColumn("Count").RightAligned());

            foreach (var (dim, count) in xdrByDimension.OrderByDescending(x => x.Value).Take(15))
            {
                dimTable.AddRow(dim, count.ToString("N0", CultureInfo.InvariantCulture));
            }
            AnsiConsole.Write(dimTable);
        }

        if (stats.Errors > 0)
            AnsiConsole.MarkupLine($"[yellow]({stats.Errors} files had parse errors)[/]");
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F2} MB",
            >= 1024 => $"{bytes / 1024.0:F2} KB",
            _ => $"{bytes} B"
        };
    }

    private class ScanStats
    {
        public int Total { get; set; }
        public int Count3XDO { get; set; }
        public int Count3XDR { get; set; }
        public long TotalBytes { get; set; }
        public long Bytes3XDO { get; set; }
        public long Bytes3XDR { get; set; }
        public int Errors { get; set; }
        public Dictionary<byte, int> FormatCounts { get; } = new();
        public Dictionary<string, int> DimensionCounts { get; } = new();
    }
}
