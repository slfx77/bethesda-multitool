using System.Runtime.InteropServices;
using BethesdaMultitool;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Games;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class ModernWaterPipelineTests
{
    [Theory]
    [InlineData(BethesdaGame.Fallout4, true)]
    [InlineData(BethesdaGame.Fallout76, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, false)]
    [InlineData(BethesdaGame.Skyrim, false)]
    public void ExplicitOptInIsLimitedToRecoveredCreationGames(BethesdaGame game, bool supported)
    {
        Assert.Equal(supported, ModernWaterPipeline.Supports(game));
        Assert.Equal(supported, ModernWaterPipeline.ShouldUse(explicitlyEnabled: true, game));
        Assert.False(ModernWaterPipeline.ShouldUse(explicitlyEnabled: false, game));
    }

    [Theory]
    [InlineData(false, false, 0x001u)]
    [InlineData(true, false, 0x041u)]
    [InlineData(false, true, 0x101u)]
    [InlineData(true, true, 0x141u)]
    public void SelectorUsesOnlyEmpiricallyRecoveredTechniqueBits(
        bool depthLut,
        bool cubemap,
        uint expected)
    {
        Assert.Equal(expected, (uint)ModernWaterPipeline.SelectTechnique(depthLut, cubemap));
    }

    [Fact]
    public void TelemetryDistinguishesDefaultFailureAndSelectedTechnique()
    {
        Assert.Equal("fo4-standin", ModernWaterPipeline.TelemetryName(
            BethesdaGame.Fallout4, false, false, false));
        Assert.Equal("fo4-modern-unavailable-standin", ModernWaterPipeline.TelemetryName(
            BethesdaGame.Fallout4, true, false, false));
        Assert.Equal("fo76-modern-prepass-fallback", ModernWaterPipeline.TelemetryName(
            BethesdaGame.Fallout76, true, true, false));
        Assert.Equal("fo76-modern-0x141", ModernWaterPipeline.TelemetryName(
            BethesdaGame.Fallout76, true, true, true,
            ModernWaterTechnique.Baseline | ModernWaterTechnique.DepthLut | ModernWaterTechnique.Cubemap));
    }

    [Fact]
    public void PrepassAndFrameLayoutsStayRegisterAligned()
    {
        Assert.Equal(128, Marshal.SizeOf<ModernWaterPrepassUniforms>());
        Assert.Equal(0, Marshal.OffsetOf<ModernWaterPrepassUniforms>(nameof(ModernWaterPrepassUniforms.NormalIndex1)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<ModernWaterPrepassUniforms>(nameof(ModernWaterPrepassUniforms.ShallowCoverage)).ToInt32());
        Assert.Equal(112, Marshal.OffsetOf<ModernWaterPrepassUniforms>(nameof(ModernWaterPrepassUniforms.Ranges)).ToInt32());
        Assert.Equal(64, Marshal.SizeOf<ModernWaterFrameUniforms>());
        Assert.Equal(16, Marshal.OffsetOf<ModernWaterFrameUniforms>(nameof(ModernWaterFrameUniforms.TechniqueId)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<ModernWaterFrameUniforms>(nameof(ModernWaterFrameUniforms.Params)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<ModernWaterFrameUniforms>(nameof(ModernWaterFrameUniforms.LightSilt)).ToInt32());
        Assert.Equal(416u, ModernWaterPipeline.FrameUniformByteSize);
    }

    [Fact]
    public void DynamicOutputsTransitionOnlyBetweenPixelReadAndUavWrite()
    {
        Assert.Equal(
            ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource,
            ModernWaterPipeline.AuthoredSourceReadState);
        Assert.Equal(ResourceStates.PixelShaderResource, ModernWaterPipeline.DynamicReadState);
        Assert.Equal(ResourceStates.UnorderedAccess, ModernWaterPipeline.DynamicWriteState);
        Assert.Equal(20u, ModernWaterPipeline.MaxPointLights);
    }

    [Fact]
    public void SelectedPipelineSurvivesSnapshotAndProfilerSerialization()
    {
        var stats = new WorldRenderStats { WaterPipeline = "fo4-modern-0x141" };

        var snapshot = stats.Snapshot();
        var fields = RendererProfilerTrace.StatsFields("water.", snapshot);

        Assert.Equal("fo4-modern-0x141", snapshot.WaterPipeline);
        Assert.Equal("fo4-modern-0x141", Assert.IsType<string>(fields["water.waterPipeline"]));
    }
}
