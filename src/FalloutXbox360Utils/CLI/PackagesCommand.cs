using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI subcommand for reporting AI package data from ESM/DMP files.
/// </summary>
public static class PackagesCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static Command Create()
    {
        var command = new Command("packages", "Report AI packages (PACK records) with decoded types, schedules, and flags");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM or DMP file" };
        var typeOpt = new Option<string?>("-t", "--type") { Description = "Filter by package type (e.g., Sandbox, Patrol)" };
        var npcOpt = new Option<string?>("--npc") { Description = "Filter to packages used by a specific NPC (editor ID)" };
        var limitOpt = new Option<int>("-l", "--limit")
        {
            Description = "Limit number of packages shown",
            DefaultValueFactory = _ => 50
        };
        var formatOpt = new Option<string>("-f", "--format")
        {
            Description = "Output format: text, json",
            DefaultValueFactory = _ => "text"
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(typeOpt);
        command.Options.Add(npcOpt);
        command.Options.Add(limitOpt);
        command.Options.Add(formatOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var typeFilter = parseResult.GetValue(typeOpt);
            var npcFilter = parseResult.GetValue(npcOpt);
            var limit = parseResult.GetValue(limitOpt);
            var format = parseResult.GetValue(formatOpt)!;
            await RunAsync(input, typeFilter, npcFilter, limit, format, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(
        string input,
        string? typeFilter,
        string? npcFilter,
        int limit,
        string format,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        AnsiConsole.MarkupLine("[blue]Loading:[/] {0}", Path.GetFileName(input));

        var analysisResult = await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Analyzing file...", maxValue: 100);
                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Description = p.Phase;
                    task.Value = p.PercentComplete;
                });

                return await EsmFileAnalyzer.AnalyzeAsync(input, progress, cancellationToken);
            });

        if (analysisResult.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No ESM records found in file.");
            return;
        }

        AnsiConsole.MarkupLine("[blue]Reconstructing records...[/]");

        var fileInfo = new FileInfo(input);
        RecordCollection semanticResult;
        using (var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read))
        {
            var parser = new RecordParser(
                analysisResult.EsmRecords, analysisResult.FormIdMap, accessor, fileInfo.Length,
                analysisResult.MinidumpInfo);
            semanticResult = parser.ReconstructAll();
        }

        var packages = semanticResult.Packages;
        var resolver = semanticResult.CreateResolver();

        // Apply filters
        IEnumerable<PackageRecord> filtered = packages;

        if (!string.IsNullOrEmpty(typeFilter))
        {
            filtered = filtered.Where(p =>
                p.TypeName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(npcFilter))
        {
            // Find NPCs matching the filter and collect their package FormIDs
            var npcPackageIds = new HashSet<uint>();
            foreach (var npc in semanticResult.Npcs)
            {
                if (npc.EditorId != null &&
                    npc.EditorId.Contains(npcFilter, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var pkgId in npc.Packages)
                    {
                        npcPackageIds.Add(pkgId);
                    }
                }
            }

            filtered = filtered.Where(p => npcPackageIds.Contains(p.FormId));
        }

        var results = filtered.Take(limit).ToList();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Total packages:[/] {0}    [blue]Showing:[/] {1}",
            packages.Count, results.Count);
        AnsiConsole.WriteLine();

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            PrintJson(results, resolver);
        }
        else
        {
            PrintTable(results, resolver);
        }
    }

    private static void PrintTable(List<PackageRecord> packages, FormIdResolver resolver)
    {
        var table = new Table()
            .AddColumn("FormID")
            .AddColumn("EditorID")
            .AddColumn("Type")
            .AddColumn("Schedule")
            .AddColumn("Location")
            .AddColumn("Target")
            .AddColumn("Flags");

        table.Border(TableBorder.Rounded);

        foreach (var pkg in packages)
        {
            var formIdStr = $"0x{pkg.FormId:X8}";
            var editorId = pkg.EditorId ?? "";
            var typeName = pkg.TypeName;

            var schedule = pkg.Schedule?.Summary ?? "";

            var location = FormatLocation(pkg.Location, resolver);
            var target = FormatTarget(pkg.Target, resolver);
            var flags = BuildFlagsDisplay(pkg);

            table.AddRow(formIdStr, editorId, typeName, schedule, location, target, flags);
        }

        AnsiConsole.Write(table);

        // Summary by type
        var typeCounts = packages
            .GroupBy(p => p.TypeName)
            .OrderByDescending(g => g.Count())
            .Select(g => (g.Key, g.Count()));

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Package Types[/]").LeftJustified());

        var summaryTable = new Table()
            .AddColumn("Type")
            .AddColumn("Count");
        summaryTable.Border(TableBorder.Simple);

        foreach (var (typeName, count) in typeCounts)
        {
            summaryTable.AddRow(typeName, count.ToString());
        }

        AnsiConsole.Write(summaryTable);
    }

    private static string FormatLocation(PackageLocation? loc, FormIdResolver resolver)
    {
        if (loc == null)
        {
            return "";
        }

        var name = resolver.GetBestNameWithRefChain(loc.Union);
        var locStr = loc.Type switch
        {
            0 => $"Near: {name ?? $"0x{loc.Union:X8}"}",
            1 => $"Cell: {name ?? $"0x{loc.Union:X8}"}",
            2 => $"Near Current: {name ?? $"0x{loc.Union:X8}"}",
            3 => $"Near Editor: {name ?? $"0x{loc.Union:X8}"}",
            4 => $"ObjID: {name ?? $"0x{loc.Union:X8}"}",
            5 => $"ObjType: {loc.Union}",
            6 => $"Near Linked Ref: {name ?? $"0x{loc.Union:X8}"}",
            7 => $"At Pkg Location: {name ?? $"0x{loc.Union:X8}"}",
            _ => $"Type{loc.Type}: 0x{loc.Union:X8}"
        };

        if (loc.Radius > 0)
        {
            locStr += $" r={loc.Radius}";
        }

        return locStr;
    }

    private static string FormatTarget(PackageTarget? tgt, FormIdResolver resolver)
    {
        if (tgt == null)
        {
            return "";
        }

        var tgtName = tgt.Type is 0 or 1
            ? resolver.GetBestNameWithRefChain(tgt.FormIdOrType)
            : null;
        return $"{tgt.TypeName}: {tgtName ?? $"0x{tgt.FormIdOrType:X8}"}";
    }

    private static string BuildFlagsDisplay(PackageRecord pkg)
    {
        if (pkg.Data == null)
        {
            return "";
        }

        var parts = new List<string>(4);

        var general = FlagRegistry.DecodeFlagNames(pkg.Data.GeneralFlags, FlagRegistry.PackageGeneralFlags);
        if (general != "None")
        {
            parts.Add(general);
        }

        if (pkg.Data.FalloutBehaviorFlags != 0)
        {
            var fo = FlagRegistry.DecodeFlagNames(pkg.Data.FalloutBehaviorFlags,
                FlagRegistry.PackageFOBehaviorFlags);
            parts.Add($"[[FO]] {fo}");
        }

        if (pkg.Data.TypeSpecificFlags != 0)
        {
            var ts = FlagRegistry.DecodeFlagNames(pkg.Data.TypeSpecificFlags,
                FlagRegistry.PackageTypeSpecificFlags);
            parts.Add($"[[Type]] {ts}");
        }

        if (pkg.IsRepeatable)
        {
            parts.Add("Repeatable");
        }

        if (pkg.IsStartingLocationLinkedRef)
        {
            parts.Add("Start at Linked Ref");
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : "None";
    }

    private static void PrintJson(List<PackageRecord> packages, FormIdResolver resolver)
    {
        var items = packages.Select(pkg => new
        {
            formId = $"0x{pkg.FormId:X8}",
            editorId = pkg.EditorId,
            type = pkg.TypeName,
            typeCode = pkg.Data?.Type,
            schedule = pkg.Schedule != null
                ? new
                {
                    summary = pkg.Schedule.Summary,
                    month = (int)pkg.Schedule.Month,
                    dayOfWeek = (int)pkg.Schedule.DayOfWeek,
                    date = (int)pkg.Schedule.Date,
                    time = (int)pkg.Schedule.Time,
                    durationHours = pkg.Schedule.Duration
                }
                : null,
            location = pkg.Location != null
                ? new
                {
                    type = (int)pkg.Location.Type,
                    union = $"0x{pkg.Location.Union:X8}",
                    unionEditorId = resolver.GetBestNameWithRefChain(pkg.Location.Union),
                    radius = pkg.Location.Radius
                }
                : null,
            target = pkg.Target != null
                ? new
                {
                    type = (int)pkg.Target.Type,
                    typeName = pkg.Target.TypeName,
                    formIdOrType = $"0x{pkg.Target.FormIdOrType:X8}",
                    editorId = pkg.Target.Type is 0 or 1
                        ? resolver.GetBestNameWithRefChain(pkg.Target.FormIdOrType)
                        : null,
                    countDistance = pkg.Target.CountDistance
                }
                : null,
            generalFlags = pkg.Data != null
                ? FlagRegistry.DecodeFlagNames(pkg.Data.GeneralFlags, FlagRegistry.PackageGeneralFlags)
                : null,
            generalFlagsRaw = pkg.Data?.GeneralFlags,
            foBehaviorFlags = pkg.Data != null && pkg.Data.FalloutBehaviorFlags != 0
                ? FlagRegistry.DecodeFlagNames(pkg.Data.FalloutBehaviorFlags, FlagRegistry.PackageFOBehaviorFlags)
                : null,
            typeSpecificFlags = pkg.Data != null && pkg.Data.TypeSpecificFlags != 0
                ? FlagRegistry.DecodeFlagNames(pkg.Data.TypeSpecificFlags, FlagRegistry.PackageTypeSpecificFlags)
                : null,
            isRepeatable = pkg.IsRepeatable ? true : (bool?)null,
            startingLocationLinkedRef = pkg.IsStartingLocationLinkedRef ? true : (bool?)null
        });

        var json = JsonSerializer.Serialize(items, JsonOptions);
        Console.WriteLine(json);
    }
}
