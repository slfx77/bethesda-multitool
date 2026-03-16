using System.CommandLine;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core;

namespace FalloutXbox360Utils.CLI;

internal static class NpcCompareRuntimeCaptureCommand
{
    private static readonly Logger Log = Logger.Instance;

    internal static Command Create()
    {
        var command = new Command(
            "compare-runtime-capture",
            "Compare an xNVSE FaceGenProbe capture against the repo's current NPC coefficient resolver");

        var esmOption = new Option<string>("--esm")
        {
            Description = "Path to ESM file used to resolve current NPC/race FaceGen coefficients",
            Required = true
        };
        var captureOption = new Option<string[]>("--capture")
        {
            Description = "One or more FaceGenProbe capture directories",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };
        var outputOption = new Option<string>("--output")
        {
            Description = "Output directory for comparison CSVs and summaries",
            DefaultValueFactory = _ => Path.Combine("artifacts", "runtime-capture-compare")
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show verbose logging"
        };

        command.Options.Add(esmOption);
        command.Options.Add(captureOption);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, _) =>
        {
            Log.SetVerbose(parseResult.GetValue(verboseOption));

            var settings = new NpcRuntimeCaptureComparisonSettings
            {
                EsmPath = parseResult.GetValue(esmOption)!,
                CaptureDirs = parseResult.GetValue(captureOption)!,
                OutputDir = parseResult.GetValue(outputOption)!
            };

            NpcRuntimeCaptureComparisonPipeline.Run(settings);
            return Task.CompletedTask;
        });

        return command;
    }
}
