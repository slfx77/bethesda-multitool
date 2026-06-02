using System.CommandLine;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

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
        var txstById = result.Records.TextureSets.ToDictionary(t => t.FormId);

        var noTxstLink = 0;
        var noTxstRecord = 0;
        var noDiffusePath = 0;
        var loadFailures = new List<(uint FormId, string? EditorId, string Path)>();
        var loaded = 0;

        foreach (var ltex in ltexes)
        {
            if (!ltex.TextureSetFormId.HasValue || ltex.TextureSetFormId.Value == 0)
            {
                noTxstLink++;
                continue;
            }
            if (!txstById.TryGetValue(ltex.TextureSetFormId.Value, out var txst))
            {
                noTxstRecord++;
                continue;
            }
            if (string.IsNullOrEmpty(txst.DiffuseTexture))
            {
                noDiffusePath++;
                continue;
            }

            var normPath = NifTexturePathUtility.Normalize(txst.DiffuseTexture);
            var tex = NifTextureLoader.TryLoadFromSources(normPath, sources);
            if (tex is null && normPath.EndsWith(".dds", StringComparison.Ordinal))
            {
                var ddxPath = string.Concat(normPath.AsSpan(0, normPath.Length - 4), ".ddx");
                tex = NifTextureLoader.TryLoadFromSources(ddxPath, sources);
            }
            if (tex is null)
            {
                loadFailures.Add((ltex.FormId, ltex.EditorId, txst.DiffuseTexture));
            }
            else
            {
                loaded++;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]LTEX total:[/] {ltexes.Count}");
        AnsiConsole.MarkupLine($"  Loaded OK: {loaded}");
        AnsiConsole.MarkupLine($"  TXST link missing: {noTxstLink}");
        AnsiConsole.MarkupLine($"  TXST record not found: {noTxstRecord}");
        AnsiConsole.MarkupLine($"  No DiffuseTexture path: {noDiffusePath}");
        AnsiConsole.MarkupLine($"  Load failures (path not found in any BSA): {loadFailures.Count}");

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
            var failed = loadFailures.Any(f => f.FormId == formId);
            var status = failed ? "LOAD FAILED" : "loaded";
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
