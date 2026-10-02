using BethesdaMultitool.CLI.Rendering.Gltf;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Rendering.Nif;

internal static class NifExportPipeline
{
    internal static void Run(NifExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(Path.GetDirectoryName(settings.OutputPath) ?? ".");

        var rawData = File.ReadAllBytes(settings.InputPath);
        if (!NifExportSceneAssembly.TryParseForExport(rawData, out var nifData, out var nif, out var error))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", error);
            return;
        }

        using var textureResolver = settings.TextureSourcePaths is { Length: > 0 }
            ? new NifTextureResolver(settings.TextureSourcePaths)
            : new NifTextureResolver();

        var scene = NifExportSceneAssembly.BuildForExport(nifData, nif, settings.InputPath, textureResolver);
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
}
