using System.CommandLine;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for rendering NPC sprites from BSA + ESM data.
///     Argument parsing stays here; the render workflow lives in
///     <see cref="NpcRenderPipeline" />.
/// </summary>
public static class RenderNpcCommand
{
    private static readonly Logger Log = Logger.Instance;

    public static Command Create()
    {
        var command = new Command(
            "npc",
            "Render NPC sprites from BSA + ESM data");

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
            Description = "Output directory for sprites",
            Required = true
        };
        var npcOption = new Option<string[]?>("--npc")
        {
            Description =
                "Render specific NPCs by FormID or EditorID (e.g., --npc 0x00104C0C --npc CraigBoone)",
            AllowMultipleArgumentsPerToken = true
        };
        var npcFileOption = new Option<string?>("--npc-file")
        {
            Description = "Path to a text file containing NPC FormIDs or EditorIDs, one per line"
        };
        var sizeOption = new Option<int>("--size")
        {
            Description = "Sprite size in pixels (longest edge)",
            DefaultValueFactory = _ => 512
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show debug output (bone transforms, EGM details, bounds)"
        };
        var dmpOption = new Option<string?>("--dmp")
        {
            Description =
                "Path to Xbox 360 memory dump (.dmp) — uses DMP-sourced FaceGen coefficients"
        };
        var exportEgtOption = new Option<bool>("--export-egt")
        {
            Description = "Export EGT debug textures (native + upscaled deltas) to output dir"
        };
        var compareRaceTextureFgtsOption = new Option<bool>("--compare-race-fgts")
        {
            Description = "Render both npc_only and npc_plus_race EGT variants for the selected NPCs"
        };
        var noBilinearOption = new Option<bool>("--no-bilinear")
        {
            Description = "Use nearest-neighbor instead of bilinear for EGT upscaling"
        };
        var noEgmOption = new Option<bool>("--no-egm")
        {
            Description = "Skip EGM mesh morphing (debug: isolate texture issues)"
        };
        var noEgtOption = new Option<bool>("--no-egt")
        {
            Description = "Skip EGT texture morphing (debug: isolate mesh issues)"
        };
        var noBumpOption = new Option<bool>("--no-bump")
        {
            Description = "Disable normal map / bump mapping"
        };
        var noTexOption = new Option<bool>("--no-tex")
        {
            Description = "Replace textures with flat white (debug: show lighting only)"
        };
        var bumpStrengthOption = new Option<float?>("--bump-strength")
        {
            Description = "Normal map bump strength (0=flat, 1=full, default 0.5)"
        };
        var headOnlyOption = new Option<bool>("--head-only")
        {
            Description = "Render head only (legacy mode)"
        };
        var noEquipOption = new Option<bool>("--no-equip")
        {
            Description = "Skip equipped armor/accessories, including head-slot gear"
        };
        var noWeaponOption = new Option<bool>("--no-weapon")
        {
            Description = "Skip weapon rendering"
        };
        var weaponOption = new Option<bool>("--weapon")
        {
            Description = "Include weapon geometry in GLB export mode"
        };
        var glbOption = new Option<bool>("--glb")
        {
            Description = "Export GLB instead of rendering PNG sprites"
        };
        var gpuOption = new Option<bool>("--gpu")
        {
            Description = "Force GPU rendering (Vulkan/D3D11)"
        };
        var cpuOption = new Option<bool>("--cpu")
        {
            Description = "Force CPU software rendering"
        };
        var skeletonOption = new Option<bool>("--skeleton")
        {
            Description = "Render skeleton bones only (debug visualization)"
        };
        var wireframeOption = new Option<bool>("--wireframe")
        {
            Description = "Overlay mesh wireframe for geometry debugging (eyes highlighted in cyan)"
        };
        var bindPoseOption = new Option<bool>("--bind-pose")
        {
            Description = "Use bind pose (T-pose) instead of idle animation"
        };
        var animOption = new Option<string?>("--anim")
        {
            Description = "Override animation KF path (e.g. sneakmtidle.kf)"
        };
        var isoOption = new Option<bool>("--iso")
        {
            Description = "Render 4 isometric views (NE, NW, SW, SE)",
            DefaultValueFactory = _ => false
        };
        var elevationOption = new Option<float>("--elevation")
        {
            Description =
                "Camera elevation in degrees from horizontal (default: 30 iso, 0 single-view NPC renders)",
            DefaultValueFactory = _ => 30f
        };
        var sideOption = new Option<bool>("--side")
        {
            Description =
                "Render 4 side profile views (front, back, left, right) at 0° elevation",
            DefaultValueFactory = _ => false
        };
        var trimetricOption = new Option<bool>("--trimetric")
        {
            Description = "Render 4 trimetric axonometric views (Fallout 1/2 camera)",
            DefaultValueFactory = _ => false
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(extraMeshesBsaOption);
        command.Options.Add(esmOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(outputOption);
        command.Options.Add(npcOption);
        command.Options.Add(npcFileOption);
        command.Options.Add(sizeOption);
        command.Options.Add(verboseOption);
        command.Options.Add(dmpOption);
        command.Options.Add(exportEgtOption);
        command.Options.Add(compareRaceTextureFgtsOption);
        command.Options.Add(noBilinearOption);
        command.Options.Add(noEgmOption);
        command.Options.Add(noEgtOption);
        command.Options.Add(noBumpOption);
        command.Options.Add(noTexOption);
        command.Options.Add(bumpStrengthOption);
        command.Options.Add(headOnlyOption);
        command.Options.Add(noEquipOption);
        command.Options.Add(noWeaponOption);
        command.Options.Add(weaponOption);
        command.Options.Add(glbOption);
        command.Options.Add(gpuOption);
        command.Options.Add(cpuOption);
        command.Options.Add(skeletonOption);
        command.Options.Add(wireframeOption);
        command.Options.Add(bindPoseOption);
        command.Options.Add(animOption);
        command.Options.Add(isoOption);
        command.Options.Add(elevationOption);
        command.Options.Add(sideOption);
        command.Options.Add(trimetricOption);

        command.SetAction((parseResult, _) =>
        {
            Log.SetVerbose(parseResult.GetValue(verboseOption));

            var isIso = parseResult.GetValue(isoOption);
            var isSide = parseResult.GetValue(sideOption);
            var isTrimetric = parseResult.GetValue(trimetricOption);
            var viewCount = (isIso ? 1 : 0) + (isSide ? 1 : 0) + (isTrimetric ? 1 : 0);
            if (viewCount > 1)
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] --iso, --side, and --trimetric are mutually exclusive");
                return Task.CompletedTask;
            }

            var elevationExplicit = parseResult.GetResult(elevationOption) is { Implicit: false };
            var compareRaceTextureFgts = parseResult.GetValue(compareRaceTextureFgtsOption);
            if (parseResult.GetValue(glbOption))
            {
                if (compareRaceTextureFgts)
                {
                    AnsiConsole.MarkupLine(
                        "[red]Error:[/] --compare-race-fgts is not supported in GLB export mode");
                    return Task.CompletedTask;
                }

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
                        noWeaponOption,
                        sizeOption,
                        exportEgtOption,
                        noBilinearOption,
                        noBumpOption,
                        noTexOption,
                        bumpStrengthOption,
                        gpuOption,
                        cpuOption,
                        skeletonOption,
                        wireframeOption,
                        isoOption,
                        elevationOption,
                        sideOption,
                        trimetricOption,
                        out var exportSettings,
                        out var exportError))
                {
                    AnsiConsole.MarkupLine("[red]Error:[/] {0}",
                        Markup.Escape(exportError ?? "invalid GLB export options"));
                    return Task.CompletedTask;
                }

                NpcExportPipeline.Run(exportSettings!);
                return Task.CompletedTask;
            }

