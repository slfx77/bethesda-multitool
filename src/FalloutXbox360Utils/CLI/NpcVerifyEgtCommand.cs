using System.CommandLine;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core;

namespace FalloutXbox360Utils.CLI;

internal static class NpcVerifyEgtCommand
{
    private static readonly Logger Log = Logger.Instance;

    internal static Command Create()
    {
        var command = new Command(
            "verify-egt",
            "Regenerate NPC head FaceGen textures and compare them to shipped facemod textures");

        var meshesBsaArg = new Argument<string>("meshes-bsa")
        {
            Description = "Path to meshes BSA file"
        };
        var extraMeshesBsaOption = new Option<string[]?>("--extra-meshes-bsa")
        {
            Description = "Additional meshes BSA file(s) searched as fallback for EGT assets",
            AllowMultipleArgumentsPerToken = true
        };
        var esmOption = new Option<string>("--esm")
        {
            Description = "Path to ESM file",
            Required = true
        };
        var texturesBsaOption = new Option<string[]?>("--textures-bsa")
        {
            Description =
                "Path to textures BSA file(s) (auto-detected from meshes BSA directory if omitted)",
            AllowMultipleArgumentsPerToken = true
        };
        var npcOption = new Option<string[]?>("--npc")
        {
            Description = "Limit verification to specific NPC FormIDs or EditorIDs",
            AllowMultipleArgumentsPerToken = true
        };
        var limitOption = new Option<int?>("--limit")
        {
            Description = "Maximum number of shipped facemod textures to verify"
        };
        var topOption = new Option<int>("--top")
        {
            Description = "How many worst mismatches to show in the summary",
            DefaultValueFactory = _ => 10
        };
        var reportOption = new Option<string?>("--report")
        {
            Description = "Optional CSV report output path"
        };
        var imagesOption = new Option<string?>("--images")
        {
            Description = "Optional output directory for generated/shipped/diff PNG comparisons"
        };
        var rmsClampOption = new Option<float>("--rms-clamp")
        {
            Description =
                "RMS clamp threshold for merged FaceGen coefficients (0 = disabled). " +
                "If set, coefficients are scaled down when their RMS exceeds this value.",
            DefaultValueFactory = _ => 0f
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show verbose logging"
        };

        command.Arguments.Add(meshesBsaArg);
        command.Options.Add(extraMeshesBsaOption);
        command.Options.Add(esmOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(npcOption);
        command.Options.Add(limitOption);
        command.Options.Add(topOption);
        command.Options.Add(reportOption);
        command.Options.Add(imagesOption);
        command.Options.Add(rmsClampOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, _) =>
        {
            Log.SetVerbose(parseResult.GetValue(verboseOption));

            var settings = new NpcEgtVerificationSettings
            {
                MeshesBsaPath = parseResult.GetValue(meshesBsaArg)!,
                ExtraMeshesBsaPaths = parseResult.GetValue(extraMeshesBsaOption),
                EsmPath = parseResult.GetValue(esmOption)!,
                ExplicitTexturesBsaPaths = parseResult.GetValue(texturesBsaOption),
                NpcFilters = parseResult.GetValue(npcOption),
                Limit = parseResult.GetValue(limitOption),
                TopCount = parseResult.GetValue(topOption),
                ReportPath = parseResult.GetValue(reportOption),
                ImageOutputDir = parseResult.GetValue(imagesOption),
                RmsClampThreshold = parseResult.GetValue(rmsClampOption)
            };

            NpcEgtVerificationPipeline.Run(settings);
            return Task.CompletedTask;
        });

        return command;
    }
}
