using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Guards Session C's authored-water path. FNV WATER000 transforms source vertices directly;
///     replacing them with their AABB changes rotated rectangles, slopes, and non-rectangular pools.
/// </summary>
public sealed class NifWaterGeometryTests
{
    private static readonly float[] ExpectedSlopedVertexHeights = [11f, 12f, 17f, 21f];

    [Fact]
    public void Transform_preserves_rotation_nonuniform_scale_and_index_winding()
    {
        var positions = new[]
        {
            new Vector3(-1f, -1f, 0f),
            new Vector3(1f, -1f, 0f),
            new Vector3(-1f, 1f, 0f),
            new Vector3(1f, 1f, 0f)
        };
        ushort[] indices = [0, 1, 2, 2, 1, 3];
        Assert.True(NifWaterGeometry.TryCreate(positions, indices, out var local));

        var world = Matrix4x4.CreateScale(3f, 2f, 1f)
                    * Matrix4x4.CreateRotationZ(MathF.PI / 2f)
                    * Matrix4x4.CreateTranslation(10f, 20f, 30f);
        var placed = Assert.IsType<NifWaterGeometry>(local!.Transform(world));

        VectorAssert.Equal(new Vector3(12f, 17f, 30f), placed.Positions.Span[0], 1e-4f);
        VectorAssert.Equal(new Vector3(12f, 23f, 30f), placed.Positions.Span[1], 1e-4f);
        VectorAssert.Equal(new Vector3(8f, 17f, 30f), placed.Positions.Span[2], 1e-4f);
        Assert.Equal(indices, placed.Indices.ToArray());
        VectorAssert.Equal(new Vector3(8f, 17f, 30f), placed.BoundsMin, 1e-4f);
        VectorAssert.Equal(new Vector3(12f, 23f, 30f), placed.BoundsMax, 1e-4f);
    }

    [Fact]
    public void Transform_retains_each_sloped_vertex_height_instead_of_one_plane_height()
    {
        Vector3[] positions =
        [
            new(0f, 0f, 1f),
            new(4f, 0f, 2f),
            new(0f, 5f, 7f),
            new(4f, 5f, 11f)
        ];
        Assert.True(NifWaterGeometry.TryCreate(positions, [0, 1, 2, 2, 1, 3], out var local));

        var placed = Assert.IsType<NifWaterGeometry>(local!.Transform(Matrix4x4.CreateTranslation(2f, 3f, 10f)));

        Assert.Equal(ExpectedSlopedVertexHeights, placed.Positions.Span.ToArray().Select(static p => p.Z));
        Assert.Equal(11f, placed.BoundsMin.Z);
        Assert.Equal(21f, placed.BoundsMax.Z);
    }

    [Fact]
    public void Circular_fan_retains_authored_outline_and_triangle_topology()
    {
        Vector3[] positions =
        [
            new(0f, 0f, 3f),
            new(2f, 0f, 3f),
            new(0f, 2f, 3f),
            new(-2f, 0f, 3f),
            new(0f, -2f, 3f)
        ];
        ushort[] indices = [0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1];
        Assert.True(NifWaterGeometry.TryCreate(positions, indices, out var geometry));

        Assert.Equal(5, geometry!.Positions.Length);
        Assert.Equal(4, geometry.TriangleCount);
        Assert.Equal(indices, geometry.Indices.ToArray());
        Assert.DoesNotContain(new Vector3(2f, 2f, 3f), geometry.Positions.ToArray());
        VectorAssert.Equal(new Vector3(-2f, -2f, 3f), geometry.BoundsMin, 1e-4f);
        VectorAssert.Equal(new Vector3(2f, 2f, 3f), geometry.BoundsMax, 1e-4f);
    }

