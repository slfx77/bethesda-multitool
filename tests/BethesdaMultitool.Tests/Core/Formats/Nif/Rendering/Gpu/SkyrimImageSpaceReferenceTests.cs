using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class SkyrimImageSpaceReferenceTests
{
    [Theory]
    [InlineData("skyrim")]
    [InlineData("SKYRIM-RETAIL")]
    public void ParseTonemapModeOverride_RecognizesExplicitSkyrimDiagnostic(string value)
    {
        Assert.Equal(GpuTonemapMode.EngineSkyrim,
            GpuTonemapSettings.ParseTonemapModeOverride(value));
    }

    [Fact]
    public void EngineSkyrimTraits_UseDedicatedReductionAdaptAndRecoveredOldrimBloom()
    {
        var traits = GpuTonemapModeTraits.For(GpuTonemapMode.EngineSkyrim, enabled: true);

        Assert.True(traits.IsHdrDisplayOperator);
        Assert.True(traits.UsesClassicReduction);
        Assert.True(traits.UsesAdaptation);
        Assert.True(traits.AllowsClassicBloom);
        Assert.True(GpuTonemapModeTraits.IsBloomActive(
            GpuTonemapMode.EngineSkyrim, enabled: true, bloomEnabled: true, brightScale: 1f));
    }

    [Fact]
    public void ApplyModernImageSpace_OldrimEnablesAuthoredBloomButFo4RemainsDisabled()
    {
        var oldrim = GpuTonemapSettings.ApplyModernImageSpace(
            GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Skyrim) with
            {
                Mode = GpuTonemapMode.EngineSkyrim
            },
            new ImageSpaceRecord
            {
                ModernHdr = new ImageSpaceModernHdr
                {
                    Family = ImageSpaceModernFamily.Skyrim,
                    BloomBlurRadius = 8f,
                    BloomThreshold = 0.7f,
                    BloomScale = 1f
                }
            });
        var fo4 = GpuTonemapSettings.ApplyModernImageSpace(
            GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Fallout4),
            new ImageSpaceRecord
            {
                ModernHdr = new ImageSpaceModernHdr
                {
                    Family = ImageSpaceModernFamily.Fallout4,
                    BloomBlurRadius = 8f,
                    BloomThreshold = 0.7f,
                    BloomScale = 1f
                }
            });

        Assert.True(oldrim.BloomEnabled);
        Assert.Equal(8f, oldrim.BlurRadius);
        Assert.Equal(0.7f, oldrim.BrightClamp);
        Assert.Equal(1f, oldrim.BrightScale);
        Assert.False(fo4.BloomEnabled);
    }

    [Fact]
    public void ResolveAdaptationFactors_WhiteRunIntIs_MatchesRecoveredCpuEquations()
    {
        var factors = SkyrimImageSpaceReference.ResolveAdaptationFactors(
            eyeAdaptSpeed: 10f,
            eyeAdaptStrength: 3f,
            deltaSeconds: 1f / 60f);

        AssertClose(0.051316702f, factors.Fast);
        AssertClose(0.016807920f, factors.Slow);
        Assert.True(factors.Fast > factors.Slow);
    }

    [Theory]
    // Row 2 is the floor case: |delta * factor| = 0.0005 sits under the 1/256 minimum step, so the
    // step clamps up to exactly 1/256 (0.50390625). A factor of 0.01 would give 0.005 > 1/256 and
    // the proportional lane would win instead (0.505), which rows 3 and 4 already cover.
    [InlineData(0.5f, 0.5001f, 0.01f, 0.5001f)]
    [InlineData(0.5f, 1.0f, 0.001f, 0.50390625f)]
    [InlineData(0.5f, 1.0f, 0.25f, 0.625f)]
    [InlineData(0.5f, 0.0f, 0.25f, 0.375f)]
    public void StepAdaptedLuminance_UsesMinimumStepWithoutOvershoot(
        float previous,
        float current,
        float factor,
        float expected)
    {
        AssertClose(expected, SkyrimImageSpaceReference.StepAdaptedLuminance(previous, current, factor));
    }

    [Fact]
    public void ApplyTonemapBlendCinematic_WhiteRunIntIs_MatchesPair13InstructionOrder()
    {
        var settings = GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Skyrim) with
        {
            White = 1f,
            ReceiveBloomThreshold = 0.7f,
            Saturation = 0.95f,
            Brightness = 0.95f,
            Contrast = 1.22f,
            TintR = 0f,
            TintG = 0f,
            TintB = 0f,
            TintAmount = 0f
        };

        var actual = SkyrimImageSpaceReference.ApplyTonemapBlendCinematic(
            new Vector3(0.25f, 0.5f, 1f),
            new Vector3(0.1f, 0.2f, 0.3f),
            adaptedSlow: 0.4f,
            adaptedFast: 0.4f,
            settings);

        AssertClose(0.24148833f, actual.X);
        AssertClose(0.54065186f, actual.Y);
        AssertClose(1.1150779f, actual.Z);
    }

    [Fact]
    public void ApplyTonemapBlendCinematic_UsesFastToSlowAdaptedRatio()
    {
        var settings = GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Skyrim) with
        {
            White = 1f,
            ReceiveBloomThreshold = 0.7f,
            Saturation = 0.95f,
            Brightness = 0.95f,
            Contrast = 1.22f,
            TintAmount = 0f
        };

        var actual = SkyrimImageSpaceReference.ApplyTonemapBlendCinematic(
            new Vector3(0.25f, 0.5f, 1f),
            new Vector3(0.1f, 0.2f, 0.3f),
            adaptedSlow: 0.4f,
            adaptedFast: 0.6f,
            settings);

        AssertClose(0.3668720f, actual.X);
        AssertClose(0.7797657f, actual.Y);
        AssertClose(1.6055533f, actual.Z);
    }

    private static void AssertClose(float expected, float actual)
    {
        Assert.InRange(MathF.Abs(expected - actual), 0f, 2e-6f);
    }
}
