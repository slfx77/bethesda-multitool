using System.CommandLine;
using NifAnalyzer.Parsers;
using Spectre.Console;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for batch scanning NIF files in directories.
/// </summary>
internal static class ScanCommands
{
    public static Command CreateScanCommand()
    {
        var command = new Command("scan", "Scan a folder for NIF files containing specific block types");
        var folderArg = new Argument<string>("folder") { Description = "Folder path to scan" };
        var blockTypeArg = new Argument<string>("blockType") { Description = "Block type to search for (e.g., NiControllerSequence)" };
        var recursiveOpt = new Option<bool>("-r", "--recursive") { Description = "Search subdirectories recursively" };
        command.Arguments.Add(folderArg);
        command.Arguments.Add(blockTypeArg);
        command.Options.Add(recursiveOpt);
        command.SetAction(parseResult => Scan(
            parseResult.GetValue(folderArg)!,
            parseResult.GetValue(blockTypeArg)!,
            parseResult.GetValue(recursiveOpt)));
        return command;
    }

    public static Command CreateAnimatedCommand()
    {
        var command = new Command("animated", "Find all NIF files with embedded animations");
        var folderArg = new Argument<string>("folder") { Description = "Folder path to scan" };
        var recursiveOpt = new Option<bool>("-r", "--recursive") { Description = "Search subdirectories recursively" };
        command.Arguments.Add(folderArg);
        command.Options.Add(recursiveOpt);
        command.SetAction(parseResult => FindAnimated(
            parseResult.GetValue(folderArg)!,
            parseResult.GetValue(recursiveOpt)));
        return command;
    }

    /// <summary>
    ///     Scan folder for NIF files containing a specific block type.
    /// </summary>
    private static void Scan(string folder, string blockType, bool recursive)
    {
        if (!Directory.Exists(folder))
        {
            AnsiConsole.MarkupLine($"[red]Folder not found:[/] {folder}");
            return;
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var nifFiles = Directory.EnumerateFiles(folder, "*.nif", searchOption).ToList();

        AnsiConsole.MarkupLine($"[bold]Scanning {nifFiles.Count} NIF files for block type:[/] {blockType}");
        AnsiConsole.WriteLine();

        var matches = new List<(string Path, int Count, List<string> Details)>();
        var errors = 0;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning...", ctx =>
            {
                foreach (var file in nifFiles)
                {
                    ctx.Status($"Scanning: {Path.GetFileName(file)}");
                    try
                    {
                        var data = File.ReadAllBytes(file);
                        var nif = NifParser.Parse(data);

                        // Get list of (index, typeName) for all blocks
                        var matchingBlocks = new List<(int Index, string TypeName)>();
                        for (var i = 0; i < nif.NumBlocks; i++)
                        {
                            var typeName = nif.GetBlockTypeName(i);
                            if (typeName.Contains(blockType, StringComparison.OrdinalIgnoreCase))
                            {
                                matchingBlocks.Add((i, typeName));
                            }
                        }

                        if (matchingBlocks.Count > 0)
                        {
                            var relativePath = Path.GetRelativePath(folder, file);
                            var details = matchingBlocks
                                .Select(x => $"[{x.Index}] {x.TypeName}")
                                .ToList();
                            matches.Add((relativePath, matchingBlocks.Count, details));
                        }
                    }
                    catch
                    {
                        errors++;
                    }
                }
            });

        // Display results
        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No NIF files found containing block type:[/] {blockType}");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn(new TableColumn("File").Width(60));
            table.AddColumn(new TableColumn("Count").RightAligned());
            table.AddColumn("Blocks");

            foreach (var (path, count, details) in matches.OrderBy(m => m.Path))
            {
                var detailStr = string.Join(", ", details.Take(3));
                if (details.Count > 3)
                    detailStr += $", +{details.Count - 3} more";
                table.AddRow(Markup.Escape(path), count.ToString(), Markup.Escape(detailStr));
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Found {matches.Count} files[/] with {matches.Sum(m => m.Count)} total blocks matching '{blockType}'");
        }

        if (errors > 0)
            AnsiConsole.MarkupLine($"[yellow]({errors} files had parse errors)[/]");
    }

    /// <summary>
    ///     Find all NIF files with embedded animation data.
    /// </summary>
    private static void FindAnimated(string folder, bool recursive)
    {
        if (!Directory.Exists(folder))
        {
            AnsiConsole.MarkupLine($"[red]Folder not found:[/] {folder}");
            return;
        }

        // Animation-related block types
        var animationBlockTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NiControllerSequence",
            "NiTransformController",
            "NiFloatInterpolator",
            "NiTransformInterpolator",
            "NiFloatData",
            "NiTransformData",
            "NiKeyframeController",
            "NiKeyframeData",
            "NiVisController",
            "NiBoolInterpolator",
            "NiBoolData",
            "NiPoint3Interpolator",
            "NiPosData",
            "NiBlendInterpolator",
            "NiBlendBoolInterpolator",
            "NiBlendFloatInterpolator",
            "NiBlendPoint3Interpolator",
            "NiBlendTransformInterpolator",
            "NiMultiTargetTransformController",
            "NiControllerManager",
            "NiTextKeyExtraData",
            "BSAnimNote",
            "BSAnimNotes",
        };

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var nifFiles = Directory.EnumerateFiles(folder, "*.nif", searchOption).ToList();

