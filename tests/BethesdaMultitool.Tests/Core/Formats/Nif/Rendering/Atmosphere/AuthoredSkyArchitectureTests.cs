using System.Numerics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

public sealed class AuthoredSkyArchitectureTests
{
    [Fact]
    public void EnvironmentOptInIsReadDynamicallyAndRequiresExactOne()
    {
        Assert.Equal("FALLOUT_VIEWER_AUTHORED_SKY", AuthoredSkyArchitecture.EnvironmentVariableName);

        var previous = EnvironmentVariables.Get(AuthoredSkyArchitecture.EnvironmentVariableName);
        try
        {
            EnvironmentVariables.Set(AuthoredSkyArchitecture.EnvironmentVariableName, null);
            Assert.False(AuthoredSkyArchitecture.Enabled);

            EnvironmentVariables.Set(AuthoredSkyArchitecture.EnvironmentVariableName, "1");
            Assert.True(AuthoredSkyArchitecture.Enabled);

            EnvironmentVariables.Set(AuthoredSkyArchitecture.EnvironmentVariableName, "0");
            Assert.False(AuthoredSkyArchitecture.Enabled);
        }
        finally
        {
            EnvironmentVariables.Set(AuthoredSkyArchitecture.EnvironmentVariableName, previous);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AtmosphereNifSelectionRequiresExplicitOptIn(bool explicitlyEnabled, bool expected)
    {
        Assert.Equal(expected,
            AuthoredSkyArchitecture.ShouldLoadAtmosphereNif(explicitlyEnabled));
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
            BethesdaGame.Skyrim, false, cube));
        Assert.Equal(cube, AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Fallout76, false, cube));
        Assert.Null(AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Fallout4, false, cube));
        Assert.Null(AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Starfield, false, cube));
        Assert.Equal(cube, AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Fallout4, true, cube));
        Assert.Null(AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
            BethesdaGame.Skyrim, false, null));
    }
}