            if (!NpcExportCommandSupport.TryLoadNpcFilters(
                    parseResult.GetValue(npcOption),
                    parseResult.GetValue(npcFileOption),
                    out var npcFilters,
                    out var filterError))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(filterError ?? "invalid NPC filters"));
                return Task.CompletedTask;
            }

            if (compareRaceTextureFgts)
            {
                if (parseResult.GetValue(noEgtOption))
                {
                    AnsiConsole.MarkupLine(
                        "[red]Error:[/] --compare-race-fgts cannot be combined with --no-egt");
                    return Task.CompletedTask;
                }

                if (npcFilters is not { Length: > 0 })
                {
                    AnsiConsole.MarkupLine(
                        "[red]Error:[/] --compare-race-fgts requires at least one --npc or --npc-file filter");
                    return Task.CompletedTask;
                }
            }

            var settings = new NpcRenderSettings
            {
                MeshesBsaPath = parseResult.GetValue(inputArg)!,
                ExtraMeshesBsaPaths = parseResult.GetValue(extraMeshesBsaOption),
                EsmPath = parseResult.GetValue(esmOption)!,
                ExplicitTexturesBsaPaths = parseResult.GetValue(texturesBsaOption),
                OutputDir = parseResult.GetValue(outputOption)!,
                NpcFilters = npcFilters,
                SpriteSize = parseResult.GetValue(sizeOption),
                DmpPath = parseResult.GetValue(dmpOption),
                ExportEgt = parseResult.GetValue(exportEgtOption),
                CompareRaceTextureFgts = compareRaceTextureFgts,
                NoBilinear = parseResult.GetValue(noBilinearOption),
                NoEgm = parseResult.GetValue(noEgmOption),
                NoEgt = parseResult.GetValue(noEgtOption),
                NoBump = parseResult.GetValue(noBumpOption),
                NoTex = parseResult.GetValue(noTexOption),
                BumpStrength = parseResult.GetValue(bumpStrengthOption),
                HeadOnly = parseResult.GetValue(headOnlyOption),
                NoEquip = parseResult.GetValue(noEquipOption),
                NoWeapon = parseResult.GetValue(noWeaponOption),
                ForceGpu = parseResult.GetValue(gpuOption),
                ForceCpu = parseResult.GetValue(cpuOption),
                Skeleton = parseResult.GetValue(skeletonOption),
                Wireframe = parseResult.GetValue(wireframeOption),
                BindPose = parseResult.GetValue(bindPoseOption),
                AnimOverride = parseResult.GetValue(animOption),
                Camera = new CameraConfig
                {
                    Isometric = isIso,
                    ElevationDeg = parseResult.GetValue(elevationOption),
                    ElevationOverridden = elevationExplicit,
                    SideProfile = isSide,
                    Trimetric = isTrimetric
                }
            };

            NpcRenderPipeline.Run(settings);
            return Task.CompletedTask;
        });

        command.Subcommands.Add(NpcVerifyEgtCommand.Create());
        command.Subcommands.Add(NpcCompareRuntimeCaptureCommand.Create());

        return command;
    }
}