        AnsiConsole.MarkupLine($"[bold]Scanning {nifFiles.Count} NIF files for animation data...[/]");
        AnsiConsole.WriteLine();

        var matches = new List<AnimatedFileInfo>();
        var errors = 0;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning...", ctx =>
            {
                foreach (var file in nifFiles)
                {
                    ctx.Status($"Scanning: {Path.GetFileName(file)}");
                    try
                    {
                        var data = File.ReadAllBytes(file);
                        var nif = NifParser.Parse(data);

                        // Count animation blocks by type
                        var animBlocks = new Dictionary<string, int>();
                        for (var i = 0; i < nif.NumBlocks; i++)
                        {
                            var typeName = nif.GetBlockTypeName(i);
                            if (animationBlockTypes.Contains(typeName))
                            {
                                if (!animBlocks.TryGetValue(typeName, out var count))
                                    count = 0;
                                animBlocks[typeName] = count + 1;
                            }
                        }

                        if (animBlocks.Count > 0)
                        {
                            var relativePath = Path.GetRelativePath(folder, file);
                            matches.Add(new AnimatedFileInfo
                            {
                                Path = relativePath,
                                BlockCounts = animBlocks,
                                TotalAnimBlocks = animBlocks.Values.Sum(),
                                HasControllerSequence = animBlocks.ContainsKey("NiControllerSequence"),
                                HasFloatData = animBlocks.ContainsKey("NiFloatData"),
                                HasTransformData = animBlocks.ContainsKey("NiTransformData"),
                            });
                        }
                    }
                    catch
                    {
                        errors++;
                    }
                }
            });

        // Display results
        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No NIF files with embedded animations found.[/]");
        }
        else
        {
            // Summary table
            var summaryTable = new Table().Border(TableBorder.Rounded).Title("[bold]Animation Block Summary[/]");
            summaryTable.AddColumn("Block Type");
            summaryTable.AddColumn(new TableColumn("Files").RightAligned());
            summaryTable.AddColumn(new TableColumn("Total Blocks").RightAligned());

            var blockTypeSummary = matches
                .SelectMany(m => m.BlockCounts)
                .GroupBy(kv => kv.Key)
                .Select(g => (Type: g.Key, Files: g.Count(), Blocks: g.Sum(kv => kv.Value)))
                .OrderByDescending(x => x.Files)
                .ToList();

            foreach (var (type, files, blocks) in blockTypeSummary)
            {
                summaryTable.AddRow(type, files.ToString(), blocks.ToString());
            }

            AnsiConsole.Write(summaryTable);
            AnsiConsole.WriteLine();

            // Detailed file list
            var fileTable = new Table().Border(TableBorder.Rounded).Title("[bold]Files with Animation Data[/]");
            fileTable.AddColumn(new TableColumn("File").Width(55));
            fileTable.AddColumn(new TableColumn("Ctrl").RightAligned());
            fileTable.AddColumn(new TableColumn("Float").RightAligned());
            fileTable.AddColumn(new TableColumn("Xform").RightAligned());
            fileTable.AddColumn("Animation Types");

            // Sort by number of controller sequences (most interesting first)
            foreach (var info in matches.OrderByDescending(m => m.BlockCounts.GetValueOrDefault("NiControllerSequence", 0))
                                       .ThenByDescending(m => m.TotalAnimBlocks)
                                       .Take(100)) // Limit to first 100
            {
                var ctrlSeq = info.BlockCounts.GetValueOrDefault("NiControllerSequence", 0);
                var floatData = info.BlockCounts.GetValueOrDefault("NiFloatData", 0);
                var xformData = info.BlockCounts.GetValueOrDefault("NiTransformData", 0);

                var types = new List<string>();
                if (info.HasControllerSequence) types.Add("[green]Seq[/]");
                if (info.HasFloatData) types.Add("[blue]Float[/]");
                if (info.HasTransformData) types.Add("[yellow]Xform[/]");

                fileTable.AddRow(
                    Markup.Escape(info.Path),
                    ctrlSeq > 0 ? $"[green]{ctrlSeq}[/]" : "-",
                    floatData > 0 ? $"[blue]{floatData}[/]" : "-",
                    xformData > 0 ? $"[yellow]{xformData}[/]" : "-",
                    string.Join(" ", types));
            }

            AnsiConsole.Write(fileTable);

            if (matches.Count > 100)
                AnsiConsole.MarkupLine($"[dim](Showing first 100 of {matches.Count} files)[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Found {matches.Count} animated NIF files[/]");
        }

        if (errors > 0)
            AnsiConsole.MarkupLine($"[yellow]({errors} files had parse errors)[/]");
    }

    private class AnimatedFileInfo
    {
        public required string Path { get; init; }
        public required Dictionary<string, int> BlockCounts { get; init; }
        public int TotalAnimBlocks { get; init; }
        public bool HasControllerSequence { get; init; }
        public bool HasFloatData { get; init; }
        public bool HasTransformData { get; init; }
    }
}
