using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class ShadowCascadeSubmissionPolicyTests
{
    [Fact]
    public void All_zero_cascades_reject_a_draw_but_any_positive_cascade_accepts_it()
    {
        Assert.False(ShadowCascadeSubmissionPolicy.HasAnyInstances(12, [0, 0, 0, 0]));
        Assert.True(ShadowCascadeSubmissionPolicy.HasAnyInstances(12, [0, 3, 0, 0]));
        Assert.False(ShadowCascadeSubmissionPolicy.HasAnyInstances(0, [1, 1, 1, 1]));
    }

    [Theory]
    [InlineData(12, -3, 0)]
    [InlineData(12, 0, 0)]
    [InlineData(12, 5, 5)]
    [InlineData(12, 99, 12)]
    [InlineData(-1, 5, 0)]
    public void Cascade_counts_are_clamped_to_the_addressable_draw_range(
        int drawCount,
        int cascadeCount,
        int expected)
    {
        Assert.Equal(
            expected,
            ShadowCascadeSubmissionPolicy.ClampInstanceCount(drawCount, cascadeCount));
    }

    [Fact]
    public void Compatible_prefixes_cap_the_source_tail_at_the_widest_cascade()
    {
        Assert.Equal(
            17,
            ShadowCascadeSubmissionPolicy.UsefulSourceTailCount(
                sourceCount: 40,
                cascadePrefixes: [0, 9, 17, 14],
                prefixesCompatible: true));
        Assert.Equal(
            40,
            ShadowCascadeSubmissionPolicy.UsefulSourceTailCount(
                sourceCount: 40,
                cascadePrefixes: [0, 9, 17, 99],
                prefixesCompatible: true));
    }

    [Fact]
    public void Incompatible_or_incomplete_prefixes_preserve_the_full_tail()
    {
        Assert.Equal(
            40,
            ShadowCascadeSubmissionPolicy.UsefulSourceTailCount(
                sourceCount: 40,
                cascadePrefixes: [0, 0, 0, 0],
                prefixesCompatible: false));
        Assert.Equal(
            40,
            ShadowCascadeSubmissionPolicy.UsefulSourceTailCount(
                sourceCount: 40,
                cascadePrefixes: [0, 0, 0],
                prefixesCompatible: true));
    }

    [Fact]
    public void Renderer_skips_empty_color_work_before_material_setup_and_keeps_shadow_replay_independent()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");

        SourceContract.AssertOrder(
            source,
            "if (drawCount == 0 && !canCaptureShadowTail)",
            "startInstance += (uint)shadowCount;",
            "continue;",
            "var textureState = ResolveTextureState(sub);");
        Assert.Contains(
            "if (!submitIndirect && drawCount > 0 && !ReferenceEquals(currentPso, batchState.Pso))",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if (tailHasCascadeInstances &&", source, StringComparison.Ordinal);
        Assert.Contains("if (mainHasCascadeInstances)", source, StringComparison.Ordinal);

        // The replay remains self-contained: it cannot depend on color-pass state that is now
        // intentionally omitted for shadow-only batches.
        SourceContract.AssertOrder(
            source,
            "public bool RenderShadowDepth(",
            "cmd.SetPipelineState(pso);",
            "cmd.SetGraphicsRootShaderResourceView(",
            "cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv",
            "cmd.IASetVertexBuffers(0, draw.VertexBufferView);",
            "cmd.IASetIndexBuffer(draw.IndexBufferView);",
            "cmd.DrawIndexedInstanced((uint)draw.IndexCount");
    }
}
