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

        var views = camera.ResolveViews(90f, 0f);

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

        var views = camera.ResolveViews(90f, 0f);

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

    [Fact]
    public void RenderNpcCommand_DefaultElevationResultIsImplicit()
    {
        var command = RenderNpcCommand.Create();
        var elevationOption = Assert.Single(
            command.Options.OfType<Option<float>>(),
            option => option.Name.Contains("elevation", StringComparison.OrdinalIgnoreCase));

        var parseResult = command.Parse([
            "meshes.bsa",
            "--esm", "FalloutNV.esm",
            "--output", "TestOutput",
            "--npc", "VMS38RedLucy"
        ]);

        var elevationResult = parseResult.GetResult(elevationOption);

        Assert.NotNull(elevationResult);
        Assert.True(elevationResult.Implicit);
    }

    [Fact]
    public void RenderNpcCommand_ExplicitElevationResultIsNotImplicit()
    {
        var command = RenderNpcCommand.Create();
        var elevationOption = Assert.Single(
            command.Options.OfType<Option<float>>(),
            option => option.Name.Contains("elevation", StringComparison.OrdinalIgnoreCase));

        var parseResult = command.Parse([
            "meshes.bsa",
            "--esm", "FalloutNV.esm",
            "--output", "TestOutput",
            "--npc", "VMS38RedLucy",
            "--elevation", "12"
        ]);

        var elevationResult = parseResult.GetResult(elevationOption);

        Assert.NotNull(elevationResult);
        Assert.False(elevationResult.Implicit);
    }

    [Fact]
    public void RenderNpcCommand_WireframeOptionParsesWithoutErrors()
    {
        var command = RenderNpcCommand.Create();

        var parseResult = command.Parse([
            "meshes.bsa",
            "--esm", "FalloutNV.esm",
            "--output", "TestOutput",
            "--npc", "VMS38RedLucy",
            "--wireframe"
        ]);

        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void RenderNpcCommand_CompareRaceTextureFgtsOptionParsesWithoutErrors()
    {
        var command = RenderNpcCommand.Create();

        var parseResult = command.Parse([
            "meshes.bsa",
            "--esm", "FalloutNV.esm",
            "--output", "TestOutput",
            "--npc", "VMS38RedLucy",
            "--compare-race-fgts"
        ]);

        Assert.Empty(parseResult.Errors);
    }
}