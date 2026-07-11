using System.CommandLine;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     Read-only DMP↔output actor ledger: enumerates every placed actor (ACHR/ACRE) the DMP
///     captured and every one the converted plugin emits, keyed by BASE identity (editor ID,
///     which survives FormID reallocation), and reports the per-base delta. The decisive
///     answer to "which named NPCs did the DMP place that the output lost?" — measured from
///     the two files directly, immune to the legacy-path log pollution documented in
///     memory/planner_dropstats_legacy_pollution.md. Writes nothing.
/// </summary>
internal static class ActorLedgerCommand
{
    private const uint RuntimeDynamicBase = 0xFF000000u;

    public static Command Create()
    {
        var command = new Command("actor-ledger",
            "Read-only DMP vs converted-plugin placed-actor ledger (ACHR/ACRE by base editor ID): " +
            "which actor bases lost placements in conversion, and where they were captured.");

        var dmpArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump (.dmp)" };
        var esmOpt = new Option<string>("--esm")
        {
            Description = "Path to the converted output plugin (.esm) to compare against",
            Required = true,
        };
        var pcEsmOpt = new Option<string>("--pc-esm")
        {
            Description = "Optional PC master ESM — improves base/worldspace name resolution for master FormIDs"
        };

        command.Arguments.Add(dmpArg);
        command.Options.Add(esmOpt);
        command.Options.Add(pcEsmOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var dmp = parseResult.GetValue(dmpArg)!;
            var esm = parseResult.GetValue(esmOpt)!;
            var pcEsm = parseResult.GetValue(pcEsmOpt);
            await RunAsync(dmp, esm, pcEsm, ct);
        });

