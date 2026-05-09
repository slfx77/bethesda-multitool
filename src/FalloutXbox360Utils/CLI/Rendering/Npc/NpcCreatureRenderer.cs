using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

/// <summary>
///     Handles creature rendering logic for the NPC render pipeline.
///     Extracted from <see cref="NpcRenderPipeline" />.
/// </summary>
internal static class NpcCreatureRenderer
{
    internal static void RenderCreaturesCpu(
        List<(uint FormId, CreatureScanEntry Creature)> creatures,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcAppearanceResolver resolver,
        NpcRenderSettings settings,
        ref int rendered,
        ref int skipped,
        ref int failed)
    {
        var views = settings.Camera.ResolveViews(90f);

        foreach (var (formId, creature) in creatures)
        {
            try
            {
                var model = BuildCreatureModel(creature, meshArchives, textureResolver, resolver, settings);
                foreach (var (suffix, azimuth, elevation) in views)
                {
                    SpriteResult? result = null;
                    if (model is { HasGeometry: true })
                    {
                        var renderModel = views.Length > 1
                            ? NpcMeshHelpers.DeepCloneModel(model)
                            : model;
                        result = NifSpriteRenderer.Render(
                            renderModel,
                            textureResolver,
                            1.0f,
                            32,
                            settings.SpriteSize,
                            azimuth,
                            elevation,
                            settings.SpriteSize);
                    }

                    SaveCreatureResult(
                        formId,
                        creature,
                        result,
                        settings,
                        creatures.Count,
                        ref rendered,
                        ref skipped,
                        ref failed,
                        suffix);
                }
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

    internal static NifRenderableModel? BuildCreatureModel(
        CreatureScanEntry creature,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcAppearanceResolver resolver,
        NpcRenderSettings settings)
    {
        if (settings.Skeleton && creature.SkeletonPath != null)
        {
            var skelPath = creature.SkeletonPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
                ? creature.SkeletonPath
                : "meshes\\" + creature.SkeletonPath.TrimStart('\\');
            return NpcSkeletonLoader.BuildSkeletonVisualization(
                skelPath,
                meshArchives,
                settings.BindPose);
        }

        var plan = CreatureCompositionPlanner.CreatePlan(
            creature,
            meshArchives,
            resolver,
            CreatureCompositionOptions.From(settings));
        return plan == null
            ? null
            : NpcCompositionRenderAdapter.BuildCreature(plan, meshArchives, textureResolver);
    }

    internal static void SaveCreatureResult(
        uint formId,
        CreatureScanEntry creature,
        SpriteResult? result,
        NpcRenderSettings settings,
        int totalCount,
        ref int rendered,
        ref int skipped,
        ref int failed,
        string viewSuffix = "")
    {
        if (result == null)
        {
            skipped++;
            if (settings.NpcFilters != null)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]Skipped:[/] 0x{0:X8} {1} -- no geometry",
                    formId,
                    creature.FullName ?? creature.EditorId ?? "unknown");
            }

            return;
        }

        var name = creature.EditorId ?? $"{formId:X8}";
        var fileName = $"{name}{viewSuffix}.png";
        var outputPath = Path.Combine(settings.OutputDir, fileName);
        var expectedLength = result.Width * result.Height * 4;

        if (result.Pixels.Length != expectedLength)
        {
            failed++;
            AnsiConsole.MarkupLine(
                "[red]FAIL:[/] 0x{0:X8} {1}: pixel buffer mismatch ({2} bytes, expected {3} for {4}x{5})",
                formId,
                creature.EditorId ?? "?",
                result.Pixels.Length,
                expectedLength,
                result.Width,
                result.Height);
            return;
        }

        PngWriter.SaveRgba(
            result.Pixels,
            result.Width,
            result.Height,
            outputPath);
        rendered++;

        if (settings.NpcFilters != null || totalCount <= 20)
        {
            AnsiConsole.MarkupLine(
                "[green]OK:[/] 0x{0:X8} {1} ({2}) -> {3} ({4}x{5})",
                formId,
                Markup.Escape(creature.FullName ?? "?"),
                Markup.Escape(creature.CreatureTypeName),
                fileName,
                result.Width,
                result.Height);
        }
    }
}
