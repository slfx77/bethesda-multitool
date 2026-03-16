using FalloutXbox360Utils.CLI.Rendering.Gltf;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Rendering.Nif;

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

        var scene = NifExportSceneBuilder.Build(nifData, nif, settings.InputPath);
        if (scene == null || scene.MeshParts.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Skipped:[/] {0} (no exportable geometry)",
                Path.GetFileName(settings.InputPath));
            return;
        }

        using var textureResolver = settings.TextureSourcePaths is { Length: > 0 }
            ? new NifTextureResolver(settings.TextureSourcePaths)
            : new NifTextureResolver();

        NpcGlbWriter.Write(scene, textureResolver, settings.OutputPath);
        GltfValidatorRunner.ValidateOrThrow(settings.OutputPath);

        AnsiConsole.MarkupLine(
            "[green]OK:[/] {0} -> {1}",
            Path.GetFileName(settings.InputPath),
            Path.GetFileName(settings.OutputPath));
    }
}
