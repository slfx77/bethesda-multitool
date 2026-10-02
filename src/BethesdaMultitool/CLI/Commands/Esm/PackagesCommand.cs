using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.AiPackages;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Esm;

/// <summary>
///     CLI subcommand for reporting AI package data from ESM/DMP files.
///     <para>
///         <c>-f json</c> writes exactly one <see cref="PackageJsonWriter" /> document to stdout and nothing
///         else: progress, status, errors and <see cref="Logger" /> output go to stderr. Exit codes: 0 on
///         success, 1 for parser errors or a missing/unreadable input, 2 for an unknown format or negative limit.
///     </para>
///     <para>
///         Conditions: the text table's last column (<c>Cond</c>) counts each package's CTDA conditions, and a
///         <c>Package conditions</c> block after the tables prints every shown package that has any, one
///         <see cref="ConditionTextFormatter.FormatLine(ConditionDescription)" /> per condition in stored order. JSON carries the same
///         descriptions as <c>conditions</c> beside the unchanged <c>conditionsRaw</c>. Both are described
///         from one <see cref="ConditionDisplayContext" /> built over the loaded records, so
///         <c>GetQuestVariable</c> names its variable when the quest's script declares it.
///     </para>
/// </summary>
public static class PackagesCommand
{
    private const int ExitSuccess = 0;
    private const int ExitFailure = 1;
    private const int ExitInvalidArgument = 2;

    public static Command Create()
    {
        var command = new Command("packages",
            "Report AI packages (PACK records) with decoded types, schedules, and flags");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM or DMP file" };
        var typeOpt = new Option<string?>("-t", "--type")
            { Description = "Filter by package type (e.g., Sandbox, Patrol)" };
        var npcOpt = new Option<string?>("--npc")
            { Description = "Filter to packages used by a specific NPC (editor ID)" };
        var limitOpt = new Option<int>("-l", "--limit")
        {
            Description = "Limit number of packages shown",
            DefaultValueFactory = _ => 50
        };
        var formatOpt = new Option<string>("-f", "--format")
        {
            Description = "Output format: text, json (json writes one document to stdout; progress goes to stderr)",
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
            return await RunAsync(input, typeFilter, npcFilter, limit, format, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        string input,
        string? typeFilter,
        string? npcFilter,
        int limit,
        string format,
        CancellationToken cancellationToken)
    {
        bool json;
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            json = true;
        }
        else if (format.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            json = false;
        }
        else
        {
            await Console.Error.WriteLineAsync($"Error: unknown --format '{format}'. Expected: text, json.");
            return ExitInvalidArgument;
        }

        if (limit < 0)
        {
            await Console.Error.WriteLineAsync($"Error: --limit must be zero or greater (got {limit}).");
            return ExitInvalidArgument;
        }

        // In JSON mode stdout carries the document alone. Logger's AsyncLocal redirect is scoped to
        // this call, so it covers every load/parse log line without leaking past the command.
        if (json)
        {
            Logger.SetOutput(Console.Error);
        }

        var console = CliConsoles.ForStatus(json);

        if (!File.Exists(input))
        {
            console.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return ExitFailure;
        }

        using var loaded = await CliSemanticLoader.TryLoadAsync(
            input,
            "Loading package data...",
            new SemanticFileLoadOptions(),
            cancellationToken,
            console);
        if (loaded == null)
        {
            return ExitFailure;
        }

        var packages = loaded.Records.Packages;
        var resolver = loaded.Resolver;
        var semanticResult = loaded.Records;

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

        var matched = filtered.ToList();
        var results = matched.Take(limit).ToList();

        // One context for the whole run: its quest-variable index is built once, lazily, from every quest
        // the source holds (a filtered-out package's quest still names the shown package's variable).
        var conditionContext = ConditionDisplayContext.From(semanticResult, resolver);
        var isMemoryDumpInput = loaded.FileType == AnalysisFileType.Minidump;

        if (json)
        {
            var document = new PackageJsonDocument(
                Path.GetFullPath(input),
                semanticResult.Game,
                CliConsoles.ToolVersion,
                packages.Count,
                matched.Count,
                results,
                string.IsNullOrEmpty(typeFilter) ? null : typeFilter,
                string.IsNullOrEmpty(npcFilter) ? null : npcFilter,
                limit);

            await using var stdout = Console.OpenStandardOutput();
            await CliJsonDocumentWriter.WriteAsync(stdout,
                buffer => PackageJsonWriter.Write(buffer, document, resolver, conditionContext, isMemoryDumpInput),
                cancellationToken);
            return ExitSuccess;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Total packages:[/] {0}    [blue]Showing:[/] {1}",
            packages.Count, results.Count);
        AnsiConsole.WriteLine();

        PrintTable(results, resolver);
        PrintConditions(results, conditionContext, isMemoryDumpInput);
        return ExitSuccess;
    }

    private static void PrintTable(List<PackageRecord> packages, FormIdResolver resolver)
    {
        // "Cond" is appended LAST so every existing column keeps its position.
        var table = new Table()
            .AddColumn("FormID")
            .AddColumn("EditorID")
            .AddColumn("Type")
            .AddColumn("Schedule")
            .AddColumn("Location")
            .AddColumn("Target")
            .AddColumn("Flags")
            .AddColumn("Cond");

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
            var conditionCount = pkg.Conditions.Count.ToString(CultureInfo.InvariantCulture);

            // Table.AddRow(string[]) parses every cell as Spectre markup, and EditorIDs and resolved
            // names are data: a bracketed EditorID either throws or silently turns into a style tag.
            table.AddRow(
                Markup.Escape(formIdStr),
                Markup.Escape(editorId),
                Markup.Escape(typeName),
                Markup.Escape(schedule),
                Markup.Escape(location),
                Markup.Escape(target),
                Markup.Escape(flags),
                Markup.Escape(conditionCount));
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
            summaryTable.AddRow(Markup.Escape(typeName), count.ToString());
        }

        AnsiConsole.Write(summaryTable);
    }

