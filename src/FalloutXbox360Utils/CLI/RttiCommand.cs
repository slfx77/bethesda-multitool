using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for resolving MSVC RTTI class names from vtable addresses in memory dumps.
/// </summary>
public static class RttiCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static Command Create()
    {
        var command = new Command("rtti", "Resolve C++ class names from vtable addresses via MSVC RTTI");

        var inputArg = new Argument<string>("input")
        {
            Description = "Path to memory dump file (.dmp)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var addressesArg = new Argument<string[]>("addresses")
        {
            Description = "Hex vtable virtual addresses (e.g., 0x82041204)",
            Arity = ArgumentArity.ZeroOrMore
        };
        var scanOpt = new Option<string?>("--scan")
        {
            Description = "Scan range: 0xSTART-0xEND (find all vtables in range)"
        };
        var strideOpt = new Option<int>("--stride")
        {
            Description = "Stride for scan mode (bytes between vtable slots)",
            DefaultValueFactory = _ => 4
        };
        var censusOpt = new Option<bool>("--census")
        {
            Description = "Full heap census: discover all C++ classes and instance counts"
        };
        var censusAllOpt = new Option<bool>("--census-all")
        {
            Description = "Run census across all DMP files in a directory and aggregate results"
        };
        var dirOpt = new Option<string>("--dir")
        {
            Description = "Directory containing DMP files (for --census-all)",
            DefaultValueFactory = _ => "Sample/MemoryDump"
        };
        var outputOpt = new Option<string>("--output")
        {
            Description = "Output path for JSON report (for --census-all)",
            DefaultValueFactory = _ => "TestOutput/rtti_census_all.json"
        };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(addressesArg);
        command.Options.Add(scanOpt);
        command.Options.Add(strideOpt);
        command.Options.Add(censusOpt);
        command.Options.Add(censusAllOpt);
        command.Options.Add(dirOpt);
        command.Options.Add(outputOpt);

        command.SetAction(parseResult =>
        {
            var censusAll = parseResult.GetValue(censusAllOpt);
            if (censusAll)
            {
                var dir = parseResult.GetValue(dirOpt)!;
                var output = parseResult.GetValue(outputOpt)!;
                ExecuteCensusAll(dir, output);
                return;
            }

            var input = parseResult.GetValue(inputArg);
            if (string.IsNullOrEmpty(input))
            {
                AnsiConsole.MarkupLine("[yellow]Usage:[/] rtti <dmp> <va> [<va2> ...] or rtti <dmp> --census or rtti --census-all");
                return;
            }

            var addresses = parseResult.GetValue(addressesArg) ?? [];
            var scan = parseResult.GetValue(scanOpt);
            var stride = parseResult.GetValue(strideOpt);
            var census = parseResult.GetValue(censusOpt);
            Execute(input, addresses, scan, stride, census);
        });

        return command;
    }

    private static void Execute(string input, string[] addresses, string? scanRange, int stride, bool census)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return;
        }

        var info = MinidumpParser.Parse(input);
        if (!info.IsValid)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid minidump file");
            return;
        }

        using var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new RttiReader(info, stream);

        if (addresses.Length == 0 && scanRange == null && !census)
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] rtti <dmp> <va> [<va2> ...] or rtti <dmp> --scan 0xSTART-0xEND or rtti <dmp> --census");
            return;
        }

        // Census mode
        if (census)
        {
            ExecuteCensus(reader);
            return;
        }

        // Individual address lookups
        if (addresses.Length > 0)
        {
            var results = new List<RttiResult>();
            foreach (var addr in addresses)
            {
                var va = ParseHexAddress(addr);
                if (va == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Invalid hex address: {addr}");
                    continue;
                }

                var result = reader.ResolveVtable(va.Value);
                if (result != null)
                {
                    results.Add(result);
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]0x{va.Value:X8}:[/] RTTI not resolved (memory not captured or invalid chain)");
                }
            }

            if (results.Count > 0)
            {
                PrintResults(results);
            }
        }

        // Scan mode
        if (scanRange != null)
        {
            var range = ParseScanRange(scanRange);
            if (range == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Invalid scan range. Use format: 0xSTART-0xEND");
                return;
            }

            AnsiConsole.MarkupLine($"[blue]Scanning[/] 0x{range.Value.start:X8}–0x{range.Value.end:X8} (stride={stride})...");
            var results = reader.ScanRange(range.Value.start, range.Value.end, stride);

            if (results.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No RTTI vtables found in range.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]Found {results.Count} unique vtable type(s).[/]");
                PrintResults(results);
            }
        }
    }

    private static void ExecuteCensus(RttiReader reader)
    {
        List<CensusEntry> entries = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("[blue]Scanning heap for vtable pointers...[/]", ctx =>
            {
                entries = reader.RunCensus((scanned, total, bytes) =>
                {
                    ctx.Status($"[blue]Scanning heap regions[/] {scanned}/{total} ({bytes / (1024 * 1024)} MB)");
                });
            });

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No RTTI-bearing objects found in heap.[/]");
            return;
        }

        var tesFormEntries = entries.Where(e => e.IsTesForm).ToList();
        var otherEntries = entries.Where(e => !e.IsTesForm).ToList();
        var totalInstances = entries.Sum(e => e.InstanceCount);

        AnsiConsole.MarkupLine($"\n[green]Census complete:[/] {entries.Count} C++ classes, {totalInstances:N0} total instances");
        AnsiConsole.MarkupLine($"  [cyan]TESForm-derived:[/] {tesFormEntries.Count} classes");
        AnsiConsole.MarkupLine($"  [grey]Other classes:[/] {otherEntries.Count} classes\n");

        // TESForm-derived classes table
        if (tesFormEntries.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold cyan]═══ TESForm-Derived Classes ═══[/]\n");
            PrintCensusTable(tesFormEntries);
        }

        // Other classes (top 50)
        if (otherEntries.Count > 0)
        {
            AnsiConsole.MarkupLine($"\n[bold grey]═══ Other Classes (top 50 of {otherEntries.Count}) ═══[/]\n");
            PrintCensusTable(otherEntries.Take(50).ToList());
        }
    }

    private static void PrintCensusTable(List<CensusEntry> entries)
    {
        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[bold]#[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Class[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Instances[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Vtable[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Offset[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Bases[/]").LeftAligned());

        var rank = 1;
        foreach (var entry in entries)
        {
            var r = entry.Rtti;
            var offsetText = r.ObjectOffset == 0
                ? "[green]0[/]"
                : $"[yellow]{r.ObjectOffset}[/]";

            var basesText = "";
            if (r.BaseClasses is { Count: > 1 })
            {
                var bases = r.BaseClasses.Skip(1).Select(b => b.ClassName).ToList();
                basesText = string.Join(", ", bases);
            }

            var classColor = entry.IsTesForm ? "cyan" : "white";

            table.AddRow(
                $"{rank++}",
                $"[{classColor}]{Markup.Escape(r.ClassName)}[/]",
                $"[bold]{entry.InstanceCount:N0}[/]",
                $"0x{r.VtableVA:X8}",
                offsetText,
                Markup.Escape(basesText));
        }

        AnsiConsole.Write(table);
    }

    private static void ExecuteCensusAll(string directory, string outputPath)
    {
        if (!Directory.Exists(directory))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {directory}");
            return;
        }

        var dmpFiles = Directory.GetFiles(directory, "*.dmp")
            .OrderBy(f => f)
            .ToList();

        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No .dmp files found in {directory}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Found {dmpFiles.Count} DMP files in {directory}[/]\n");

        var dumpResults = new List<DumpCensusResult>();

        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .Start(ctx =>
            {
                for (var i = 0; i < dmpFiles.Count; i++)
                {
                    var filePath = dmpFiles[i];
                    var fileName = Path.GetFileName(filePath);
                    var label = $"[[{i + 1}/{dmpFiles.Count}]] {Markup.Escape(fileName)}";
                    var task = ctx.AddTask(label, maxValue: 100);

                    try
                    {
                        var info = MinidumpParser.Parse(filePath);
                        if (!info.IsValid)
                        {
                            task.Description = $"[red]{Markup.Escape(fileName)}[/] [grey](invalid minidump)[/]";
                            task.Value = 100;
                            continue;
                        }

                        var buildType = MinidumpAnalyzer.DetectBuildType(info);
                        var gameModule = MinidumpAnalyzer.FindGameModule(info);
                        var peTimestamp = gameModule?.TimeDateStamp;

                        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var reader = new RttiReader(info, stream);

                        var entries = reader.RunCensus((scanned, total, _) =>
                        {
                            task.Value = total > 0 ? (double)scanned / total * 100 : 0;
                        });

                        var totalInstances = entries.Sum(e => e.InstanceCount);

                        dumpResults.Add(new DumpCensusResult
                        {
                            FileName = fileName,
                            FilePath = filePath,
                            BuildType = buildType,
                            PeTimestamp = peTimestamp,
                            ClassCount = entries.Count,
                            TotalInstances = totalInstances,
                            Entries = entries
                        });

                        task.Value = 100;
                        task.Description = $"[green]{Markup.Escape(fileName)}[/] [grey]({entries.Count} classes, {totalInstances:N0} instances)[/]";
                    }
                    catch (Exception ex)
                    {
                        task.Description = $"[red]{Markup.Escape(fileName)}[/] [grey]({Markup.Escape(ex.Message)})[/]";
                        task.Value = 100;
                    }
                }
            });

        if (dumpResults.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No dumps processed successfully.");
            return;
        }

        // Aggregate
        var report = RttiCensusAggregator.Aggregate(dumpResults);

        // Console output
        PrintAggregatedSummary(report);

        // JSON output (strip per-dump Entries to keep file size manageable)
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        File.WriteAllText(outputPath, json);
        AnsiConsole.MarkupLine($"\n[green]Full report saved:[/] {outputPath}");
    }

    private static void PrintAggregatedSummary(AggregatedCensusReport report)
    {
        AnsiConsole.MarkupLine($"\n[bold green]═══ RTTI Census: All Dumps ═══[/]\n");
        AnsiConsole.MarkupLine($"Processed: [bold]{report.TotalDumps}[/] dumps");
        AnsiConsole.MarkupLine($"Classes found: [bold]{report.TotalClasses}[/] unique C++ classes");
        AnsiConsole.MarkupLine($"Total instances: [bold]{report.TotalInstances:N0}[/] (across all dumps)");
        AnsiConsole.MarkupLine($"TESForm-derived: [bold cyan]{report.TesFormClasses}[/] classes\n");

        // Build type breakdown
        var buildGroups = report.Dumps
            .GroupBy(d => d.BuildType ?? "Unknown")
            .OrderByDescending(g => g.Count())
            .ToList();

        var btTable = new Table();
        btTable.Border(TableBorder.Rounded);
        btTable.AddColumn("[bold]Build Type[/]");
        btTable.AddColumn(new TableColumn("[bold]Dumps[/]").RightAligned());
        btTable.AddColumn(new TableColumn("[bold]Avg Classes[/]").RightAligned());
        btTable.AddColumn(new TableColumn("[bold]Avg Instances[/]").RightAligned());

        foreach (var group in buildGroups)
        {
            var dumps = group.ToList();
            var avgClasses = dumps.Average(d => d.ClassCount);
            var avgInstances = dumps.Average(d => d.TotalInstances);
            btTable.AddRow(
                Markup.Escape(group.Key),
                $"{dumps.Count}",
                $"{avgClasses:N0}",
                $"{avgInstances:N0}");
        }

        AnsiConsole.Write(btTable);

        // TESForm-derived classes (deduplicated by class name, primary vtable only)
        var tesFormClasses = report.Classes
            .Where(e => e.IsTesForm)
            .OrderByDescending(e => e.TotalInstances)
            .ToList();

        if (tesFormClasses.Count > 0)
        {
            AnsiConsole.MarkupLine($"\n[bold cyan]═══ TESForm-Derived Classes ({tesFormClasses.Count}) ═══[/]\n");
            PrintAggregatedTable(tesFormClasses, report.TotalDumps);
        }

        // Other classes (top 30)
        var otherClasses = report.Classes
            .Where(e => !e.IsTesForm)
            .OrderByDescending(e => e.TotalInstances)
            .Take(30)
            .ToList();

        var totalOther = report.Classes.Count(e => !e.IsTesForm);
        if (otherClasses.Count > 0)
        {
            AnsiConsole.MarkupLine($"\n[bold grey]═══ Other Classes (top 30 of {totalOther}) ═══[/]\n");
            PrintAggregatedTable(otherClasses, report.TotalDumps);
        }
    }

    private static void PrintAggregatedTable(List<AggregatedCensusEntry> entries, int totalDumps)
    {
        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[bold]#[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Class[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Total[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Dumps[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Avg/Dump[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Bases[/]").LeftAligned());

        var rank = 1;
        foreach (var entry in entries)
        {
            var avgPerDump = entry.DumpsPresent > 0 ? entry.TotalInstances / entry.DumpsPresent : 0;
            var basesText = entry.BaseClassNames != null
                ? string.Join(", ", entry.BaseClassNames)
                : "";

            var classColor = entry.IsTesForm ? "cyan" : "white";

            table.AddRow(
                $"{rank++}",
                $"[{classColor}]{Markup.Escape(entry.ClassName)}[/]",
                $"[bold]{entry.TotalInstances:N0}[/]",
                $"{entry.DumpsPresent}/{totalDumps}",
                $"{avgPerDump:N0}",
                Markup.Escape(basesText));
        }

        AnsiConsole.Write(table);
    }

    private static void PrintResults(List<RttiResult> results)
    {
        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[bold]Vtable[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Class[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Offset[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Bases[/]").LeftAligned());

        foreach (var result in results)
        {
            var offsetText = result.ObjectOffset == 0
                ? "[green]0 (primary)[/]"
                : $"[yellow]{result.ObjectOffset}[/]";

            var basesText = "";
            if (result.BaseClasses is { Count: > 0 })
            {
                // Skip the first entry (it's the class itself)
                var bases = result.BaseClasses
                    .Skip(1)
                    .Select(b => b.ClassName)
                    .ToList();
                basesText = bases.Count > 0 ? string.Join(", ", bases) : "";
            }

            table.AddRow(
                $"0x{result.VtableVA:X8}",
                $"[green]{Markup.Escape(result.ClassName)}[/]",
                offsetText,
                Markup.Escape(basesText));
        }

        AnsiConsole.Write(table);

        // Print detailed hierarchy for each result
        foreach (var result in results)
        {
            if (result.BaseClasses is not { Count: > 1 })
            {
                continue;
            }

            AnsiConsole.MarkupLine($"\n[blue]Hierarchy for[/] [green]{Markup.Escape(result.ClassName)}[/] (vtable 0x{result.VtableVA:X8}):");

            var tree = new Tree($"[green]{Markup.Escape(result.ClassName)}[/]");
            foreach (var baseClass in result.BaseClasses.Skip(1))
            {
                var label = baseClass.MemberDisplacement != 0
                    ? $"{Markup.Escape(baseClass.ClassName)} [grey](+{baseClass.MemberDisplacement})[/]"
                    : Markup.Escape(baseClass.ClassName);
                tree.AddNode(label);
            }

            AnsiConsole.Write(tree);
        }
    }

    private static uint? ParseHexAddress(string hex)
    {
        var s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    private static (uint start, uint end)? ParseScanRange(string range)
    {
        var parts = range.Split('-', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        var start = ParseHexAddress(parts[0]);
        var end = ParseHexAddress(parts[1]);

        if (start == null || end == null || start.Value >= end.Value)
        {
            return null;
        }

        return (start.Value, end.Value);
    }
}
