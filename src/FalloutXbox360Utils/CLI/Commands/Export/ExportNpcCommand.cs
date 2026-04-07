using System.CommandLine;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Export;

internal static class ExportNpcCommand
{
    private static readonly Logger Log = Logger.Instance;

    public static Command Create()
    {
        var command = new Command(
            "npc",
            "Export NPC GLBs from BSA + ESM data");

        var inputArg = new Argument<string>("meshes-bsa")
        {
            Description = "Path to meshes BSA file"
        };
        var extraMeshesBsaOption = new Option<string[]?>("--extra-meshes-bsa")
        {
            Description = "Additional meshes BSA file(s) searched as fallback for NIF/EGM/EGT assets",
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
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for GLBs",
            Required = true
        };
        var npcOption = new Option<string[]?>("--npc")
        {
            Description =
                "Export specific NPCs by FormID or EditorID (e.g., --npc 0x00104C0C --npc CraigBoone)",
            AllowMultipleArgumentsPerToken = true
        };
        var npcFileOption = new Option<string?>("--npc-file")
        {
            Description = "Path to a text file containing NPC FormIDs or EditorIDs, one per line"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show debug output"
        };
        var dmpOption = new Option<string?>("--dmp")
        {
            Description =
                "Path to Xbox 360 memory dump (.dmp) — uses DMP-sourced FaceGen coefficients"
        };
        var noEgmOption = new Option<bool>("--no-egm")
        {
            Description = "Skip EGM mesh morphing"
        };
        var noEgtOption = new Option<bool>("--no-egt")
        {
            Description = "Skip EGT texture morphing"
        };
        var headOnlyOption = new Option<bool>("--head-only")
        {
            Description = "Export head only"
        };
        var noEquipOption = new Option<bool>("--no-equip")
        {
            Description = "Skip equipped armor/accessories, including head-slot gear"
        };
        var weaponOption = new Option<bool>("--weapon")
        {
            Description = "Include weapon geometry"
        };
        var bindPoseOption = new Option<bool>("--bind-pose")
        {
            Description = "Use bind pose (default unless --anim is specified)"
        };
        var animOption = new Option<string?>("--anim")
        {
            Description = "Override animation KF path (e.g. sneakmtidle.kf)"
        };
        var noTexturesOption = new Option<bool>("--no-textures")
        {
            Description = "Export without textures (vertex colors + flat material only)"
        };
        var diagnoseNormalsOption = new Option<bool>("--diagnose-normals")
        {
            Description = "Run normal/winding consistency diagnostic on exported meshes"
        };
        var noHairOption = new Option<bool>("--no-hair")
        {
            Description = "Skip hair geometry (debugging)"
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(extraMeshesBsaOption);
        command.Options.Add(esmOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(outputOption);
        command.Options.Add(npcOption);
        command.Options.Add(npcFileOption);
        command.Options.Add(verboseOption);
        command.Options.Add(dmpOption);
        command.Options.Add(noEgmOption);
        command.Options.Add(noEgtOption);
        command.Options.Add(headOnlyOption);
        command.Options.Add(noEquipOption);
        command.Options.Add(weaponOption);
        command.Options.Add(bindPoseOption);
        command.Options.Add(animOption);
        command.Options.Add(noTexturesOption);
        command.Options.Add(diagnoseNormalsOption);
        command.Options.Add(noHairOption);

        command.SetAction((parseResult, _) =>
        {
            Log.SetVerbose(parseResult.GetValue(verboseOption));

            if (!NpcExportCommandSupport.TryCreateSettings(
                    parseResult,
                    inputArg,
                    extraMeshesBsaOption,
                    esmOption,
                    texturesBsaOption,
                    outputOption,
                    npcOption,
                    npcFileOption,
                    verboseOption,
                    dmpOption,
                    headOnlyOption,
                    noEquipOption,
                    noEgmOption,
                    noEgtOption,
                    bindPoseOption,
                    animOption,
                    weaponOption,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out var settings,
                    out var error))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(error ?? "invalid export options"));
                return Task.CompletedTask;
            }

            settings!.NoTextures = parseResult.GetValue(noTexturesOption);
            settings.DiagnoseNormals = parseResult.GetValue(diagnoseNormalsOption);
            settings.NoHair = parseResult.GetValue(noHairOption);
            NpcExportPipeline.Run(settings);
            return Task.CompletedTask;
        });

        return command;
    }
}
