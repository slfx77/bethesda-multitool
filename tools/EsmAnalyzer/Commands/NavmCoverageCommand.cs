using System.CommandLine;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     One-shot NAVM coverage diagnostic. Quantifies whether the runtime NavMesh reader's
///     count-only extraction leaves a LAND-style gap: NAVMs whose runtime memory carries
///     vertex/triangle data but whose ESM-parsed RawSubrecords are empty (so neither the
///     GUI overlay nor the ESP encoder can use them).
/// </summary>
internal static class NavmCoverageCommand
{
    public static Command Create()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "Path to DMP file"
        };

        var cmd = new Command("navm-coverage", "Report NAVM RawSubrecords coverage on a DMP")
        {
            inputArg
        };

        cmd.SetAction(async (parseResult, ct) =>
        {
            var path = parseResult.GetValue(inputArg)!;
            await RunAsync(path, ct);
            return 0;
        });

        return cmd;
    }

    private static async Task RunAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(path)}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Loading[/] {Markup.Escape(path)}");
        using var loaded = await SemanticFileLoader.LoadAsync(
            path,
            new SemanticFileLoadOptions { FileType = AnalysisFileType.Minidump },
            ct);

        var nm = loaded.Records.NavMeshes;
        if (nm.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No NAVM records detected.[/]");
            return;
        }

        var hasRawSubs = nm.Count(n => n.RawSubrecords.Count > 0);
        var emptyRawSubs = nm.Count - hasRawSubs;
        var hasVerts = nm.Count(n => n.VertexCount > 0);
        var emptyRawButHasVerts = nm.Count(n => n.RawSubrecords.Count == 0 && n.VertexCount > 0);
        var totalVerts = nm.Sum(n => (long)n.VertexCount);
        var totalTris = nm.Sum(n => (long)n.TriangleCount);

        // Cross-check runtime editor-id map: the BSTHashMap<BSFixedString, TESForm*> is
        // STRING-keyed. NAVMs typically have no editor-id, so they likely never appear here
        // even though their BSNavMesh structs DO live in runtime memory. If the runtime
        // editor-id count is far below the cell count, MergeRuntimeRecords can never surface
        // runtime-only navmeshes — the architectural gap isn't in RuntimeNavMeshReader, it's
        // upstream in how NAVMs are even discovered.
        var runtimeNavmEntries = loaded.RawResult.EsmRecords?.RuntimeEditorIds?
            .Count(e => e.FormType == 0x43) ?? 0;
        var parsedWithEditorId = nm.Count(n => !string.IsNullOrEmpty(n.EditorId));
        var cellCount = loaded.Records.Cells.Count;
        var exteriorCellCount = loaded.Records.Cells.Count(c => !c.IsInterior);

        AnsiConsole.MarkupLine($"[bold]NAVM coverage on[/] {Markup.Escape(Path.GetFileName(path))}");
        AnsiConsole.MarkupLine($"  Total NAVMs:                {nm.Count:N0}");
        AnsiConsole.MarkupLine($"  With RawSubrecords:         [green]{hasRawSubs:N0}[/] " +
                               $"({100.0 * hasRawSubs / nm.Count:F1}%)");
        AnsiConsole.MarkupLine($"  Empty RawSubrecords:        [yellow]{emptyRawSubs:N0}[/]");
        AnsiConsole.MarkupLine($"  Empty but VertexCount > 0:  [red]{emptyRawButHasVerts:N0}[/] " +
                               $"(runtime-only — LAND-style gap)");
        AnsiConsole.MarkupLine($"  Records with vertices:      {hasVerts:N0}");
        AnsiConsole.MarkupLine($"  With non-empty EditorId:    {parsedWithEditorId:N0}");
        AnsiConsole.MarkupLine($"  Total VertexCount across all NAVMs:   {totalVerts:N0}");
        AnsiConsole.MarkupLine($"  Total TriangleCount across all NAVMs: {totalTris:N0}");
        AnsiConsole.MarkupLine($"  [grey]---[/]");
        AnsiConsole.MarkupLine($"  Cells in DMP:               {cellCount:N0} ({exteriorCellCount:N0} exterior)");
        AnsiConsole.MarkupLine($"  Runtime editor-id entries with FormType 0x43:  " +
                               $"[bold]{runtimeNavmEntries:N0}[/]");
        AnsiConsole.MarkupLine(runtimeNavmEntries == 0
            ? "  [red]The editor-id hash table contains ZERO NAVM entries.[/] " +
              "MergeRuntimeRecords has no input — runtime NAVMs are invisible."
            : $"  [yellow]Discovery path uses string-keyed editor-id map; NAVMs without " +
              "editor IDs cannot be added even when their BSNavMesh structs exist in memory.[/]");

        if (emptyRawButHasVerts > 0)
        {
            AnsiConsole.MarkupLine("\n[yellow]Sample of empty-RawSubrecord NAVMs with runtime vertex counts:[/]");
            var samples = nm
                .Where(n => n.RawSubrecords.Count == 0 && n.VertexCount > 0)
                .Take(10);
            foreach (var s in samples)
            {
                AnsiConsole.MarkupLine(
                    $"  FormID 0x{s.FormId:X8}  Cell 0x{s.CellFormId:X8}  " +
                    $"V={s.VertexCount:N0}  T={s.TriangleCount:N0}  D={s.DoorPortalCount:N0}");
            }
        }
    }
}
