using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Pins the UNORM16 packing that halved the terrain blend-weight stream. The claim being
///     defended is that the error cannot reach the framebuffer: a weight's error becomes a colour
///     error of at most that fraction of the difference between two layers, and 1/65535 is far below
///     one step of an 8-bit output channel.
/// </summary>
public sealed class TerrainBlendWeightPackingTests
{
    /// <summary>One step of an 8-bit output channel — the coarsest thing the renderer can show.</summary>
    private const float OneOutputLevel = 1f / 255f;

    [Fact]
    public void The_endpoints_are_exact()
    {
        // A zero weight must stay exactly zero: an inactive slot that packed to 1/65535 would tint
        // every vertex with a texture that is not supposed to contribute at all. And a lone active
        // slot must reach exactly 1, or a single-layer cell renders slightly darkened.
        Assert.Equal((ushort)0, TerrainBlendWeightPacking.Pack(0f));
        Assert.Equal((ushort)65535, TerrainBlendWeightPacking.Pack(1f));
        Assert.Equal(0f, TerrainBlendWeightPacking.Unpack(0));
        Assert.Equal(1f, TerrainBlendWeightPacking.Unpack(65535));
    }

    [Theory]
    [InlineData(-0.001f, 0)]
    [InlineData(-5f, 0)]
    [InlineData(1.00005f, 65535)] // the renormalisation tolerance can leave a weight just over 1
    [InlineData(2f, 65535)]
    [InlineData(float.NaN, 0)]
    public void Out_of_range_weights_clamp_rather_than_wrapping(float weight, int expected)
    {
        // Wrapping is the failure that matters: a weight of 1.00005 becoming ~0 would drop a layer
        // entirely at one vertex, which reads as a single black or mistextured spike.
        Assert.Equal((ushort)expected, TerrainBlendWeightPacking.Pack(weight));
    }

    [Fact]
    public void The_worst_case_error_is_far_below_one_output_level()
    {
        // Swept rather than argued: 65,536 samples across the whole range.
        var worst = 0f;
        for (var i = 0; i <= 65_536; i++)
        {
            worst = MathF.Max(worst, TerrainBlendWeightPacking.RoundTripError(i / 65_536f));
        }

        Assert.True(worst <= 1f / 65_535f, $"worst error {worst} exceeds one UNORM16 step");
        Assert.True(worst * 100f < OneOutputLevel,
            $"worst error {worst} is no longer two orders below one 8-bit output level ({OneOutputLevel})");
    }

    [Fact]
    public void Unorm8_would_not_have_cleared_the_same_bar()
    {
        // Why the ruling went to 16 bits. An 8-bit weight's quantisation is the same order as the
        // output quantisation itself, so it can move a rendered pixel — visible as banding along a
        // smooth two-layer blend. Computed here rather than asserted from the production code,
        // which has no UNORM8 path.
        const float unorm8Step = 1f / 255f;

        Assert.True(unorm8Step >= OneOutputLevel,
            "an 8-bit weight step is at least one output level, so it is not free");
    }

    [Fact]
    public void Packing_is_monotonic_so_a_gradient_cannot_invert()
    {
        // A non-monotonic pack would make a smooth blend reverse direction somewhere across a cell,
        // which is far more visible than the quantisation itself.
        ushort previous = 0;
        for (var i = 0; i <= 4096; i++)
        {
            var packed = TerrainBlendWeightPacking.Pack(i / 4096f);
            Assert.True(packed >= previous, $"pack({i}/4096) went backwards: {packed} < {previous}");
            previous = packed;
        }
    }

    [Fact]
    public void A_partition_of_unity_stays_a_partition_of_unity_within_a_step()
    {
        // Weights arrive summing to 1. They are packed independently, so the sum can drift by up to
        // half a step per weight — bounded here at the full 16-slot width, because the fragment
        // shader's Σ wᵢ·textureᵢ darkens or brightens by exactly this drift.
        var weights = new float[16];
        for (var s = 0; s < weights.Length; s++)
        {
            weights[s] = 1f / weights.Length;
        }

        var sum = 0f;
        foreach (var w in weights)
        {
            sum += TerrainBlendWeightPacking.Unpack(TerrainBlendWeightPacking.Pack(w));
        }

        Assert.True(MathF.Abs(sum - 1f) < OneOutputLevel / 10f,
            $"16 packed weights summed to {sum}, drifting more than a tenth of an output level");
    }

