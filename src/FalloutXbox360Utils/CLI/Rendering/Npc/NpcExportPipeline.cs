using FalloutXbox360Utils.CLI.Rendering.Gltf;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

internal static class NpcExportPipeline
{
    internal static void Run(NpcExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!ValidateInputPaths(settings, out var texturesBsaPaths))
        {
            return;
        }

        Directory.CreateDirectory(settings.OutputDir);

        AnsiConsole.MarkupLine(
            "Loading ESM: [cyan]{0}[/]",
            Path.GetFileName(settings.EsmPath));
        var esm = EsmFileLoader.Load(settings.EsmPath, false);
        if (esm == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to load ESM file");
            return;
        }

        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        var pluginName = Path.GetFileName(settings.EsmPath);
        var appearances = ResolveAppearances(settings, resolver, pluginName);
        if (appearances == null)
        {
            return;
        }

        using var meshArchives = NpcMeshArchiveSet.Open(settings.MeshesBsaPath, settings.ExtraMeshesBsaPaths);
        foreach (var extraMeshesBsaPath in meshArchives.ArchivePaths.Skip(1))
        {
            AnsiConsole.MarkupLine(
                "Loading fallback meshes BSA: [cyan]{0}[/]",
                Path.GetFileName(extraMeshesBsaPath));
        }

        using var textureResolver = new NifTextureResolver(texturesBsaPaths);
        var egmCache = new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache = new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var exported = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var npc in appearances)
        {
            try
            {
                var scene = NpcExportSceneBuilder.Build(
                    npc,
                    meshArchives,
                    textureResolver,
                    egmCache,
                    egtCache,
                    settings);
                if (scene == null || scene.MeshParts.Count == 0)
                {
                    skipped++;
                    continue;
                }

                var outputPath = Path.Combine(
                    settings.OutputDir,
                    NpcExportFileNaming.BuildFileName(npc));
                NpcGlbWriter.Write(scene, textureResolver, outputPath);
                GltfValidatorRunner.ValidateOrThrow(outputPath);
                textureResolver.EvictTexture(NpcTextureHelpers.BuildNpcFaceEgtTextureKey(npc));
                exported++;

                if (settings.NpcFilters != null || appearances.Count <= 20)
                {
                    AnsiConsole.MarkupLine(
                        "[green]OK:[/] 0x{0:X8} {1} -> {2}",
                        npc.NpcFormId,
                        npc.FullName ?? npc.EditorId ?? "?",
                        Path.GetFileName(outputPath));
                }
            }
            catch (Exception ex)
            {
                failed++;
                AnsiConsole.MarkupLine(
                    "[red]FAIL:[/] 0x{0:X8} {1}: {2}",
                    npc.NpcFormId,
                    npc.EditorId ?? "?",
                    Markup.Escape(ex.Message));
            }
            finally
            {
                textureResolver.EvictTexture(NpcTextureHelpers.BuildNpcFaceEgtTextureKey(npc));
            }
        }

        AnsiConsole.MarkupLine(
            "\nExported: [green]{0}[/]  Skipped: [yellow]{1}[/]  Failed: [red]{2}[/]",
            exported,
            skipped,
            failed);
    }

    private static bool ValidateInputPaths(
        NpcExportSettings settings,
        out string[] texturesBsaPaths)
    {
        texturesBsaPaths = Array.Empty<string>();

        if (!File.Exists(settings.MeshesBsaPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Meshes BSA not found: {0}", settings.MeshesBsaPath);
            return false;
        }

        if (settings.ExtraMeshesBsaPaths is { Length: > 0 })
        {
            foreach (var extraMeshesBsaPath in settings.ExtraMeshesBsaPaths)
            {
                if (!File.Exists(extraMeshesBsaPath))
                {
                    AnsiConsole.MarkupLine("[red]Error:[/] Extra meshes BSA not found: {0}", extraMeshesBsaPath);
                    return false;
                }
            }
        }

        if (!File.Exists(settings.EsmPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ESM file not found: {0}", settings.EsmPath);
            return false;
        }

        texturesBsaPaths = NpcTextureHelpers.ResolveTexturesBsaPaths(
            settings.MeshesBsaPath,
            settings.ExplicitTexturesBsaPaths);
        if (texturesBsaPaths.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No texture BSA files found");
            return false;
        }

        if (settings.DmpPath != null && !File.Exists(settings.DmpPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] DMP file not found: {0}", settings.DmpPath);
            return false;
        }

        return true;
    }

    private static List<NpcAppearance>? ResolveAppearances(
        NpcExportSettings settings,
        NpcAppearanceResolver resolver,
        string pluginName)
    {
        if (settings.DmpPath != null)
        {
            var appearances = NpcRenderHelpers.ResolveFromDmp(
                settings.DmpPath,
                resolver,
                pluginName,
                settings.NpcFilters);
            return appearances.Count == 0 ? null : appearances;
        }

        if (settings.NpcFilters is { Length: > 0 })
        {
            var allAppearances = resolver.ResolveAllHeadOnly(pluginName);
            var formIdSet = new HashSet<uint>();
            var editorIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var filter in settings.NpcFilters)
            {
                var formId = NpcTextureHelpers.ParseFormId(filter);
                if (formId.HasValue)
                {
                    formIdSet.Add(formId.Value);
                }
                else
                {
                    editorIdSet.Add(filter.Trim());
                }
            }

            var filtered = allAppearances
                .Where(appearance =>
                    formIdSet.Contains(appearance.NpcFormId) ||
                    (appearance.EditorId != null && editorIdSet.Contains(appearance.EditorId)))
                .ToList();
            if (filtered.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] None of the specified NPCs found in ESM");
                return null;
            }

            return filtered;
        }

        return resolver.ResolveAllHeadOnly(pluginName, true);
    }
}