    /// <summary>
    ///     The <c>Package conditions</c> block, printed after the tables so every earlier line keeps its place:
    ///     for each shown package that has conditions, a header <c>EDID [0xFORMID] (N conditions)</c>, one
    ///     numbered <see cref="ConditionTextFormatter.FormatLine(ConditionDescription)" /> per condition in stored order, and, when the
    ///     list has more than one condition or a stray OR flag, the grouping summary (labeled as the GECK
    ///     convention, not asserted as engine behavior).
    ///     <para>
    ///         The block is written through the console's raw output writer, never as markup. Nothing in it is
    ///         parsed, so data needs no <see cref="Markup.Escape" />: a bracketed EditorID, <c>[0x000F2429]</c>
    ///         and <c>[Run On: Subject]</c> print literally (escaping them for a raw writer would print doubled
    ///         brackets). Nothing is wrapped at the console width either, so each condition stays on one
    ///         grep-able line even when stdout is redirected.
    ///     </para>
    /// </summary>
    private static void PrintConditions(
        List<PackageRecord> packages,
        ConditionDisplayContext context,
        bool isMemoryDumpInput)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Package conditions[/]").LeftJustified());

        var output = AnsiConsole.Profile.Out.Writer;
        if (context.GameAssumed)
        {
            output.WriteLine(
                $"Note: the game was not detected; condition functions are named from the {context.Game} table (assumed).");
        }

        if (isMemoryDumpInput)
        {
            output.WriteLine(
                $"Note: memory-dump input. Cond 0: conditions {ScriptSourceProvenance.PartialDumpAbsenceWording}.");
        }

        var printed = 0;
        foreach (var package in packages)
        {
            if (package.Conditions.Count == 0)
            {
                continue;
            }

            var described = ConditionDescriber.DescribeAll(package.Conditions, context);
            if (printed > 0)
            {
                output.WriteLine();
            }

            var count = described.Count.ToString(CultureInfo.InvariantCulture);
            output.WriteLine(
                $"{ConditionTextFormatter.FormatFormId(package.FormId, package.EditorId)} " +
                $"({count} {(described.Count == 1 ? "condition" : "conditions")})");
            foreach (var description in described)
            {
                output.WriteLine(
                    $"  {description.Index.ToString(CultureInfo.InvariantCulture)}: " +
                    ConditionTextFormatter.FormatLine(description));
            }

            if (ConditionTextFormatter.FormatLogicSummary(described) is { } logic)
            {
                output.WriteLine($"  Logic: {logic}");
            }

            printed++;
        }

        if (printed == 0)
        {
            output.WriteLine("(none of the shown packages has conditions)");
        }

        output.Flush();
    }

    internal static string FormatLocation(PackageLocation? loc, FormIdResolver resolver)
    {
        if (loc == null)
        {
            return "";
        }

        return $"{loc.TypeName}: {RecordDetailHelpers.FormatPackageLocation(loc, resolver)}";
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

    /// <summary>Plain-text flag summary; the caller escapes it for markup.</summary>
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
            parts.Add($"[FO] {fo}");
        }

        if (pkg.Data.TypeSpecificFlags != 0)
        {
            var ts = FlagRegistry.DecodeFlagNames(pkg.Data.TypeSpecificFlags,
                FlagRegistry.PackageTypeSpecificFlags);
            parts.Add($"[Type] {ts}");
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
}