    [Fact]
    public void The_wire_width_matches_the_slot_count()
    {
        // The widest stride the input layout declares has to be exactly this, and the residency
        // policy's upper bound predicts from it. Stated independently: 16 slots × 2 bytes.
        // (Asserting it equals TerrainVertexLayout's stride would be a tautology — that constant IS
        // this one; TerrainVertexLayoutTests checks the stride against the elements that tile it.)
        Assert.Equal(32, TerrainBlendWeightPacking.BytesPerVertex);
        Assert.Equal(16 * sizeof(ushort), TerrainBlendWeightPacking.BytesPerVertex);
    }

    [Theory]
    // Every slot count from "no texture set at all" to the full ceiling, with the quad boundaries
    // (4/5, 8/9, 12/13) written out — those are where an off-by-one in the ceil would show up as a
    // cell that drops its last four layers.
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 3)]
    [InlineData(12, 3)]
    [InlineData(13, 4)]
    [InlineData(16, 4)]
    public void A_cell_carries_only_the_quads_its_slot_count_reaches(int activeSlots, int expectedQuads)
    {
        Assert.Equal(expectedQuads, TerrainBlendWeightPacking.QuadCountFor(activeSlots));

        // The invariant that makes dropping the rest EXACT rather than lossy: every active slot has
        // to fall inside the quads kept. A slot beyond them would be a layer silently deleted from
        // the cell, which is a visible retexturing rather than a quantisation.
        Assert.True(activeSlots <= expectedQuads * TerrainBlendWeightPacking.SlotsPerQuad,
            $"{activeSlots} active slots do not fit in {expectedQuads} quads");
    }

    [Fact]
    public void A_cell_with_no_layers_still_gets_a_quad_to_read()
    {
        // Not merely defensive. A cell with no LAND texture data renders through the fragment
        // shader's totalWeight fallback, which still reads quad 0; and a zero-quad geometry stream
        // is a different vertex shader entirely (the depth-only permutation, which has no pixel
        // shader at all). Sizing an unpainted cell to zero would pair a colour PSO with a layout
        // missing the input its shader declares — the one mismatch D3D12 rejects outright.
        Assert.Equal(1, TerrainBlendWeightPacking.QuadCountFor(0));
        Assert.Equal(1, TerrainBlendWeightPacking.QuadCountFor(-1));
    }

    [Fact]
    public void An_oversized_slot_count_cannot_ask_for_a_fifth_quad()
    {
        // There is no TEXCOORD7 weight attribute and no layout element for one, so a slot count that
        // somehow exceeded the ceiling has to saturate rather than index past the family.
        Assert.Equal(TerrainBlendWeightPacking.MaxQuadCount, TerrainBlendWeightPacking.QuadCountFor(17));
        Assert.Equal(TerrainBlendWeightPacking.MaxQuadCount, TerrainBlendWeightPacking.QuadCountFor(int.MaxValue));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 8)]
    [InlineData(2, 16)]
    [InlineData(3, 24)]
    [InlineData(4, 32)]
    public void Wire_bytes_are_eight_per_quad(int quads, int expectedBytes)
    {
        // 4 weights × 2 bytes. Pinned as literals rather than recomputed from the same constants the
        // production expression uses, so an edit to either side has something to disagree with.
        Assert.Equal(expectedBytes, TerrainBlendWeightPacking.BytesPerVertexFor(quads));
    }

    [Fact]
    public void The_common_cell_pays_a_quarter_of_what_the_fixed_stream_charged()
    {
        // The claim phase 3d is worth doing. A cell paints 2-6 land textures in practice; at 4 or
        // fewer that is one quad, against the 16-slot stream every cell used to carry.
        var typical = TerrainBlendWeightPacking.BytesPerVertexFor(TerrainBlendWeightPacking.QuadCountFor(4));

        Assert.Equal(8, typical);
        Assert.Equal(TerrainBlendWeightPacking.BytesPerVertex / 4, typical);
    }
}
