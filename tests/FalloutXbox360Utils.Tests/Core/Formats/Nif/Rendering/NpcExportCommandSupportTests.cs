using System.CommandLine;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcExportCommandSupportTests
{
    [Fact]
    public void TryCreateSettings_WithoutAnim_DefaultsToBindPose()
    {
        var harness = CreateHarness();
        var parseResult = harness.Command.Parse([
            "meshes.bsa",
            "--esm", "FalloutNV.esm",
            "--output", "glb-out",
            "--npc", "CraigBoone"
        ]);

        var success = NpcExportCommandSupport.TryCreateSettings(
            parseResult,
            harness.MeshesBsaArgument,
            harness.EsmOption,
            harness.TexturesBsaOption,
            harness.OutputOption,
            harness.NpcOption,
            harness.VerboseOption,
            harness.DmpOption,
            harness.HeadOnlyOption,
            harness.NoEquipOption,
            harness.NoEgmOption,
            harness.NoEgtOption,
            harness.BindPoseOption,
            harness.AnimOption,
            harness.WeaponOption,
            harness.NoWeaponOption,
            harness.RasterSizeOption,
            harness.ExportEgtOption,
            harness.NoBilinearOption,
            harness.NoBumpOption,
            harness.NoTexOption,
            harness.BumpStrengthOption,
            harness.GpuOption,
            harness.CpuOption,
            harness.SkeletonOption,
            harness.WireframeOption,
            harness.IsoOption,
            harness.ElevationOption,
            harness.SideOption,
            harness.TrimetricOption,
            out var settings,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(settings);
        Assert.True(settings.BindPose);
        Assert.Null(settings.AnimOverride);
        Assert.False(settings.IncludeWeapon);
    }

    [Fact]
    public void TryCreateSettings_WithAnimOverride_UsesPosedExport()
    {
        var harness = CreateHarness();
        var parseResult = harness.Command.Parse([
            "meshes.bsa",
            "--esm", "FalloutNV.esm",
            "--output", "glb-out",
            "--anim", "locomotion\\sneakmtidle.kf",
            "--weapon"
        ]);

        var success = NpcExportCommandSupport.TryCreateSettings(
            parseResult,
            harness.MeshesBsaArgument,
            harness.EsmOption,
            harness.TexturesBsaOption,
            harness.OutputOption,
            harness.NpcOption,
            harness.VerboseOption,
            harness.DmpOption,
            harness.HeadOnlyOption,
            harness.NoEquipOption,
            harness.NoEgmOption,
            harness.NoEgtOption,
            harness.BindPoseOption,
            harness.AnimOption,
            harness.WeaponOption,
            harness.NoWeaponOption,
            harness.RasterSizeOption,
            harness.ExportEgtOption,
            harness.NoBilinearOption,
            harness.NoBumpOption,
            harness.NoTexOption,
            harness.BumpStrengthOption,
            harness.GpuOption,
            harness.CpuOption,
            harness.SkeletonOption,
            harness.WireframeOption,
            harness.IsoOption,
            harness.ElevationOption,
            harness.SideOption,
            harness.TrimetricOption,
            out var settings,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(settings);
        Assert.False(settings.BindPose);
        Assert.Equal("locomotion\\sneakmtidle.kf", settings.AnimOverride);
        Assert.True(settings.IncludeWeapon);
    }

    [Fact]
    public void TryCreateSettings_BindPoseSuppressesAnimOverride()
    {
        var harness = CreateHarness();
        var parseResult = harness.Command.Parse([
            "meshes.bsa",
            "--esm", "FalloutNV.esm",
            "--output", "glb-out",
            "--bind-pose",
            "--anim", "locomotion\\mtidle.kf"
        ]);

        var success = NpcExportCommandSupport.TryCreateSettings(
            parseResult,
            harness.MeshesBsaArgument,
            harness.EsmOption,
            harness.TexturesBsaOption,
            harness.OutputOption,
            harness.NpcOption,
            harness.VerboseOption,
            harness.DmpOption,
            harness.HeadOnlyOption,
            harness.NoEquipOption,
            harness.NoEgmOption,
            harness.NoEgtOption,
            harness.BindPoseOption,
            harness.AnimOption,
            harness.WeaponOption,
            harness.NoWeaponOption,
            harness.RasterSizeOption,
            harness.ExportEgtOption,
            harness.NoBilinearOption,
            harness.NoBumpOption,
            harness.NoTexOption,
            harness.BumpStrengthOption,
            harness.GpuOption,
            harness.CpuOption,
            harness.SkeletonOption,
            harness.WireframeOption,
            harness.IsoOption,
            harness.ElevationOption,
            harness.SideOption,
            harness.TrimetricOption,
            out var settings,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(settings);
        Assert.True(settings.BindPose);
        Assert.Null(settings.AnimOverride);
    }

    [Fact]
    public void TryCreateSettings_RejectsRasterOnlyFlagsInGlbMode()
    {
        var harness = CreateHarness();
        var parseResult = harness.Command.Parse([
            "meshes.bsa",
            "--esm", "FalloutNV.esm",
            "--output", "glb-out",
            "--wireframe"
        ]);

        var success = NpcExportCommandSupport.TryCreateSettings(
            parseResult,
            harness.MeshesBsaArgument,
            harness.EsmOption,
            harness.TexturesBsaOption,
            harness.OutputOption,
            harness.NpcOption,
            harness.VerboseOption,
            harness.DmpOption,
            harness.HeadOnlyOption,
            harness.NoEquipOption,
            harness.NoEgmOption,
            harness.NoEgtOption,
            harness.BindPoseOption,
            harness.AnimOption,
            harness.WeaponOption,
            harness.NoWeaponOption,
            harness.RasterSizeOption,
            harness.ExportEgtOption,
            harness.NoBilinearOption,
            harness.NoBumpOption,
            harness.NoTexOption,
            harness.BumpStrengthOption,
            harness.GpuOption,
            harness.CpuOption,
            harness.SkeletonOption,
            harness.WireframeOption,
            harness.IsoOption,
            harness.ElevationOption,
            harness.SideOption,
            harness.TrimetricOption,
            out _,
            out var error);

        Assert.False(success);
        Assert.Equal("--wireframe is not supported in GLB export mode", error);
    }

    [Fact]
    public void TryLoadNpcFilters_MergesInlineAndFileFilters()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tempFile, [
                "",
                "# comment",
                "; comment",
                "0x00012345",
                "CraigBoone",
                "CraigBoone"
            ]);

            var success = NpcExportCommandSupport.TryLoadNpcFilters(
                ["Veronica"],
                tempFile,
                out var filters,
                out var error);

            Assert.True(success);
            Assert.Null(error);
            Assert.NotNull(filters);
            Assert.Equal(["Veronica", "0x00012345", "CraigBoone"], filters);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static CommandHarness CreateHarness()
    {
        var command = new Command("npc");
        var meshesBsaArgument = new Argument<string>("meshes-bsa");
        var esmOption = new Option<string>("--esm") { Required = true };
        var texturesBsaOption = new Option<string[]?>("--textures-bsa")
        {
            AllowMultipleArgumentsPerToken = true
        };
        var outputOption = new Option<string>("-o", "--output") { Required = true };
        var npcOption = new Option<string[]?>("--npc")
        {
            AllowMultipleArgumentsPerToken = true
        };
        var verboseOption = new Option<bool>("-v", "--verbose");
        var dmpOption = new Option<string?>("--dmp");
        var headOnlyOption = new Option<bool>("--head-only");
        var noEquipOption = new Option<bool>("--no-equip");
        var noEgmOption = new Option<bool>("--no-egm");
        var noEgtOption = new Option<bool>("--no-egt");
        var bindPoseOption = new Option<bool>("--bind-pose");
        var animOption = new Option<string?>("--anim");
        var weaponOption = new Option<bool>("--weapon");
        var noWeaponOption = new Option<bool>("--no-weapon");
        var rasterSizeOption = new Option<int>("--size");
        var exportEgtOption = new Option<bool>("--export-egt");
        var noBilinearOption = new Option<bool>("--no-bilinear");
        var noBumpOption = new Option<bool>("--no-bump");
        var noTexOption = new Option<bool>("--no-tex");
        var bumpStrengthOption = new Option<float?>("--bump-strength");
        var gpuOption = new Option<bool>("--gpu");
        var cpuOption = new Option<bool>("--cpu");
        var skeletonOption = new Option<bool>("--skeleton");
        var wireframeOption = new Option<bool>("--wireframe");
        var isoOption = new Option<bool>("--iso");
        var elevationOption = new Option<float>("--elevation");
        var sideOption = new Option<bool>("--side");
        var trimetricOption = new Option<bool>("--trimetric");

        command.Arguments.Add(meshesBsaArgument);
        command.Options.Add(esmOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(outputOption);
        command.Options.Add(npcOption);
        command.Options.Add(verboseOption);
        command.Options.Add(dmpOption);
        command.Options.Add(headOnlyOption);
        command.Options.Add(noEquipOption);
        command.Options.Add(noEgmOption);
        command.Options.Add(noEgtOption);
        command.Options.Add(bindPoseOption);
        command.Options.Add(animOption);
        command.Options.Add(weaponOption);
        command.Options.Add(noWeaponOption);
        command.Options.Add(rasterSizeOption);
        command.Options.Add(exportEgtOption);
        command.Options.Add(noBilinearOption);
        command.Options.Add(noBumpOption);
        command.Options.Add(noTexOption);
        command.Options.Add(bumpStrengthOption);
        command.Options.Add(gpuOption);
        command.Options.Add(cpuOption);
        command.Options.Add(skeletonOption);
        command.Options.Add(wireframeOption);
        command.Options.Add(isoOption);
        command.Options.Add(elevationOption);
        command.Options.Add(sideOption);
        command.Options.Add(trimetricOption);

        return new CommandHarness(
            command,
            meshesBsaArgument,
            esmOption,
            texturesBsaOption,
            outputOption,
            npcOption,
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
            rasterSizeOption,
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
            trimetricOption);
    }

    private sealed record CommandHarness(
        Command Command,
        Argument<string> MeshesBsaArgument,
        Option<string> EsmOption,
        Option<string[]?> TexturesBsaOption,
        Option<string> OutputOption,
        Option<string[]?> NpcOption,
        Option<bool> VerboseOption,
        Option<string?> DmpOption,
        Option<bool> HeadOnlyOption,
        Option<bool> NoEquipOption,
        Option<bool> NoEgmOption,
        Option<bool> NoEgtOption,
        Option<bool> BindPoseOption,
        Option<string?> AnimOption,
        Option<bool> WeaponOption,
        Option<bool> NoWeaponOption,
        Option<int> RasterSizeOption,
        Option<bool> ExportEgtOption,
        Option<bool> NoBilinearOption,
        Option<bool> NoBumpOption,
        Option<bool> NoTexOption,
        Option<float?> BumpStrengthOption,
        Option<bool> GpuOption,
        Option<bool> CpuOption,
        Option<bool> SkeletonOption,
        Option<bool> WireframeOption,
        Option<bool> IsoOption,
        Option<float> ElevationOption,
        Option<bool> SideOption,
        Option<bool> TrimetricOption);
}