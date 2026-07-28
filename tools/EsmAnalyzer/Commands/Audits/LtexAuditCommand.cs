using System.CommandLine;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Spectre.Console;

namespace EsmAnalyzer.Commands.Audits;

/// <summary>
///     Audit every LTEX in the ESM: walks LTEX -> TXST -> DiffuseTexture and tries to load
///     each via the BSAs adjacent to the source ESM. Surfaces FormIDs whose textures fail
///     to resolve so we can tell why the rendered Terrain Textures layer shows fallback
///     FormID-hash colors instead of real sampled textures.
/// </summary>
internal static class LtexAuditCommand
{
    internal static Command Create()
    {
        var command = new Command("ltex-audit",
            "Audit LTEX -> TXST -> texture load chain; list FormIDs whose textures fail to load");
        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => Execute(parseResult.GetValue(fileArg)!));
        return command;
    }

    private static int Execute(string filePath)
    {
        var task = ExecuteAsync(filePath);
        task.GetAwaiter().GetResult();
        return 0;
    }

    private static async Task ExecuteAsync(string filePath)
    {
        AnsiConsole.MarkupLine($"[cyan]Auditing LTEX -> texture chain for[/] {Path.GetFileName(filePath)}");
        using var result = await UnifiedAnalyzer.AnalyzeAsync(filePath, null, default);

        var bsaPaths = BsaDiscovery.Discover(filePath).TexturesBsaPaths;
        AnsiConsole.MarkupLine($"[cyan]Discovered {bsaPaths.Length} texture BSA(s):[/]");
        foreach (var p in bsaPaths) AnsiConsole.WriteLine($"  {p}");

        var sources = bsaPaths.Length > 0
            ? NifTextureArchiveSourceFactory.Create(bsaPaths)
            : new List<INifTextureSource>();

        var ltexes = result.Records.LandTextures;
        var ltexById = ltexes
            .GroupBy(l => l.FormId)
            .ToDictionary(g => g.Key, g => g.First());
        var txstById = result.Records.TextureSets.ToDictionary(t => t.FormId);

        var noPath = 0;
        var loadFailures = new List<(uint FormId, string? EditorId, string Path)>();
        var loadedFormIds = new HashSet<uint>();
        var sampledPaths = new List<string>();

        foreach (var ltex in ltexes)
        {
            // Use the SHARED production resolver, which prefers TNAM->TXST (FO3/FNV/Skyrim) and falls
            // back to the Oblivion direct-ICON path. This audit previously only walked the TXST chain,
            // so it reported every Oblivion LTEX as "TXST link missing" and never tested the ICON load.
            var diffusePath = LandscapeTexturePathResolver.ResolveDiffuse(ltex.FormId, ltexById, txstById);
            if (string.IsNullOrWhiteSpace(diffusePath))
            {
                noPath++;
                continue;
            }

            if (sampledPaths.Count < 8)
            {
                sampledPaths.Add($"0x{ltex.FormId:X8} ({ltex.EditorId ?? "?"}) -> {diffusePath}");
            }

            var normPath = NifTexturePathUtility.Normalize(diffusePath);
            var tex = NifTextureLoader.TryLoadFromSources(normPath, sources);
            if (tex is null && normPath.EndsWith(".dds", StringComparison.Ordinal))
            {
                var ddxPath = string.Concat(normPath.AsSpan(0, normPath.Length - 4), ".ddx");
                tex = NifTextureLoader.TryLoadFromSources(ddxPath, sources);
            }
            if (tex is null)
            {
                loadFailures.Add((ltex.FormId, ltex.EditorId, diffusePath));
            }
            else
            {
                loadedFormIds.Add(ltex.FormId);
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]LTEX total:[/] {ltexes.Count}  [grey](resolver: TNAM->TXST or Oblivion ICON)[/]");
        AnsiConsole.MarkupLine($"  Loaded OK: {loadedFormIds.Count}");
        AnsiConsole.MarkupLine($"  No diffuse path (neither TXST nor ICON): {noPath}");
        AnsiConsole.MarkupLine($"  Load failures (path not found in any BSA): {loadFailures.Count}");

        if (sampledPaths.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Sample resolved diffuse paths:[/]");
            foreach (var s in sampledPaths) AnsiConsole.WriteLine($"    {s}");
        }

        if (loadFailures.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]First 30 load failures:[/]");
            foreach (var (formId, editorId, path) in loadFailures.Take(30))
            {
                AnsiConsole.WriteLine($"  LTEX 0x{formId:X8} ({editorId ?? "?"}) -> {path}");
            }
        }

        // Inverse audit: what TextureFormIds do LAND records ACTUALLY reference, and how many
        // of those land in the LTEX dict vs are dangling FormIDs the renderer would treat as
        // failed-to-resolve.
        var ltexFormIds = new HashSet<uint>(ltexes.Select(l => l.FormId));
        var refByFormId = new Dictionary<uint, int>();
        var cellsUsingDangling = new HashSet<uint>();
        foreach (var cell in result.Records.Cells)
        {
            var layers = cell.LandVisualData?.TextureLayers;
            if (layers is null) continue;
            foreach (var layer in layers)
            {
                if (layer.TextureFormId == 0) continue;
                refByFormId.TryGetValue(layer.TextureFormId, out var count);
                refByFormId[layer.TextureFormId] = count + 1;
                if (!ltexFormIds.Contains(layer.TextureFormId))
                {
                    cellsUsingDangling.Add(cell.FormId);
                }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[cyan]Most-referenced textures (by count of BTXT/ATXT entries):[/]");
        foreach (var (formId, count) in refByFormId.OrderByDescending(kvp => kvp.Value).Take(15))
        {
            var ltexInfo = ltexes.FirstOrDefault(l => l.FormId == formId);
            var status = loadedFormIds.Contains(formId) ? "loaded"
                : loadFailures.Any(f => f.FormId == formId) ? "LOAD FAILED"
                : "no LTEX/no path";
            AnsiConsole.WriteLine(
                $"  0x{formId:X8} ({ltexInfo?.EditorId ?? "?"}): {count} refs [{status}]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Texture FormIDs referenced by LAND layers:[/] {refByFormId.Count}");
        var dangling = refByFormId
            .Where(kvp => !ltexFormIds.Contains(kvp.Key))
            .OrderByDescending(kvp => kvp.Value)
            .ToList();
        AnsiConsole.MarkupLine(
            $"  Of those, references that point to NON-EXISTENT LTEX FormIDs: {dangling.Count} unique formIds");
        AnsiConsole.MarkupLine(
            $"  Cells with at least one dangling LTEX reference: {cellsUsingDangling.Count}");
        if (dangling.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]Top 20 dangling FormIDs by reference count:[/]");
            foreach (var (formId, count) in dangling.Take(20))
            {
                AnsiConsole.WriteLine($"  0x{formId:X8} referenced by {count} BTXT/ATXT entries");
            }
        }

        foreach (var s in sources) s.Dispose();
    }
}