    [Fact]
    public void Triangle_packet_copies_source_index_order_and_winding_exactly()
    {
        Vector3[] positions =
        [
            new(0f, 0f, 0f),
            new(2f, 0f, 0f),
            new(0f, 3f, 1f),
            new(2f, 4f, 2f)
        ];
        // Deliberately non-sequential index order: the packet must not canonicalize either triangle.
        Assert.True(NifWaterGeometry.TryCreate(positions, [2, 0, 1, 1, 0, 3], out var geometry));

        var packet = geometry!.GetTrianglePacket(0);

        Assert.Equal(positions[2], packet.Vertex0);
        Assert.Equal(positions[0], packet.Vertex1);
        Assert.Equal(positions[1], packet.Vertex2);
        Assert.Equal(positions[1], packet.Vertex3);
        Assert.Equal(positions[0], packet.Vertex4);
        Assert.Equal(positions[3], packet.Vertex5);
        Assert.Equal(
            Vector3.Cross(positions[0] - positions[2], positions[1] - positions[2]),
            Vector3.Cross(packet.Vertex1 - packet.Vertex0, packet.Vertex2 - packet.Vertex0));
    }

    [Fact]
    public void Odd_triangle_packet_uses_a_degenerate_second_triangle()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        Assert.True(NifWaterGeometry.TryCreate(positions, [0, 1, 2], out var geometry));

        var packet = geometry!.GetTrianglePacket(0);

        Assert.Equal(1, geometry.TrianglePacketCount);
        Assert.Equal(packet.Vertex2, packet.Vertex3);
        Assert.Equal(packet.Vertex2, packet.Vertex4);
        Assert.Equal(packet.Vertex2, packet.Vertex5);
    }

    [Fact]
    public void TryCreate_rejects_out_of_range_indices_and_incomplete_triangles()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];

        Assert.False(NifWaterGeometry.TryCreate(positions, [0, 1, 3], out var invalidIndex));
        Assert.Null(invalidIndex);
        Assert.False(NifWaterGeometry.TryCreate(positions, [0, 1, 2, 0], out var incomplete));
        Assert.Null(incomplete);
    }

    [Fact]
    public void Bounds_drive_conservative_xy_square_culling_without_rectangularizing_geometry()
    {
        Vector3[] positions = [new(10f, 20f, 1f), new(14f, 20f, 4f), new(12f, 26f, 2f)];
        Assert.True(NifWaterGeometry.TryCreate(positions, [0, 1, 2], out var geometry));

        VectorAssert.Equal(new Vector3(10f, 20f, 1f), geometry!.BoundsMin, 1e-4f);
        VectorAssert.Equal(new Vector3(14f, 26f, 4f), geometry.BoundsMax, 1e-4f);
        Assert.True(geometry.IntersectsXY(new Vector2(8f, 23f), 2f));
        Assert.False(geometry.IntersectsXY(new Vector2(7.9f, 23f), 2f));
        // VisibilityCylinder is a Chebyshev square: this corner is inside even though its Euclidean
        // distance from the geometry AABB is sqrt(8), outside an inscribed radius-2 circle.
        Assert.True(geometry.IntersectsXY(new Vector2(8f, 18f), 2f));
    }

    [Fact]
    public void Point_height_probe_uses_the_authored_sloped_triangle_not_its_aabb_plane()
    {
        Vector3[] positions =
        [
            new(0f, 0f, 10f),
            new(10f, 0f, 20f),
            new(0f, 10f, 30f),
        ];
        Assert.True(NifWaterGeometry.TryCreate(positions, [0, 1, 2], out var geometry));

        Assert.True(geometry!.TryGetHeightAtXY(2f, 3f, out var height));
        Assert.Equal(18f, height, 4);
        Assert.False(
            geometry.TryGetHeightAtXY(9f, 9f, out _),
            "an XY point inside the AABB but outside the authored triangle must not acquire water");
    }

    [Fact]
    public void Point_height_probe_selects_the_highest_overlapping_authored_surface()
    {
        Vector3[] positions =
        [
            new(0f, 0f, 5f), new(4f, 0f, 5f), new(0f, 4f, 5f),
            new(0f, 0f, 12f), new(4f, 0f, 12f), new(0f, 4f, 12f),
        ];
        Assert.True(NifWaterGeometry.TryCreate(positions, [0, 1, 2, 3, 4, 5], out var geometry));

        Assert.True(geometry!.TryGetHeightAtXY(1f, 1f, out var height));
        Assert.Equal(12f, height);
    }
}
