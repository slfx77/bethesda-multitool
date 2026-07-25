using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Unit coverage for the pure upload-time packing self-check. Grass renders giant garbage
///     triangles when a submesh's indices reach outside its own vertex window (BaseVertexLocation is
///     always 0), so the out-of-window-index case is the load-bearing assertion here.
/// </summary>
public sealed class MeshPackingValidator12Tests
{
    private const uint Stride = 48; // representative GpuVertex stride

    [Fact]
    public void ValidWindow_ReturnsNull()
    {
        ushort[] indices = [0, 1, 2, 2, 1, 3];
        var result = MeshPackingValidator12.ValidateSubmeshWindow(
            0,
            4,
            indices,
            0,
            4 * Stride,
            0,
            indices.Length * sizeof(ushort),
            Stride,
            4 * Stride,
            (uint)(indices.Length * sizeof(ushort)));

        Assert.Null(result);
    }

    [Fact]
    public void IndexOutsideVertexWindow_IsDetected()
    {
        // 4 vertices in this window but an index of 7 → reads a neighbor submesh's floats as a
        // position with BaseVertexLocation 0. This is the grass-shard mechanism.
        ushort[] indices = [0, 1, 7];
        var result = MeshPackingValidator12.ValidateSubmeshWindow(
            2,
            4,
            indices,
            4 * Stride,
            4 * Stride,
            0,
            indices.Length * sizeof(ushort),
            Stride,
            8 * Stride,
            (uint)(indices.Length * sizeof(ushort)));

        Assert.NotNull(result);
        Assert.Contains("index[2]", result);
    }

    [Fact]
    public void VertexWindowPastAllocation_IsDetected()
    {
        ushort[] indices = [0, 1, 2];
        var result = MeshPackingValidator12.ValidateSubmeshWindow(
            1,
            4,
            indices,
            6 * Stride, // window [6,10) but allocation only holds 8 vertices
            4 * Stride,
            0,
            indices.Length * sizeof(ushort),
            Stride,
            8 * Stride,
            (uint)(indices.Length * sizeof(ushort)));

        Assert.NotNull(result);
        Assert.Contains("vertex window", result);
    }

    [Fact]
    public void SizeStrideMismatch_IsDetected()
    {
        ushort[] indices = [0, 1, 2];
        var result = MeshPackingValidator12.ValidateSubmeshWindow(
            0,
            4,
            indices,
            0,
            4 * Stride + 1, // wrong: not vertexCount × stride
            0,
            indices.Length * sizeof(ushort),
            Stride,
            4 * Stride + 1,
            (uint)(indices.Length * sizeof(ushort)));

        Assert.NotNull(result);
        Assert.Contains("stride", result);
    }
}