using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Pins the <c>R8G8B8A8_UNORM</c> vertex-colour packing that narrowed <c>GpuVertex</c> from 72 to
///     60 bytes. The saving lands in the geometry arena, which is an UPLOAD heap — system RAM, not
///     VRAM — and on Fallout 76 that arena is a large part of the ~8 GB of unmanaged memory that
///     still stops a capture from settling.
///     <para>
///         The change is only safe because it is <b>bit-exact</b>: every source value is a NIF byte
///         divided by 255, so packing it back must recover the identical byte. That is the claim
///         under test — not "close enough", exactly equal, for all 256 values of every channel.
///     </para>
/// </summary>
public sealed class GpuVertexColorPackingTests
{
    [Fact]
    public void Every_byte_value_round_trips_exactly_through_the_packed_format()
    {
        // The whole justification in one assertion. A truncating implementation fails this at the
        // first channel; a rounding one passes for all 256.
        for (var b = 0; b <= 255; b++)
        {
            var asFloat = b / 255f;
            var packed = GpuMeshUploader.PackColor(new Vector4(asFloat, asFloat, asFloat, asFloat));
            var unpacked = GpuMeshUploader.UnpackColor(packed);

            Assert.Equal(asFloat, unpacked.X);
            Assert.Equal(asFloat, unpacked.Y);
            Assert.Equal(asFloat, unpacked.Z);
            Assert.Equal(asFloat, unpacked.W);
        }
    }

    [Fact]
    public void Channels_land_in_R_G_B_A_memory_order()
    {
        // DXGI R8G8B8A8_UNORM means byte 0 = R. Get this backwards and every mesh renders with its
        // red and blue swapped — which looks deliberate enough to survive a casual glance.
        var packed = GpuMeshUploader.PackColor(new Vector4(1f, 2f / 255f, 3f / 255f, 4f / 255f));
        var bytes = BitConverter.GetBytes(packed);

        Assert.Equal(255, bytes[0]);
        Assert.Equal(2, bytes[1]);
        Assert.Equal(3, bytes[2]);
        Assert.Equal(4, bytes[3]);
    }

    [Fact]
    public void Out_of_range_values_clamp_instead_of_wrapping()
    {
        // Nothing should feed these today, but a wrapped channel would be a silent, wildly wrong
        // colour rather than a clipped one.
        var high = GpuMeshUploader.UnpackColor(GpuMeshUploader.PackColor(new Vector4(2f, 2f, 2f, 2f)));
        var low = GpuMeshUploader.UnpackColor(GpuMeshUploader.PackColor(new Vector4(-1f, -1f, -1f, -1f)));

        Assert.Equal(Vector4.One, high);
        Assert.Equal(Vector4.Zero, low);
    }

    [Fact]
    public void The_vertex_is_sixty_bytes_with_colour_at_offset_thirty_two()
    {
        // The input layout hard-codes these offsets (GpuMeshBufferFactory12.InputElements). If the
        // struct and the layout ever disagree, the GPU reads each attribute from the wrong place and
        // the failure is visual, not an exception — so pin both halves of the contract here.
        Assert.Equal(60, Marshal.SizeOf<GpuMeshUploader.GpuVertex>());
        Assert.Equal(32, (int)Marshal.OffsetOf<GpuMeshUploader.GpuVertex>(
            nameof(GpuMeshUploader.GpuVertex.VertexColorRgba)));
        Assert.Equal(36, (int)Marshal.OffsetOf<GpuMeshUploader.GpuVertex>(
            nameof(GpuMeshUploader.GpuVertex.Tangent)));
        Assert.Equal(48, (int)Marshal.OffsetOf<GpuMeshUploader.GpuVertex>(
            nameof(GpuMeshUploader.GpuVertex.Bitangent)));
    }

    [Fact]
    public void The_float4_facade_agrees_with_the_packed_field()
    {
        // ~4 call sites still assign VertexColor as a Vector4; they must keep working unchanged.
        var vertex = default(GpuMeshUploader.GpuVertex);
        var color = new Vector4(10f / 255f, 20f / 255f, 30f / 255f, 40f / 255f);

        vertex.VertexColor = color;

        Assert.Equal(color, vertex.VertexColor);
        Assert.Equal(GpuMeshUploader.PackColor(color), vertex.VertexColorRgba);
    }
}
