using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Pins D3D12's 64 KiB committed-buffer rounding. Expected values are computed by hand below,
///     never by calling the method under test — an agreement check against the same arithmetic would
///     pass no matter what the rule became.
/// </summary>
public sealed class GpuResourceFootprintTests
{
    private const long Kib64 = 65_536;

    [Theory]
    [InlineData(0, 0)] // nothing allocated costs nothing
    [InlineData(1, 65_536)] // any non-zero request occupies a whole 64 KiB page
    [InlineData(65_536, 65_536)] // exact multiples are not rounded up a page
    [InlineData(65_537, 131_072)]
    [InlineData(131_072, 131_072)]
    public void CommittedBufferBytes_rounds_up_to_the_placement_alignment(long requested, long expected) =>
        Assert.Equal(expected, GpuResourceFootprint.CommittedBufferBytes(requested));

    [Fact]
    public void A_negative_request_is_treated_as_zero()
    {
        // Buffer widths come from unsigned D3D12 descriptions, but the cast site is long; a negative
        // must not round to a huge positive and poison a byte budget.
        Assert.Equal(0, GpuResourceFootprint.CommittedBufferBytes(-1));
    }

    [Fact]
    public void Each_buffer_of_a_pair_is_rounded_independently()
    {
        // Two 33 KiB buffers cost two pages, not one — summing first and rounding once would
        // understate a per-cell terrain charge by half.
        const long thirtyThreeKib = 33 * 1024;
        Assert.Equal(2 * Kib64, GpuResourceFootprint.CommittedBufferBytes(thirtyThreeKib, thirtyThreeKib));
    }

    [Fact]
    public void Committed_buffers_waste_43_percent_of_a_33_grid_terrain_cell()
    {
        // Why the terrain arena exists. The Fallout/Oblivion/Skyrim case, computed from the formats
        // rather than from the code: 33x33 = 1089 verts; vertex stream 72 B/vertex, blend-weight
        // stream 4 float4s = 64 B/vertex. This is the cost the arena AVOIDS — see
        // Arena_suballocation_is_dramatically_cheaper_than_two_committed_buffers.
        const long vertices = 33 * 33;
        const long vertexBytes = vertices * 72; // 78,408 -> 2 pages
        const long blendBytes = vertices * 64; // 69,696 -> 2 pages
        Assert.Equal(78_408, vertexBytes);
        Assert.Equal(69_696, blendBytes);

        var charged = GpuResourceFootprint.CommittedBufferBytes(vertexBytes, blendBytes);

        Assert.Equal(4 * Kib64, charged); // 262,144
        var wasted = charged - (vertexBytes + blendBytes);
        Assert.Equal(114_040, wasted);
        Assert.True(wasted > charged * 0.43, $"expected >43% padding, got {wasted / (double)charged:P1}");
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 16)] // padded to one 16-byte region
    [InlineData(16, 16, 32)] // both already aligned
    [InlineData(17, 0, 32)]
    [InlineData(78_408, 69_696, 148_112)] // 33-grid: 78,408 is 16-aligned; total padded to 148,112
    public void ArenaSubAllocationBytes_pads_the_first_stream_then_the_whole_range(
        long first, long second, long expected) =>
        Assert.Equal(expected, GpuResourceFootprint.ArenaSubAllocationBytes(first, second));

    [Fact]
    public void ArenaSubAllocationBytes_treats_negative_inputs_as_zero() =>
        Assert.Equal(0, GpuResourceFootprint.ArenaSubAllocationBytes(-1, -1));

    [Fact]
    public void Arena_suballocation_is_dramatically_cheaper_than_two_committed_buffers()
    {
        // The whole point of Phase 2, and the reason the residency policy MUST predict with the
        // arena rule: at the 33 grid the arena charge is ~56% of the committed-buffer charge, so a
        // predictor still using the committed rule would keep the planned budget permanently above
        // real residency and the byte bound would never evict anything.
        const long vertices = 33 * 33;
        var committed = GpuResourceFootprint.CommittedBufferBytes(vertices * 72, vertices * 64);
        var arena = GpuResourceFootprint.ArenaSubAllocationBytes(vertices * 72, vertices * 64);

        Assert.Equal(262_144, committed);
        Assert.Equal(148_112, arena);
        Assert.True(arena < committed * 0.6, $"arena {arena} vs committed {committed}");

        // And the arena's padding is a rounding error rather than a design cost.
        Assert.Equal(8, arena - (vertices * 72 + vertices * 64));
    }

    [Fact]
    public void A_129_grid_terrain_cell_wastes_far_less_proportionally()
    {
        // Fallout 76 / Starfield. Same absolute rule, but ~94 KiB against a 2.16 MiB payload — which
        // is why the alignment fix matters most to the SMALL-grid games even though the raw byte
        // totals are dominated by the large-grid ones.
        const long vertices = 129 * 129;
        const long vertexBytes = vertices * 72; // 1,198,152
        const long blendBytes = vertices * 64; // 1,065,024

        var charged = GpuResourceFootprint.CommittedBufferBytes(vertexBytes, blendBytes);
        var wasted = charged - (vertexBytes + blendBytes);

        Assert.Equal((19 + 17) * Kib64, charged); // 1,245,184 + 1,114,112
        Assert.Equal(96_120, wasted);
        Assert.True(wasted < charged * 0.05, $"expected <5% padding, got {wasted / (double)charged:P1}");
    }
}
