using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class FnvClassicBasicRenderTelemetryTests
{
    [Fact]
    public void WorldRenderStats_SnapshotAndResetPreserveSubmittedRouteCounts()
    {
        var stats = new WorldRenderStats
        {
            ReferenceFnvSls1009Draws = 2,
            ReferenceFnvSls1009Instances = 3,
            ReferenceFnvSls1013Draws = 4,
            ReferenceFnvSls1013Instances = 7,
            ReferencePlacedLightCount = 3,
            ReferenceFnvClassicBasicLightingEnabled = true,
            ReferenceFnvClassicBasicFallbackDraws = 5,
            ReferenceFnvClassicBasicFallbackInstances = 8,
            ReferenceFnvClassicBasicFallbackReason = "per-geometry-local-light-selection-unrecovered",
            ReferenceFnvActiveAdtBaseDraws = 6,
            ReferenceFnvActiveAdtBaseInstances = 9,
            ReferenceFnvActiveAdtBaseVertexColorDraws = 2,
            ReferenceFnvActiveAdtBaseVertexColorInstances = 3,
            ReferenceFnvActiveAdtBaseEnabled = true,
            ReferenceFnvActiveAdtBaseFallbackDraws = 7,
            ReferenceFnvActiveAdtBaseFallbackInstances = 11,
            ReferenceFnvActiveAdtBaseFallbackReason = "projected-shadow-permutation-unrecovered"
        };

        var snapshot = stats.Snapshot();

        Assert.Equal(2, snapshot.ReferenceFnvSls1009Draws);
        Assert.Equal(3, snapshot.ReferenceFnvSls1009Instances);
        Assert.Equal(4, snapshot.ReferenceFnvSls1013Draws);
        Assert.Equal(7, snapshot.ReferenceFnvSls1013Instances);
        Assert.Equal(3, snapshot.ReferencePlacedLightCount);
        Assert.True(snapshot.ReferenceFnvClassicBasicLightingEnabled);
        Assert.Equal(5, snapshot.ReferenceFnvClassicBasicFallbackDraws);
        Assert.Equal(8, snapshot.ReferenceFnvClassicBasicFallbackInstances);
        Assert.Equal("per-geometry-local-light-selection-unrecovered",
            snapshot.ReferenceFnvClassicBasicFallbackReason);
        Assert.Equal(6, snapshot.ReferenceFnvActiveAdtBaseDraws);
        Assert.Equal(9, snapshot.ReferenceFnvActiveAdtBaseInstances);
        Assert.Equal(2, snapshot.ReferenceFnvActiveAdtBaseVertexColorDraws);
        Assert.Equal(3, snapshot.ReferenceFnvActiveAdtBaseVertexColorInstances);
        Assert.True(snapshot.ReferenceFnvActiveAdtBaseEnabled);
        Assert.Equal(7, snapshot.ReferenceFnvActiveAdtBaseFallbackDraws);
        Assert.Equal(11, snapshot.ReferenceFnvActiveAdtBaseFallbackInstances);
        Assert.Equal("projected-shadow-permutation-unrecovered",
            snapshot.ReferenceFnvActiveAdtBaseFallbackReason);

        var fields = RendererProfilerTrace.StatsFields("refs.", snapshot);
        Assert.Equal(2, Assert.IsType<int>(fields["refs.refFnvSls1009Draws"]));
        Assert.Equal(3, Assert.IsType<int>(fields["refs.refFnvSls1009Instances"]));
        Assert.Equal(4, Assert.IsType<int>(fields["refs.refFnvSls1013Draws"]));
        Assert.Equal(7, Assert.IsType<int>(fields["refs.refFnvSls1013Instances"]));
        Assert.Equal(3, Assert.IsType<int>(fields["refs.refPlacedLightCount"]));
        Assert.True(Assert.IsType<bool>(fields["refs.refFnvClassicBasicLightingEnabled"]));
        Assert.Equal(5, Assert.IsType<int>(fields["refs.refFnvClassicBasicFallbackDraws"]));
        Assert.Equal(8, Assert.IsType<int>(fields["refs.refFnvClassicBasicFallbackInstances"]));
        Assert.Equal("per-geometry-local-light-selection-unrecovered",
            Assert.IsType<string>(fields["refs.refFnvClassicBasicFallbackReason"]));
        Assert.Equal(6, Assert.IsType<int>(fields["refs.refFnvActiveAdtBaseDraws"]));
        Assert.Equal(9, Assert.IsType<int>(fields["refs.refFnvActiveAdtBaseInstances"]));
        Assert.Equal(2, Assert.IsType<int>(fields["refs.refFnvActiveAdtBaseVertexColorDraws"]));
        Assert.Equal(3, Assert.IsType<int>(fields["refs.refFnvActiveAdtBaseVertexColorInstances"]));
        Assert.True(Assert.IsType<bool>(fields["refs.refFnvActiveAdtBaseEnabled"]));
        Assert.Equal(7, Assert.IsType<int>(fields["refs.refFnvActiveAdtBaseFallbackDraws"]));
        Assert.Equal(11, Assert.IsType<int>(fields["refs.refFnvActiveAdtBaseFallbackInstances"]));
        Assert.Equal("projected-shadow-permutation-unrecovered",
            Assert.IsType<string>(fields["refs.refFnvActiveAdtBaseFallbackReason"]));

        stats.Reset();

        Assert.Equal(0, stats.ReferenceFnvSls1009Draws);
        Assert.Equal(0, stats.ReferenceFnvSls1009Instances);
        Assert.Equal(0, stats.ReferenceFnvSls1013Draws);
        Assert.Equal(0, stats.ReferenceFnvSls1013Instances);
        Assert.Equal(0, stats.ReferencePlacedLightCount);
        Assert.False(stats.ReferenceFnvClassicBasicLightingEnabled);
        Assert.Equal(0, stats.ReferenceFnvClassicBasicFallbackDraws);
        Assert.Equal(0, stats.ReferenceFnvClassicBasicFallbackInstances);
        Assert.Null(stats.ReferenceFnvClassicBasicFallbackReason);
        Assert.Equal(0, stats.ReferenceFnvActiveAdtBaseDraws);
        Assert.Equal(0, stats.ReferenceFnvActiveAdtBaseInstances);
        Assert.Equal(0, stats.ReferenceFnvActiveAdtBaseVertexColorDraws);
        Assert.Equal(0, stats.ReferenceFnvActiveAdtBaseVertexColorInstances);
        Assert.False(stats.ReferenceFnvActiveAdtBaseEnabled);
        Assert.Equal(0, stats.ReferenceFnvActiveAdtBaseFallbackDraws);
        Assert.Equal(0, stats.ReferenceFnvActiveAdtBaseFallbackInstances);
        Assert.Null(stats.ReferenceFnvActiveAdtBaseFallbackReason);
    }

    [Fact]
    public void RendererSource_CountsBothInstancedAndDirectColorDrawsAfterSubmission()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "ReferenceRenderer12.cs");
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var compact = string.Concat(renderer.Where(static character => !char.IsWhiteSpace(character)));
        var compactFrame = string.Concat(frame.Where(static character => !char.IsWhiteSpace(character)));

        Assert.Contains(
            "_renderCache?.Game!=Core.Games.BethesdaGame.FalloutNewVegas",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "cmd.DrawIndexedInstanced((uint)batchState.Submesh.IndexCount,(uint)drawCount,0,0,0);" +
            "ObserveFnvActiveAdtBaseDraw(sub,textureState,drawCount);",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "cmd.DrawIndexedInstanced((uint)effectiveIndexCount,1,0,0,0);" +
            "ObserveFnvActiveAdtBaseDraw(draw.Submesh,textureState,1);",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "(flags&FnvActiveAdtBasePolicy.RuntimeActiveAdtFlag)==0",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "(flags&FnvActiveAdtBasePolicy.RuntimeActiveAdtVertexColorFlag)!=0",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.Z=FnvActiveAdtBasePolicy.ApplyRuntimeFlags(eligibility,",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"per-geometry-local-light-selection-unrecovered\"",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"projected-shadow-permutation-unrecovered\"",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "varisFalloutNewVegas=_renderCache?.Game==" +
            "Core.Games.BethesdaGame.FalloutNewVegas;",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceFnvActiveAdtBaseEnabled=isFalloutNewVegas&&",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "_references?.SetPlacedLightCount(placedLightCount);",
            compactFrame,
            StringComparison.Ordinal);
        Assert.Contains(
            "_references?.SetFnvActiveAdtBaseState(" +
            "lightingOn,projectedSunShadowActive,fogEnabled);",
            compactFrame,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RendererSource_ReResolvesBlendedRouteAtSubmissionAcrossRuntimeGateTransitions()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "ReferenceRenderer12.cs");
        var compact = string.Concat(renderer.Where(static character => !char.IsWhiteSpace(character)));

        Assert.Contains(
            "vartextureState=ResolveTextureState(draw.Submesh);" +
            "varperDraw=newPerDrawConstants{World=draw.World,AlphaState=alphaState," +
            "RenderState=draw.RenderState,TextureState=textureState,",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "cmd.DrawIndexedInstanced((uint)effectiveIndexCount,1,0,0,0);" +
            "ObserveFnvActiveAdtBaseDraw(draw.Submesh,textureState,1);",
            compact,
            StringComparison.Ordinal);
        Assert.DoesNotContain("draw.TextureState", renderer, StringComparison.Ordinal);
    }
}