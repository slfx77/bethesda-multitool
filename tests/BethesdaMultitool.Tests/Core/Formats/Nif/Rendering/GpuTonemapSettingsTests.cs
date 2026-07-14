using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Locks the tonemap parameter presets to the shipped FalloutNV.esm imagespace records
///     (DefaultImageSpaceInterior 0x160 / DefaultImageSpaceExterior 0x161 — see
///     docs/research/fnv_engine_hdr_imagespace.md) and the bloom gating rules: bloom is engine-mode
///     only this cut, and FALLOUT_VIEWER_BLOOM=0|off kills it for A/Bs.
/// </summary>
public sealed class GpuTonemapSettingsTests
{
    [Fact]
    public void EngineExteriorDefaults_MatchShippedImageSpace0x161()
    {
        var s = GpuTonemapSettings.EngineExteriorDefaults;

        Assert.True(s.BloomEnabled);
        Assert.Equal(8f, s.BlurRadius);
        Assert.Equal(2f, s.BlurPasses);
        Assert.Equal(1.5f, s.BrightScale);
        Assert.Equal(0.35f, s.BrightClamp);
        Assert.Equal(1.2f, s.EmissiveMult);
        Assert.Equal(1.2f, s.TargetLum);
    }

    [Fact]
    public void EngineInteriorDefaults_MatchShippedImageSpace0x160()
    {
        var s = GpuTonemapSettings.EngineInteriorDefaults;

        Assert.True(s.BloomEnabled);
        Assert.Equal(7f, s.BlurRadius);
        Assert.Equal(2f, s.BlurPasses);
        Assert.Equal(2f, s.BrightScale);
        Assert.Equal(0.35f, s.BrightClamp);
        Assert.Equal(1f, s.EmissiveMult);
        Assert.Equal(1.0f, s.TargetLum);
    }

    [Fact]
    public void GammaAcesDefaults_BloomOff()
    {
        // Skyrim/FO4/76 bloom rides their imagespace port; the ACES stand-in must not bloom.
        Assert.False(GpuTonemapSettings.GammaAcesDefaults.BloomEnabled);
        Assert.Equal(1f, GpuTonemapSettings.GammaAcesDefaults.EmissiveMult);
    }

    [Fact]
    public void ResolveEmissiveMult_PreservesAuthoredZero()
    {
        Assert.Equal(0f, GpuTonemapSettings.ResolveEmissiveMult(
            familyDefault: 1.2f, authoredValue: 0f, hdrEnabled: true,
            imagespaceModifiersEnabled: true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ResolveEmissiveMult_InactivePathIsNeutral(
        bool hdrEnabled, bool imagespaceModifiersEnabled)
    {
        Assert.Equal(1f, GpuTonemapSettings.ResolveEmissiveMult(
            familyDefault: 1.2f, authoredValue: 4f, hdrEnabled,
            imagespaceModifiersEnabled));
    }

    [Fact]
    public void ResolveEmissiveMult_MissingAuthoredValueUsesFamilyDefault()
    {
        Assert.Equal(1.2f, GpuTonemapSettings.ResolveEmissiveMult(
            familyDefault: 1.2f, authoredValue: null, hdrEnabled: true,
            imagespaceModifiersEnabled: true));
    }

    [Fact]
    public void ApplyOverrides_TonemapOperatorDoesNotChangeEmissiveMult()
    {
        var previous = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP");
        try
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP", "aces");
            var s = GpuTonemapSettings.ApplyOverrides(
                GpuTonemapSettings.EngineExteriorDefaults with { EmissiveMult = 0f });

            Assert.Equal(GpuTonemapMode.GammaAces, s.Mode);
            Assert.Equal(0f, s.EmissiveMult);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP", previous);
        }
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapMode.EngineFo3Fnv, true)]
    [InlineData(BethesdaGame.Fallout3, (int)GpuTonemapMode.EngineFo3Fnv, true)]
    [InlineData(BethesdaGame.Oblivion, (int)GpuTonemapMode.EngineFo3Fnv, true)]
    [InlineData(BethesdaGame.Skyrim, (int)GpuTonemapMode.GammaAces, false)]
    [InlineData(BethesdaGame.Morrowind, (int)GpuTonemapMode.LegacyClamp, false)]
    public void ForGame_BloomFollowsEngineMode(BethesdaGame game, int expectedMode, bool expectedBloom)
    {
        var s = GpuTonemapSettings.ForGame(game);

        Assert.Equal((GpuTonemapMode)expectedMode, s.Mode);
        Assert.Equal(expectedBloom, s.BloomEnabled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("OFF")]
    public void ApplyOverrides_BloomKillSwitch(string value)
    {
        var previous = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_BLOOM");
        try
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", value);
            var s = GpuTonemapSettings.ApplyOverrides(GpuTonemapSettings.EngineExteriorDefaults);
            Assert.False(s.BloomEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", previous);
        }
    }

    [Fact]
    public void ApplyOverrides_NoEnv_KeepsBloom()
    {
        var previous = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_BLOOM");
        try
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", null);
            var s = GpuTonemapSettings.ApplyOverrides(GpuTonemapSettings.EngineExteriorDefaults);
            Assert.True(s.BloomEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", previous);
        }
    }

    [Fact]
    public void ApplyOverrides_ForceOn_EnablesBloomOnAcesPreset()
    {
        var previous = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_BLOOM");
        try
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", "1");
            var s = GpuTonemapSettings.ApplyOverrides(GpuTonemapSettings.GammaAcesDefaults);
            Assert.True(s.BloomEnabled);
            // The ACES preset carries usable engine bloom params so the force-on actually blooms.
            Assert.True(s.BrightScale > 0f);
            Assert.True(s.BlurPasses >= 1f);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", previous);
        }
    }
}
