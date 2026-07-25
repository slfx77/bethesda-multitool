using System.CommandLine;
using BethesdaMultitool.Core.Analysis;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnostic for the placed-light pipeline: production-parses an ESM and reports the LIGH radius
///     histogram (a radius-0 spike means the DATA parse dropped a variant — TES4 ships 24-byte DATA
///     without Value/Weight), model-less emitter counts, and how many placed LIGH REFRs reach cell
///     PlacedObjects with a usable (radius &gt; 0) base, split interior/exterior. Proves the light feed
///     end-to-end without a GUI session.
/// </summary>
internal static class LightAuditCommand
{
    internal static Command Create()
    {
        var command = new Command("light-audit",
            "Audit LIGH records + placed light REFRs (radius histogram, interior/exterior usability)");
        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => Execute(parseResult.GetValue(fileArg)!));
        return command;
    }

    private static int Execute(string filePath)
    {
        using var result = UnifiedAnalyzer.AnalyzeAsync(filePath).GetAwaiter().GetResult();
        var lights = result.Records.Lights;
        var lightsById = new Dictionary<uint, uint>(lights.Count);
        foreach (var light in lights)
        {
            lightsById[light.FormId] = light.Radius;
        }

        var zero = lights.Count(l => l.Radius == 0);
        var small = lights.Count(l => l.Radius is > 0 and < 64);
        var mid = lights.Count(l => l.Radius is >= 64 and < 256);
        var large = lights.Count(l => l.Radius is >= 256 and < 1024);
        var huge = lights.Count(l => l.Radius >= 1024);
        var modelLess = lights.Count(l => string.IsNullOrEmpty(l.ModelPath));

        AnsiConsole.MarkupLine(
            $"[cyan]File:[/] {Path.GetFileName(filePath)}  [cyan]LIGH records:[/] {lights.Count}  " +
            $"[cyan]model-less:[/] {modelLess}");
        AnsiConsole.MarkupLine(
            $"  radius histogram: [red]0: {zero}[/]  1-63: {small}  64-255: {mid}  " +
            $"256-1023: {large}  >=1024: {huge}");

        var interiorRefs = 0;
        var interiorUsable = 0;
        var exteriorRefs = 0;
        var exteriorUsable = 0;
        foreach (var cell in result.Records.Cells)
        {
            foreach (var reference in cell.PlacedObjects)
            {
                if (!lightsById.TryGetValue(reference.BaseFormId, out var radius)) continue;
                if (cell.IsInterior)
                {
                    interiorRefs++;
                    if (radius > 0) interiorUsable++;
                }
                else
                {
                    exteriorRefs++;
                    if (radius > 0) exteriorUsable++;
                }
            }
        }

        AnsiConsole.MarkupLine(
            $"  placed LIGH REFRs — interior: {interiorRefs} ([green]usable {interiorUsable}[/])  " +
            $"exterior: {exteriorRefs} ([green]usable {exteriorUsable}[/])");

        foreach (var light in lights.Where(l => l.Radius > 0).Take(5))
        {
            AnsiConsole.MarkupLine(
                $"  0x{light.FormId:X8} {light.EditorId ?? "(no edid)"} radius={light.Radius} " +
                $"color=0x{light.Color:X8} falloff={light.FalloffExponent:0.###} fov={light.Fov:0.#}");
        }

        return 0;
    }
}
