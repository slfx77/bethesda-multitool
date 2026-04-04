using FalloutXbox360Utils.CLI.Rendering.Gltf;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
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
        var creatures = ResolveCreatures(settings, resolver);

        if ((appearances == null || appearances.Count == 0) &&
            (creatures == null || creatures.Count == 0))
        {
            if (settings.NpcFilters is { Length: > 0 })
            {
                AnsiConsole.MarkupLine("[red]Error:[/] None of the specified NPCs or creatures found in ESM");
            }

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

        // Export NPCs
        if (appearances != null)
        {
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
        }

        // Export creatures
        if (creatures != null)
        {
            foreach (var (formId, creature) in creatures)
            {
                try
                {
                    string? weaponMeshPath = null;
                    if (creature.InventoryItems != null)
                    {
                        foreach (var item in creature.InventoryItems)
                        {
                            weaponMeshPath = resolver.ResolveWeaponMeshPath(item.ItemFormId);
                            if (weaponMeshPath != null) break;
                        }
                    }

                    var scene = NifExportSceneBuilder.BuildCreature(
                        creature.SkeletonPath!,
                        creature.BodyModelPaths!,
                        meshArchives,
                        settings.BindPose,
                        creature.ResolveIdleAnimationPath(),
                        weaponMeshPath);
                    if (scene == null || scene.MeshParts.Count == 0)
                    {
                        skipped++;
                        continue;
                    }

                    var name = creature.EditorId ?? $"{formId:X8}";
                    var outputPath = Path.Combine(settings.OutputDir, $"{name}.glb");
                    NpcGlbWriter.Write(scene, textureResolver, outputPath);
                    GltfValidatorRunner.ValidateOrThrow(outputPath);
                    exported++;

                    AnsiConsole.MarkupLine(
                        "[green]OK:[/] 0x{0:X8} {1} [{2}] -> {3}",
                        formId,
                        creature.FullName ?? "?",
                        creature.CreatureTypeName,
                        Path.GetFileName(outputPath));
                }
                catch (Exception ex)
                {
                    failed++;
                    AnsiConsole.MarkupLine(
                        "[red]FAIL:[/] 0x{0:X8} {1}: {2}",
                        formId,
                        creature.EditorId ?? "?",
                        Markup.Escape(ex.Message));
                }
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
            // Don't error if no NPCs — filters may match creatures instead
            return filtered;
        }

        return resolver.ResolveAllHeadOnly(pluginName, true);
    }

    private static List<(uint FormId, CreatureScanEntry Creature)>? ResolveCreatures(
        NpcExportSettings settings,
        NpcAppearanceResolver resolver)
    {
        if (settings.DmpPath != null)
        {
            return null;
        }

        var allCreatures = resolver.GetAllCreatures();
        if (allCreatures.Count == 0)
        {
            return null;
        }

        if (settings.NpcFilters is { Length: > 0 })
        {
            var formIdSet = new HashSet<uint>();
            var editorIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var filter in settings.NpcFilters)
            {
                var formId = NpcTextureHelpers.ParseFormId(filter);
                if (formId.HasValue)
                {
                    formIdSet.Add(formId.Value);
                    continue;
                }

                editorIdSet.Add(filter.Trim());
            }

            var filtered = allCreatures
                .Where(kvp =>
                    formIdSet.Contains(kvp.Key) ||
                    (kvp.Value.EditorId != null && editorIdSet.Contains(kvp.Value.EditorId)))
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();

            return filtered.Count > 0 ? filtered : null;
        }

        // No filters: include all named creatures
        return allCreatures
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value.FullName))
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
    }
}
