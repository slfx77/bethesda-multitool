using System.CommandLine;
using System.Globalization;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Loads an ESM/ESP and prints the full PlacedReference detail (position, rotation,
///     XTEL teleport, XLOC lock, XESP enable parent, XLKR link, owner, …) for one or
///     more REFR/ACHR/ACRE FormIDs. Useful for one-off lookups when <c>show</c> doesn't
///     dive into placed-reference subrecords.
/// </summary>
internal static class EsmRefrDetailCommand
{
    public static Command CreateRefrDetailCommand()
    {
        var command = new Command("refr-detail",
            "Print PlacedReference detail (incl. XTEL) for a list of REFR/ACHR/ACRE FormIDs in an ESM/ESP.");

        var fileArg = new Argument<string>("file") { Description = "Path to ESM/ESP" };
        var fidArg = new Argument<string[]>("formids") { Description = "One or more REFR/ACHR/ACRE FormIDs (hex)" };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(fidArg);

        command.SetAction(async (parseResult, ct) =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var fids = parseResult.GetValue(fidArg)!;
            await RunAsync(file, fids, ct);
        });

        return command;
    }

    private static async Task RunAsync(string esmPath, string[] fidArgs, CancellationToken ct)
    {
        if (!File.Exists(esmPath))
        {
            AnsiConsole.MarkupLine($"[red]ESM not found:[/] {Markup.Escape(esmPath)}");
            return;
        }

        var targets = new HashSet<uint>();
        foreach (var s in fidArgs)
        {
            if (TryParseHexUInt(s, out var fid))
            {
                targets.Add(fid);
            }
        }
        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No valid FormIDs.[/]");
            return;
        }

        using var loaded = await SemanticFileLoader.LoadAsync(
            esmPath, new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile }, ct);

        var resolver = loaded.Resolver;
        var found = 0;

        void DumpRef(uint cellFid, FalloutXbox360Utils.Core.Formats.Esm.Models.World.PlacedReference r)
        {
            found++;
            var baseName = resolver.GetDisplayName(r.BaseFormId)
                           ?? r.BaseEditorId
                           ?? resolver.GetEditorId(r.BaseFormId)
                           ?? "(unknown)";
            AnsiConsole.MarkupLine($"\n[yellow]REFR 0x{r.FormId:X8}[/] ({r.RecordType})  parent cell 0x{cellFid:X8}");
            AnsiConsole.WriteLine($"  Editor ID    : {r.EditorId ?? "(none)"}");
            AnsiConsole.WriteLine($"  Base         : 0x{r.BaseFormId:X8}  {baseName}  (edid={r.BaseEditorId ?? "(none)"})");
            AnsiConsole.WriteLine($"  Position     : ({r.X:F1}, {r.Y:F1}, {r.Z:F1})  rot=({r.RotX:F3}, {r.RotY:F3}, {r.RotZ:F3})  scale={r.Scale:F2}");
            AnsiConsole.WriteLine($"  Model path   : {r.ModelPath ?? "(none)"}");
            AnsiConsole.WriteLine($"  IsPersistent : {r.IsPersistent}    IsInitiallyDisabled: {r.IsInitiallyDisabled}");

            if (r.DestinationDoorFormId.HasValue || r.TeleportPosRot is not null || r.DestinationCellFormId.HasValue)
            {
                AnsiConsole.MarkupLine("  [cyan]XTEL teleport:[/]");
                if (r.DestinationDoorFormId.HasValue)
                {
                    var destDoorName = resolver.GetEditorId(r.DestinationDoorFormId.Value)
                                       ?? resolver.GetDisplayName(r.DestinationDoorFormId.Value)
                                       ?? "(unresolved)";
                    AnsiConsole.WriteLine($"    Destination door FormID : 0x{r.DestinationDoorFormId.Value:X8}  {destDoorName}");
                }
                if (r.DestinationCellFormId.HasValue)
                {
                    var destCellName = resolver.GetEditorId(r.DestinationCellFormId.Value)
                                       ?? resolver.GetDisplayName(r.DestinationCellFormId.Value)
                                       ?? "(unresolved — cell may have been removed)";
                    AnsiConsole.WriteLine($"    Destination cell FormID : 0x{r.DestinationCellFormId.Value:X8}  {destCellName}");
                }
                if (r.TeleportPosRot is not null)
                {
                    var t = r.TeleportPosRot;
                    AnsiConsole.WriteLine(
                        $"    Teleport pos/rot        : ({t.X:F1}, {t.Y:F1}, {t.Z:F1})  rot=({t.RotX:F3}, {t.RotY:F3}, {t.RotZ:F3})");
                }
                if (r.TeleportFlags.HasValue)
                {
                    AnsiConsole.WriteLine($"    XTEL flags              : 0x{r.TeleportFlags.Value:X2}");
                }
            }

            if (r.OwnerFormId.HasValue)
            {
                AnsiConsole.WriteLine($"  Owner       : 0x{r.OwnerFormId.Value:X8}");
            }
            if (r.EnableParentFormId.HasValue)
            {
                AnsiConsole.WriteLine($"  XESP parent : 0x{r.EnableParentFormId.Value:X8} flags=0x{r.EnableParentFlags ?? 0:X2}");
            }
            if (r.LinkedRefFormId.HasValue)
            {
                AnsiConsole.WriteLine($"  XLKR        : 0x{r.LinkedRefFormId.Value:X8} keyword=0x{r.LinkedRefKeywordFormId ?? 0:X8}");
            }
            if (r.IsMapMarker)
            {
                AnsiConsole.WriteLine($"  Map marker  : name=\"{r.MarkerName}\" type={r.MarkerType}");
            }
        }

        foreach (var cell in loaded.Records.Cells)
        {
            foreach (var pr in cell.PlacedObjects)
            {
                if (targets.Contains(pr.FormId))
                {
                    DumpRef(cell.FormId, pr);
                }
            }
        }
        foreach (var ws in loaded.Records.Worldspaces)
        {
            foreach (var cell in ws.Cells)
            {
                foreach (var pr in cell.PlacedObjects)
                {
                    if (targets.Contains(pr.FormId))
                    {
                        DumpRef(cell.FormId, pr);
                    }
                }
            }
        }

        if (found == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Found 0 of {targets.Count} requested REFR(s) in any cell.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"\n[green]Found {found} REFR(s).[/]");
        }
    }

    private static bool TryParseHexUInt(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }
        var span = s.AsSpan();
        if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
        {
            span = span[2..];
        }
        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
