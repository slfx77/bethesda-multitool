using System.CommandLine;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Render command: renders NIF models to PNG sprites.
///     Supports single local file, local directory batch, or BSA batch (via --bsa).
///     Also hosts the 'npc' subcommand for NPC head rendering.
/// </summary>
public static class RenderCommand
{
    public static Command Create()
    {
        var command = new Command("render", "Render NIF models to PNG sprites");

        var pathArg = new Argument<string>("path")
            { Description = "NIF file, directory of NIFs, or BSA-relative path (when --bsa is provided)" };
        var bsaOption = new Option<string?>("--bsa")
            { Description = "Path to meshes BSA file (path argument becomes BSA-relative prefix)" };
        var outputOption = new Option<string>("-o", "--output")
            { Description = "Output directory for sprites", Required = true };
        var ppuOption = new Option<float>("--ppu")
            { Description = "Pixels per game unit (default: 1.0)", DefaultValueFactory = _ => 1.0f };
        var minSizeOption = new Option<int>("--min-size")
            { Description = "Minimum sprite dimension (default: 32)", DefaultValueFactory = _ => 32 };
        var maxSizeOption = new Option<int>("--max-size")
            { Description = "Maximum sprite dimension (default: 1024)", DefaultValueFactory = _ => 1024 };
        var parallelismOption = new Option<int>("-j", "--parallelism")
        {
            Description = "Max parallel tasks (default: processor count)",
            DefaultValueFactory = _ => Environment.ProcessorCount
        };
        var texturesBsaOption = new Option<string[]>("--textures-bsa")
        {
            Description = "Path to textures BSA file(s) for texture-mapped rendering (can specify multiple)",
            AllowMultipleArgumentsPerToken = true
        };
        var esmOption = new Option<string?>("--esm")
            { Description = "ESM file for cross-referencing FormIDs, EditorIDs, and RefIDs" };
        var isoOption = new Option<bool>("--iso")
        {
            Description = "Render 4 isometric views (NE, NW, SW, SE) instead of top-down",
            DefaultValueFactory = _ => false
        };
        var elevationOption = new Option<float>("--elevation")
        {
            Description = "Isometric camera elevation in degrees from horizontal (default: 30)",
            DefaultValueFactory = _ => 30f
        };
        var sideOption = new Option<bool>("--side")
        {
            Description = "Render 4 side profile views (front, back, left, right) at 0° elevation",
            DefaultValueFactory = _ => false
        };
        var trimetricOption = new Option<bool>("--trimetric")
        {
            Description = "Render 4 trimetric axonometric views (unequal axis foreshortening)",
            DefaultValueFactory = _ => false
        };
        var sizeOption = new Option<int?>("--size")
            { Description = "Force all sprites to this size (longest edge), regardless of model scale" };
        var gpuOption = new Option<bool>("--gpu")
            { Description = "Force GPU rendering (Vulkan/D3D11)", DefaultValueFactory = _ => false };
        var cpuOption = new Option<bool>("--cpu")
            { Description = "Force CPU software rendering", DefaultValueFactory = _ => false };

        command.Arguments.Add(pathArg);
        command.Options.Add(bsaOption);
        command.Options.Add(outputOption);
        command.Options.Add(ppuOption);
        command.Options.Add(minSizeOption);
        command.Options.Add(maxSizeOption);
        command.Options.Add(parallelismOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(esmOption);
        command.Options.Add(isoOption);
        command.Options.Add(elevationOption);
        command.Options.Add(sideOption);
        command.Options.Add(trimetricOption);
        command.Options.Add(sizeOption);
        command.Options.Add(gpuOption);
        command.Options.Add(cpuOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var isIso = parseResult.GetValue(isoOption);
            var isSide = parseResult.GetValue(sideOption);
            var isTrimetric = parseResult.GetValue(trimetricOption);

            var viewCount = (isIso ? 1 : 0) + (isSide ? 1 : 0) + (isTrimetric ? 1 : 0);
            if (viewCount > 1)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] --iso, --side, and --trimetric are mutually exclusive");
                return;
            }

            var elevationExplicit = parseResult.GetResult(elevationOption) != null;

            var settings = new NifRenderSettings
            {
                Path = parseResult.GetValue(pathArg)!,
                BsaPath = parseResult.GetValue(bsaOption),
                OutputDir = parseResult.GetValue(outputOption)!,
                Render = new RenderParams(
                    parseResult.GetValue(ppuOption),
                    parseResult.GetValue(minSizeOption),
                    parseResult.GetValue(maxSizeOption)),
                Parallelism = parseResult.GetValue(parallelismOption),
                TexturesBsaPaths = ResolveTexturesBsaPaths(
                    parseResult.GetValue(bsaOption),
                    parseResult.GetValue(texturesBsaOption)),
                EsmPath = parseResult.GetValue(esmOption),
                Camera = new CameraConfig
                {
                    Isometric = isIso,
                    ElevationDeg = parseResult.GetValue(elevationOption),
                    ElevationOverridden = elevationExplicit,
                    SideProfile = isSide,
                    Trimetric = isTrimetric
                },
                FixedSize = parseResult.GetValue(sizeOption),
                ForceGpu = parseResult.GetValue(gpuOption),
                ForceCpu = parseResult.GetValue(cpuOption)
            };

            if (settings.BsaPath != null)
            {
                // BSA mode: path is a BSA-relative prefix
                await RenderNifProcessor.RunBsaBatchAsync(settings, ct);
            }
            else if (Directory.Exists(settings.Path))
            {
                // Local directory batch mode
                await RenderNifProcessor.RunLocalDirectoryAsync(settings, ct);
            }
            else
            {
                // Single local NIF file
                await RenderNifProcessor.RunLocalFileAsync(settings, ct);
            }
        });

        // NPC head rendering subcommand
        command.Subcommands.Add(RenderNpcCommand.Create());

        return command;
    }

    /// <summary>
    ///     Auto-discovers texture BSAs from the meshes BSA directory when none are explicitly specified.
    /// </summary>
    private static string[] ResolveTexturesBsaPaths(string? bsaPath, string[]? explicitPaths)
    {
        if (explicitPaths is { Length: > 0 })
            return explicitPaths;

        // Auto-detect only when a meshes BSA is specified
        if (string.IsNullOrEmpty(bsaPath) || !File.Exists(bsaPath))
            return [];

        var dir = Path.GetDirectoryName(Path.GetFullPath(bsaPath));
        if (dir == null || !Directory.Exists(dir))
            return [];

        var found = Directory.GetFiles(dir, "*Texture*.bsa")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (found.Length > 0)
            AnsiConsole.MarkupLine("Auto-detected [green]{0}[/] texture BSA(s) in [cyan]{1}[/]", found.Length, dir);

        return found;
    }
}
