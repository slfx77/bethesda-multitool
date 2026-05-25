using System.CommandLine;
using FalloutXbox360Utils.CLI;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcCameraDefaultsTests
{
    [Fact]
    public void ResolveViews_SingleViewNpcDefaultsResolveToFrontOn()
    {
        var camera = new CameraConfig();

        var views = camera.ResolveViews(90f);

        var view = Assert.Single(views);
        Assert.Equal(string.Empty, view.Suffix);
        Assert.Equal(90f, view.Azimuth);
        Assert.Equal(0f, view.Elevation);
    }

    [Fact]
    public void ResolveViews_SingleViewHonorsExplicitElevationOverride()
    {
        var camera = new CameraConfig
        {
            ElevationDeg = 12f,
            ElevationOverridden = true
        };

        var views = camera.ResolveViews(90f);

        var view = Assert.Single(views);
        Assert.Equal(90f, view.Azimuth);
        Assert.Equal(12f, view.Elevation);
    }

    [Fact]
    public void RenderNpcCommand_ElevationHelpMentionsSingleViewNpcDefault()
    {
        var command = RenderNpcCommand.Create();

        var elevationOption = Assert.Single(
            command.Options.OfType<Option<float>>(),
            option => option.Name.Contains("elevation", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            "0 single-view NPC renders",
            elevationOption.Description,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, true)]   // no --elevation → Implicit
    [InlineData(true, false)]   // explicit --elevation 12 → not Implicit
    public void RenderNpcCommand_ElevationImplicitMatchesArgs(bool passElevation, bool expectedImplicit)
    {
        var command = RenderNpcCommand.Create();
        var elevationOption = Assert.Single(
            command.Options.OfType<Option<float>>(),
            option => option.Name.Contains("elevation", StringComparison.OrdinalIgnoreCase));

        var args = new List<string>
        {
            "meshes.bsa",
            "--esm", "FalloutNV.esm",
            "--output", "TestOutput",
            "--npc", "VMS38RedLucy"
        };
        if (passElevation)
        {
            args.Add("--elevation");
            args.Add("12");
        }

        var elevationResult = command.Parse(args.ToArray()).GetResult(elevationOption);

        Assert.NotNull(elevationResult);
        Assert.Equal(expectedImplicit, elevationResult.Implicit);
    }

    [Theory]
    [InlineData("--wireframe")]
    [InlineData("--compare-race-fgts")]
    public void RenderNpcCommand_FlagOptionParsesWithoutErrors(string flag)
    {
        var command = RenderNpcCommand.Create();

        var parseResult = command.Parse([
            "meshes.bsa",
            "--esm", "FalloutNV.esm",
            "--output", "TestOutput",
            "--npc", "VMS38RedLucy",
            flag
        ]);

        Assert.Empty(parseResult.Errors);
    }
}