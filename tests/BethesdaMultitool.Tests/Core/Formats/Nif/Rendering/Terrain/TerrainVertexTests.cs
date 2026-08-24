using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Pins the terrain vertex's wire layout. The struct is memcpy'd into an upload buffer and read
///     back by a hand-written D3D12 input layout, so its size and field offsets are an ABI: a
///     mismatch does not fail to bind, it decodes garbage geometry.
/// </summary>
public sealed class TerrainVertexTests
{
    [Fact]
    public void The_declared_size_matches_the_actual_managed_layout()
    {
        // MemoryMarshal.AsBytes uses the MANAGED size, the input layout uses the declared constant,
        // and the residency policy predicts from the same constant. All three have to agree.
        Assert.Equal(12, TerrainVertex.SizeInBytes);
        Assert.Equal(TerrainVertex.SizeInBytes, Unsafe.SizeOf<TerrainVertex>());
        Assert.Equal(TerrainVertex.SizeInBytes, Marshal.SizeOf<TerrainVertex>());
    }

    [Fact]
    public void Field_offsets_match_the_input_layout()
    {
        // Hard-coded, matching TerrainVertexLayout.Elements: height at 0, octahedral normal at 4,
        // packed colour at 8. Read from the type rather than asserted against a second copy of the
        // same arithmetic.
        Assert.Equal(0, Marshal.OffsetOf<TerrainVertex>(nameof(TerrainVertex.Height)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<TerrainVertex>(nameof(TerrainVertex.NormalOctX)).ToInt32());
        Assert.Equal(6, Marshal.OffsetOf<TerrainVertex>(nameof(TerrainVertex.NormalOctY)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<TerrainVertex>(nameof(TerrainVertex.Color)).ToInt32());
    }

    [Fact]
    public void It_is_dramatically_smaller_than_the_shared_mesh_vertex_it_replaced()
    {
        // The point of the exercise. GpuVertex was 72 bytes: pos 12 + normal 12 + uv 8 + colour 16
        // + tangent 12 + bitangent 12, of which uv/tangent/bitangent were never read by either
        // terrain shader.
        Assert.Equal(72, Unsafe.SizeOf<BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.GpuMeshUploader.GpuVertex>());
        Assert.True(TerrainVertex.SizeInBytes * 5 < 72, "expected better than a 5x reduction");
    }

    [Theory]
    [InlineData(0, 0, 0, 0xFF000000)]
    [InlineData(255, 255, 255, 0xFFFFFFFF)]
    [InlineData(255, 0, 0, 0xFF0000FF)] // R occupies the LOW byte — the IA reads byte 0 as red
    [InlineData(0, 255, 0, 0xFF00FF00)]
    [InlineData(0, 0, 255, 0xFFFF0000)]
    [InlineData(18, 52, 86, 0xFF563412)]
    public void PackColor_puts_red_in_the_low_byte(byte r, byte g, byte b, uint expected) =>
        Assert.Equal(expected, TerrainVertex.PackColor(r, g, b));

    [Fact]
    public void Every_byte_triple_round_trips_exactly()
    {
        // The colour narrowing's whole justification: VCLR is bytes, so R8G8B8A8_UNORM stores the
        // source values themselves. If this were lossy the narrowing would be a real trade-off
        // rather than the removal of 12 bytes of padding-with-extra-steps.
        for (var v = 0; v <= 255; v++)
        {
            var packed = TerrainVertex.PackColor((byte)v, (byte)(255 - v), (byte)((v * 7) & 0xFF));
            var unpacked = TerrainVertex.UnpackColor(packed);

            Assert.Equal(v / 255f, unpacked.X);
            Assert.Equal((255 - v) / 255f, unpacked.Y);
            Assert.Equal(((v * 7) & 0xFF) / 255f, unpacked.Z);
            Assert.Equal(1f, unpacked.W);
        }
    }

    [Fact]
    public void The_untinted_default_is_opaque_white()
    {
        // TerrainMeshBuilder substitutes this for a cell with no VCLR; anything else would tint
        // every un-authored cell in the game.
        Assert.Equal(Vector4.One, TerrainVertex.UnpackColor(0xFFFFFFFF));
        Assert.Equal(0xFFFFFFFF, TerrainVertex.PackColor(Vector4.One));
    }

    [Fact]
    public void The_float_colour_overload_clamps_rather_than_wrapping()
    {
        // An out-of-range component must not wrap around to a wildly different colour.
        Assert.Equal(0xFFFFFFFF, TerrainVertex.PackColor(new Vector4(2f, 5f, 1.001f, 1f)));
        Assert.Equal(0x000000FFu, TerrainVertex.PackColor(new Vector4(1f, -1f, -0.5f, -3f)));
    }

    [Fact]
    public void The_constructor_stores_the_packed_normal_and_reports_it_back_decoded()
    {
        var normal = Vector3.Normalize(new Vector3(0.3f, -0.4f, 0.8f));

        var vertex = new TerrainVertex(3f, normal, TerrainVertex.PackColor(10, 20, 30));

        Assert.Equal(3f, vertex.Height);
        Assert.Equal(new Vector4(10 / 255f, 20 / 255f, 30 / 255f, 1f), vertex.VertexColor);
        var degrees = TerrainNormalPacking.AngleDegrees(normal, vertex.Normal);
        Assert.True(degrees < 0.01, $"normal drifted {degrees:F5}°");
    }

    [Fact]
    public void An_array_of_vertices_is_contiguous_with_no_inter_element_padding()
    {
        // MemoryMarshal.AsBytes over the scratch array is what reaches the staging buffer, and the
        // input layout assumes a tight stride. A hidden trailing pad would shear every cell.
        var vertices = new TerrainVertex[4];
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i] = new TerrainVertex(i, Vector3.UnitZ, TerrainVertex.PackColor((byte)i, 0, 0));
        }

        var bytes = MemoryMarshal.AsBytes<TerrainVertex>(vertices);

        Assert.Equal(4 * TerrainVertex.SizeInBytes, bytes.Length);
        // Third vertex's packed colour sits at 2*12 + 8 = 32, with red (= 2) in the low byte.
        Assert.Equal(2, bytes[32]);
    }
}
