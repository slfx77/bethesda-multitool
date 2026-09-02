using System.Numerics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

public sealed class AuthoredSkyArchitectureTests
{
    [Fact]
    public void EnvironmentOverrideIsReadDynamicallyAndRequiresExactZeroOrOne()
    {
        Assert.Equal("FALLOUT_VIEWER_AUTHORED_SKY", AuthoredSkyArchitecture.EnvironmentVariableName);

        var previous = EnvironmentVariables.Get(AuthoredSkyArchitecture.EnvironmentVariableName);
        try
        {
            EnvironmentVariables.Set(AuthoredSkyArchitecture.EnvironmentVariableName, null);
            Assert.Null(AuthoredSkyArchitecture.ExplicitOverride);

            EnvironmentVariables.Set(AuthoredSkyArchitecture.EnvironmentVariableName, "1");
            Assert.True(AuthoredSkyArchitecture.ExplicitOverride);

            EnvironmentVariables.Set(AuthoredSkyArchitecture.EnvironmentVariableName, "0");
            Assert.False(AuthoredSkyArchitecture.ExplicitOverride);

            EnvironmentVariables.Set(AuthoredSkyArchitecture.EnvironmentVariableName, "true");
            Assert.Null(AuthoredSkyArchitecture.ExplicitOverride);
        }
        finally
        {
            EnvironmentVariables.Set(AuthoredSkyArchitecture.EnvironmentVariableName, previous);
        }
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout76, null, false)]
    [InlineData(BethesdaGame.Fallout76, false, false)]
    [InlineData(BethesdaGame.Fallout76, true, true)]
    [InlineData(BethesdaGame.Unknown, null, false)]
    [InlineData(BethesdaGame.Skyrim, null, false)]
    [InlineData(BethesdaGame.Fallout4, null, false)]
    [InlineData(BethesdaGame.Starfield, null, false)]
    [InlineData(BethesdaGame.Skyrim, true, true)]
    [InlineData(BethesdaGame.Fallout4, true, true)]
    [InlineData(BethesdaGame.Starfield, true, true)]
    public void AtmosphereNifSelectionRequiresExplicitEvidenceRun(
        BethesdaGame game,
        bool? explicitOverride,
        bool expected)
    {
        Assert.Equal(expected,
            AuthoredSkyArchitecture.ShouldLoadAtmosphereNif(game, explicitOverride));
    }

    [Fact]
    public void DirectionalAmbientUpload_DefaultsOnForSkyrimAndFallout76WithoutEnablingOtherFamilies()
    {
        var cube = new AtmosphereState.ResolvedAmbientCube(
            new Vector3(1f, 2f, 3f),
            new Vector3(4f, 5f, 6f),
            new Vector3(7f, 8f, 9f),
            new Vector3(10f, 11f, 12f),
            new Vector3(13f, 14f, 15f),
            new Vector3(16f, 17f, 18f));

        Assert.Equal(cube, AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Skyrim, null, cube));
        Assert.Equal(cube, AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Fallout76, null, cube));
        Assert.Null(AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Fallout76, false, cube));
        Assert.Null(AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Fallout4, null, cube));
        Assert.Null(AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Starfield, null, cube));
        Assert.Equal(cube, AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Fallout4, true, cube));
        Assert.Null(AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Skyrim, null, null));
    }

    [Fact]
    public void ProfilerManifestRecordsTheRawAuthoredSkyOverride()
    {
        var profiler = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "Program.cs");

        Assert.Contains("[\"authoredSkyOverride\"] =", profiler, StringComparison.Ordinal);
        Assert.Contains(
            "EnvironmentVariables.Get(EnvironmentVariables.Viewer.AuthoredSky)",
            profiler,
            StringComparison.Ordinal);

        var capture = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.SceneCapture.cs");
        Assert.Contains("[\"explicitOverride\"] = AuthoredSkyArchitecture.ExplicitOverride", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"policyEnabled\"] = _authoredAtmospherePolicyEnabled", capture,
            StringComparison.Ordinal);
    }
}