        return command;
    }

    private static async Task RunAsync(string dmpPath, string esmPath, string? pcEsmPath, CancellationToken ct)
    {
        foreach (var (path, label) in new[] { (dmpPath, "DMP"), (esmPath, "--esm") })
        {
            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] {label} not found: {Markup.Escape(path)}");
                return;
            }
        }

        AnsiConsole.MarkupLine($"[blue]Actor ledger[/] — {Markup.Escape(Path.GetFileName(dmpPath))} vs {Markup.Escape(Path.GetFileName(esmPath))}");

        AnsiConsole.MarkupLine("[dim]Loading DMP (runtime scan; takes a minute)...[/]");
        using var dump = await SemanticFileLoader.LoadAsync(
            dmpPath, new SemanticFileLoadOptions { FileType = AnalysisFileType.Minidump }, ct);

        AnsiConsole.MarkupLine("[dim]Loading converted plugin...[/]");
        using var output = await SemanticFileLoader.LoadAsync(
            esmPath, new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile }, ct);

        var names = new BaseNameResolver();
        names.AddFrom(dump);
        names.AddFrom(output);
        if (pcEsmPath is not null && File.Exists(pcEsmPath))
        {
            AnsiConsole.MarkupLine("[dim]Loading master for name resolution...[/]");
            using var master = await SemanticFileLoader.LoadAsync(
                pcEsmPath, new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile }, ct);
            names.AddFrom(master);
        }

        var dmpSide = CollectPlacements(dump, names);
        var outSide = CollectPlacements(output, names);
        Print(dmpSide, outSide);
    }

    /// <summary>One base's placements on one side of the ledger.</summary>
    private sealed class BaseTally
    {
        public int Count;
        public readonly List<string> Cells = [];
    }

    private static Dictionary<string, BaseTally> CollectPlacements(
        UnifiedAnalysisResult file, BaseNameResolver names)
    {
        var tallies = new Dictionary<string, BaseTally>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in file.Records.Cells)
        {
            var cellLabel = names.CellLabel(cell);
            foreach (var placed in cell.PlacedObjects)
            {
                if (placed.RecordType is not ("ACHR" or "ACRE"))
                {
                    continue;
                }

                var key = names.BaseKey(placed);
                if (!tallies.TryGetValue(key, out var tally))
                {
                    tally = new BaseTally();
                    tallies[key] = tally;
                }

                tally.Count++;
                if (tally.Cells.Count < 4 && !tally.Cells.Contains(cellLabel))
                {
                    tally.Cells.Add(cellLabel);
                }
            }
        }

        return tallies;
    }

    private static void Print(
        Dictionary<string, BaseTally> dmpSide, Dictionary<string, BaseTally> outSide)
    {
        var dmpTotal = dmpSide.Values.Sum(t => t.Count);
        var outTotal = outSide.Values.Sum(t => t.Count);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[bold]Placed actors:[/] DMP={dmpTotal:N0} across {dmpSide.Count:N0} base(s)   " +
            $"OUTPUT={outTotal:N0} across {outSide.Count:N0} base(s)");
        AnsiConsole.WriteLine();

        // Bases the DMP placed that the output lacks entirely, or has fewer of.
        var losses = dmpSide
            .Select(kv => (Base: kv.Key, Dmp: kv.Value, Out: outSide.GetValueOrDefault(kv.Key)?.Count ?? 0))
            .Where(row => row.Out < row.Dmp.Count)
            .OrderByDescending(row => row.Dmp.Count - row.Out)
            .ThenBy(row => row.Base, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (losses.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No base lost placements: every DMP-placed actor base has at least as many output placements.[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("Base (editor ID)");
            table.AddColumn(new TableColumn("DMP").RightAligned());
            table.AddColumn(new TableColumn("OUT").RightAligned());
            table.AddColumn(new TableColumn("Δ").RightAligned());
            table.AddColumn("Captured in (sample cells)");
            foreach (var row in losses)
            {
                table.AddRow(
                    Markup.Escape(row.Base),
                    row.Dmp.Count.ToString("N0"),
                    row.Out.ToString("N0"),
                    (row.Dmp.Count - row.Out).ToString("N0"),
                    Markup.Escape(string.Join(", ", row.Dmp.Cells)));
            }

            AnsiConsole.MarkupLine($"[bold yellow]{losses.Count:N0} base(s) with fewer output placements:[/]");
            AnsiConsole.Write(table);
        }

        // Inverse view: output-only bases (recovered/rescued placements) — context, not a defect.
        var gains = outSide.Keys.Where(k => !dmpSide.ContainsKey(k)).OrderBy(k => k).ToList();
        if (gains.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[dim]{gains.Count:N0} base(s) appear only in the output (recovery/rescue re-pointing): " +
                $"{Markup.Escape(string.Join(", ", gains.Take(10)))}{(gains.Count > 10 ? ", …" : "")}[/]");
        }
    }

    /// <summary>
    ///     Resolves a placed actor to a stable ledger key and cells/worldspaces to readable
    ///     labels, from the union of every loaded file's NPC_/CREA/cell/worldspace identity.
    /// </summary>
    private sealed class BaseNameResolver
    {
        private readonly Dictionary<uint, string> _baseNames = [];
        private readonly Dictionary<uint, string> _worldspaceNames = [];

        public void AddFrom(UnifiedAnalysisResult file)
        {
            foreach (var npc in file.Records.Npcs)
            {
                if (!string.IsNullOrEmpty(npc.EditorId))
                {
                    _baseNames.TryAdd(npc.FormId, npc.EditorId);
                }
            }

            foreach (var crea in file.Records.Creatures)
            {
                if (!string.IsNullOrEmpty(crea.EditorId))
                {
                    _baseNames.TryAdd(crea.FormId, crea.EditorId);
                }
            }

            foreach (var wrld in file.Records.Worldspaces)
            {
                if (!string.IsNullOrEmpty(wrld.EditorId))
                {
                    _worldspaceNames.TryAdd(wrld.FormId, wrld.EditorId);
                }
            }
        }

        /// <summary>
        ///     Ledger key for a placed actor's base. Editor ID when known (stable across
        ///     FormID reallocation); a leveled-spawn 0xFF base resolves through its captured
        ///     ExtraLeveledCreature pointer the same way recovery re-points it.
        /// </summary>
        public string BaseKey(PlacedReference placed)
        {
            var baseId = placed.BaseFormId;
            if (baseId >= RuntimeDynamicBase)
            {
                var candidate = placed.LeveledCreatureOriginalBaseFormId
                                ?? placed.LeveledCreatureTemplateFormId;
                if (candidate is { } c && c != 0 && c < RuntimeDynamicBase)
                {
                    baseId = c;
                }
                else
                {
                    return "(leveled 0xFF, unresolvable)";
                }
            }

            if (_baseNames.TryGetValue(baseId, out var name))
            {
                return name;
            }

            return !string.IsNullOrEmpty(placed.BaseEditorId)
                ? placed.BaseEditorId
                : $"0x{baseId:X8}";
        }

        public string CellLabel(Core.Formats.Esm.Models.Records.World.CellRecord cell)
        {
            var worldspace = cell.WorldspaceFormId is { } ws
                ? _worldspaceNames.TryGetValue(ws, out var wsName) ? wsName : $"WRLD 0x{ws:X8}"
                : "interior";
            var cellName = !string.IsNullOrEmpty(cell.EditorId)
                ? cell.EditorId
                : $"0x{cell.FormId:X8} ({cell.GridX},{cell.GridY})";
            return $"{cellName} [{worldspace}]";
        }
    }
}
