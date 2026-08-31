using BethesdaMultitool.CLI.Rendering.Gltf;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Rendering.Nif;

internal static class NifExportPipeline
{
    internal static void Run(NifExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(Path.GetDirectoryName(settings.OutputPath) ?? ".");

        var nifData = File.ReadAllBytes(settings.InputPath);
        var nif = NifParser.Parse(nifData);
        if (nif == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to parse NIF file");
            return;
        }

        if (nif.IsBigEndian)
        {
            var converted = NifConverter.Convert(nifData);
            if (!converted.Success || converted.OutputData == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Failed to convert Xbox NIF to PC format");
                return;
            }

            nifData = converted.OutputData;
            nif = NifParser.Parse(nifData);
            if (nif == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Failed to parse converted NIF file");
                return;
            }
        }

        using var textureResolver = settings.TextureSourcePaths is { Length: > 0 }
            ? new NifTextureResolver(settings.TextureSourcePaths)
            : new NifTextureResolver();

        var scene = BuildScene(nifData, nif, settings.InputPath, textureResolver);
        if (scene == null || scene.MeshParts.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Skipped:[/] {0} (no exportable geometry)",
                Path.GetFileName(settings.InputPath));
            return;
        }

        GlbWriter.Write(scene, textureResolver, settings.OutputPath);
        GltfValidatorRunner.ValidateOrThrow(settings.OutputPath);

        AnsiConsole.MarkupLine(
            "[green]OK:[/] {0} -> {1}",
            Path.GetFileName(settings.InputPath),
            Path.GetFileName(settings.OutputPath));
    }

    private static GlbScene? BuildScene(
        byte[] data,
        NifInfo nif,
        string sourceLabel,
        NifTextureResolver textureResolver)
    {
        if (!nif.Blocks.Any(block => NifSceneGraphWalker.SelfContainedShapeTypes.Contains(block.TypeName)))
        {
            return NifExportSceneBuilder.Build(data, nif, sourceLabel);
        }

        // The renderer extractor owns external BGSM/BGEM resolution for modern self-contained
        // shapes. Keep its fully resolved material state even when FO76 skinning requires the
        // hierarchy exporter to own joints, weights, and inverse-bind matrices.
        var modernModel = NifGeometryExtractor.Extract(data, nif, textureResolver);
        var hasFo76SkinCandidate = Enumerable.Range(0, nif.Blocks.Count)
            .Any(shapeIndex => Fo76BsSkinBindingExtractor.IsCandidate(data, nif, shapeIndex));
        if (hasFo76SkinCandidate)
        {
            var hierarchyScene = NifExportSceneBuilder.Build(data, nif, sourceLabel);
            if (hierarchyScene is not null)
            {
                if (modernModel is not null)
                {
                    NifExportSceneBuilder.ApplyModernMaterialState(hierarchyScene, modernModel);
                }

                return hierarchyScene;
            }
        }

        // Rigid FO4/FO76 shapes (and unsupported hierarchy cases) retain the modern extractor's
        // transformed geometry and resolved material state instead of falling back to an empty GLB.
        return modernModel is null
            ? null
            : NifExportSceneBuilder.BuildRenderableModel(modernModel, sourceLabel);
    }
}
