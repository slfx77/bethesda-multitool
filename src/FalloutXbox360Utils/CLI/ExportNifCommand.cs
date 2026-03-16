using System.CommandLine;
using FalloutXbox360Utils.CLI.Rendering.Nif;
using FalloutXbox360Utils.Core;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

internal static class ExportNifCommand
{
    private static readonly Logger Log = Logger.Instance;

    public static Command Create()
    {
        var command = new Command("nif", "Export a NIF model to GLB");

        var inputArg = new Argument<string>("path")
        {
            Description = "Path to a local NIF file"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output .glb file or output directory",
            Required = true
        };
        var dataRootOption = new Option<string[]?>("--data-root")
        {
            Description = "Game Data root(s) used to resolve loose textures (contains textures\\...)",
            AllowMultipleArgumentsPerToken = true
        };
        var texturesBsaOption = new Option<string[]?>("--textures-bsa")
        {
            Description = "Texture BSA file(s) used to resolve embedded material textures",
            AllowMultipleArgumentsPerToken = true
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show debug output"
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOption);
        command.Options.Add(dataRootOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, _) =>
        {
            Log.SetVerbose(parseResult.GetValue(verboseOption));

            var settings = new NifExportSettings
            {
                InputPath = parseResult.GetValue(inputArg)!,
                OutputPath = parseResult.GetValue(outputOption)!,
                DataRoots = parseResult.GetValue(dataRootOption),
                TextureSourcePaths = parseResult.GetValue(texturesBsaOption)
            };

            if (!NifExportPathResolver.TryResolve(settings, out var resolvedSettings, out var error))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(error ?? "invalid export options"));
                return Task.CompletedTask;
            }

            NifExportPipeline.Run(resolvedSettings!);
            return Task.CompletedTask;
        });

        return command;
    }
}
