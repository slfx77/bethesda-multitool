using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

public sealed class StarfieldEnvironmentRenderingApproximationTests
{
    [Fact]
    public void Apply_ProjectsOnlyDirectSunAndWeatherColorChannels()
    {
        var baseline = AtmosphereState.Resolve(12f, game: BethesdaGame.Starfield);
        var weather = new StarfieldWeatherSettingsPatch
        {
            Colors = new StarfieldWeatherColorSettingsPatch
            {
                FogNear = Set(0.2f, 0.4f, 0.6f, 1f, 0.5f),
                FogFar = Set(0.8f, 0.6f, 0.4f, 1f, 1f),
                Sun = Set(0.7f, 0.5f, 0.3f, 0.1f, 0.25f),
                SunGlare = Set(0.9f, 0.8f, 0.7f, 0.6f, 1f),
                // These source values are deliberately outside the bounded projection until TODD
                // and CE2 lighting semantics are recovered.
                EffectLighting = Set(0.01f, 0.02f, 0.03f, 1f, 1f),
                Sunlight = Set(0.11f, 0.12f, 0.13f, 1f, 1f),
                Moonlight = Set(0.21f, 0.22f, 0.23f, 1f, 1f)
            }
        };
        var sunPreset = new StarfieldSunPresetPatch
        {
            SunColor = Color(0.4f, 0.3f, 0.2f, 0.9f),
            SunGlareColor = Color(0.6f, 0.5f, 0.4f, 0.8f),
            SunIlluminance = 123_456f,
            SunDiskScreenSizeMin = 0.02f,
            SunDiskScreenSizeMax = 0.138f
        };

        var projected = StarfieldEnvironmentRenderingApproximation.Apply(
            baseline,
            weather,
            sunPreset);

        Assert.Equal(
            Vector3.Lerp(baseline.FogColor, new Vector3(0.2f, 0.4f, 0.6f), 0.5f),
            projected.Atmosphere.FogColor);
        Assert.Equal(new Vector3(0.8f, 0.6f, 0.4f), projected.Atmosphere.FogFarColor);
        Assert.Equal(
            Vector4.Lerp(new Vector4(0.4f, 0.3f, 0.2f, 0.9f), new Vector4(0.7f, 0.5f, 0.3f, 0.1f), 0.25f),
            projected.Atmosphere.SunDiscColor);
        Assert.Equal(new Vector4(0.9f, 0.8f, 0.7f, 0.6f), projected.Atmosphere.SunGlareColor);

        Assert.Equal(baseline.SunColor, projected.Atmosphere.SunColor);
        Assert.Equal(baseline.SunIntensity, projected.Atmosphere.SunIntensity);
        Assert.Equal(baseline.AmbientColor, projected.Atmosphere.AmbientColor);
        Assert.Equal(baseline.SkyTopColor, projected.Atmosphere.SkyTopColor);
        Assert.Equal(baseline.FogNear, projected.Atmosphere.FogNear);
        Assert.Equal(baseline.FogFar, projected.Atmosphere.FogFar);

        Assert.Equal(
            StarfieldEnvironmentApproximationChannels.SunPresetDiscColor |
            StarfieldEnvironmentApproximationChannels.SunPresetGlareColor |
            StarfieldEnvironmentApproximationChannels.WeatherFogNear |
            StarfieldEnvironmentApproximationChannels.WeatherFogFar |
            StarfieldEnvironmentApproximationChannels.WeatherSunDisc |
            StarfieldEnvironmentApproximationChannels.WeatherSunGlare,
            projected.AppliedChannels);
        Assert.Equal(StarfieldEnvironmentApproximationChannels.None, projected.RejectedChannels);
        Assert.Equal(
            "starfield-source-backed-environment-approx",
            StarfieldEnvironmentApproximationResult.Name);
    }

    [Fact]
    public void Apply_RejectsUnsupportedOrIncompleteBlendablesWithoutChangingFallback()
    {
        var baseline = AtmosphereState.Resolve(12f, game: BethesdaGame.Starfield);
        var weather = new StarfieldWeatherSettingsPatch
        {
            Colors = new StarfieldWeatherColorSettingsPatch
            {
                FogNear = Set(1f, 0f, 0f, 1f, 0.5f) with { Operation = "Multiply" },
                FogFar = Set(1f, 0f, 0f, 1f, 0.5f) with { BlendAmount = float.NaN },
                Sun = Set(1f, 0f, 0f, 1f, 0.5f) with
                {
                    Value = new StarfieldFloat4Patch { X = 1f, Y = 0f, Z = 0f }
                },
                SunGlare = Set(1f, 0f, 0f, 1f, 0.5f) with { BlendAmount = 2f }
            }
        };
        var sunPreset = new StarfieldSunPresetPatch
        {
            SunColor = new StarfieldSunPresetFloat4Patch { X = 1f, Y = 1f, Z = 1f },
            SunGlareColor = Color(float.PositiveInfinity, 1f, 1f, 1f)
        };

        var projected = StarfieldEnvironmentRenderingApproximation.Apply(
            baseline,
            weather,
            sunPreset);

        Assert.Equal(baseline, projected.Atmosphere);
        Assert.Equal(StarfieldEnvironmentApproximationChannels.None, projected.AppliedChannels);
        Assert.Equal(
            StarfieldEnvironmentApproximationChannels.SunPresetDiscColor |
            StarfieldEnvironmentApproximationChannels.SunPresetGlareColor |
            StarfieldEnvironmentApproximationChannels.WeatherFogNear |
            StarfieldEnvironmentApproximationChannels.WeatherFogFar |
            StarfieldEnvironmentApproximationChannels.WeatherSunDisc |
            StarfieldEnvironmentApproximationChannels.WeatherSunGlare,
            projected.RejectedChannels);
    }

    private static StarfieldBlendableColorPatch Set(
        float x,
        float y,
        float z,
        float w,
        float blendAmount) => new()
    {
        Operation = "Set",
        Value = new StarfieldFloat4Patch { X = x, Y = y, Z = z, W = w },
        BlendAmount = blendAmount
    };

    private static StarfieldSunPresetFloat4Patch Color(float x, float y, float z, float w) => new()
    {
        X = x,
        Y = y,
        Z = z,
        W = w
    };
}
