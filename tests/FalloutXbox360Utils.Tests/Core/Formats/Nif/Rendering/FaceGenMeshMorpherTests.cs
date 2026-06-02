using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class FaceGenMeshMorpherTests
{
    private const float SamePosEps = 1e-4f;

    [Fact]
    public void WeldSeamNormals_TwoColocatedVerticesSameHemisphere_AveragesNormals()
    {
        // Two normals tilted off-axis but sharing the +Y hemisphere — a typical neck-ring
        // seam after EGM morphing where neighboring submeshes carry slightly different
        // normals for the same shared vertex position. The welder should average them.
        var positions = new float[] { 0f, 0f, 0f, 0f, 0f, 0f };
        var normals = new float[] { 0.1f, 0.9f, 0f, 0f, 0.9f, 0.1f };
        // Normalize originals so the comparison is meaningful.
        Normalize(normals, 0);
        Normalize(normals, 3);

        FaceGenMeshMorpher.WeldSeamNormals(positions, normals);

        // Both vertices receive the same welded normal.
        Assert.Equal(normals[0], normals[3], precision: 5);
        Assert.Equal(normals[1], normals[4], precision: 5);
        Assert.Equal(normals[2], normals[5], precision: 5);

        // Result is unit-length and stays in the +Y hemisphere.
        var len = MathF.Sqrt(normals[0] * normals[0] + normals[1] * normals[1] + normals[2] * normals[2]);
        Assert.Equal(1f, len, precision: 5);
        Assert.True(normals[1] > 0f, "Welded normal should remain in +Y hemisphere");
    }

    [Fact]
    public void RecalculateNormals_DegenerateAccumulation_FallsBackToAuthoredOriginal()
    {
        // Vertex 0 is shared by two triangles with opposite winding (same triangle, both faces).
        // Their face cross products cancel exactly, so the accumulated normal length at vertex 0
        // is ~zero. Before the fallback fix this left vertex 0 with a zero-length normal, which
        // produced bright spots / black holes after seam welding. The fallback should restore
        // the authored direction (+Z here) so downstream shading stays sane.
        var sub = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2, 0, 2, 1],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f]
        };

        FaceGenMeshMorpher.RecalculateNormals(sub);

        var v0Len = MathF.Sqrt(
            sub.Normals![0] * sub.Normals[0] +
            sub.Normals[1] * sub.Normals[1] +
            sub.Normals[2] * sub.Normals[2]);
        Assert.Equal(1f, v0Len, precision: 5);
        Assert.Equal(0f, sub.Normals[0], precision: 5);
        Assert.Equal(0f, sub.Normals[1], precision: 5);
        Assert.Equal(1f, sub.Normals[2], precision: 5);
    }

    private static void Normalize(float[] normals, int offset)
    {
        var x = normals[offset];
        var y = normals[offset + 1];
        var z = normals[offset + 2];
        var len = MathF.Sqrt(x * x + y * y + z * z);
        normals[offset] = x / len;
        normals[offset + 1] = y / len;
        normals[offset + 2] = z / len;
    }

    [Fact]
    public void WeldSeamNormals_OpposingHemisphereSeam_PreservesSplit()
    {
        // Two co-located vertices with opposite normals — a mouth-interior / face-exterior
        // seam where the engine intentionally keeps the normals split. Before the
        // hemisphere-aware weld, these averaged to ~zero and normalized to garbage,
        // producing the dark/light face splotches.
        var positions = new float[] { 0f, 0f, 0f, 0f, 0f, 0f };
        var normals = new float[] { 0f, 0f, 1f, 0f, 0f, -1f };

        FaceGenMeshMorpher.WeldSeamNormals(positions, normals);

        // Each vertex must retain a unit-length normal pointing in its original direction.
        var len0 = MathF.Sqrt(normals[0] * normals[0] + normals[1] * normals[1] + normals[2] * normals[2]);
        var len1 = MathF.Sqrt(normals[3] * normals[3] + normals[4] * normals[4] + normals[5] * normals[5]);
        Assert.Equal(1f, len0, precision: 5);
        Assert.Equal(1f, len1, precision: 5);
        Assert.Equal(1f, normals[2], precision: 5);
        Assert.Equal(-1f, normals[5], precision: 5);
    }

    [Fact]
    public void WeldSeamNormals_ThreeColocatedMixedHemispheres_PartitionsIntoTwoGroups()
    {
        // Three vertices at the same position: two outward-facing, one inward-facing.
        // Expected: the two outward verts weld together; the inward vert stays solo.
        var positions = new float[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
        var normals = new float[] {
            0f, 0f, 1f,   // outward A
            0.1f, 0f, 0.99f, // outward B (slightly off-axis)
            0f, 0f, -1f   // inward C
        };

        FaceGenMeshMorpher.WeldSeamNormals(positions, normals);

        // A and B should now share the same welded normal (outward, +Z hemisphere).
        Assert.Equal(normals[0], normals[3], precision: 5);
        Assert.Equal(normals[1], normals[4], precision: 5);
        Assert.Equal(normals[2], normals[5], precision: 5);
        Assert.True(normals[2] > 0f, "Welded A/B normal should remain in +Z hemisphere");

        // C must remain in -Z hemisphere — it was not merged with A/B.
        Assert.Equal(-1f, normals[8], precision: 5);
    }

    [Fact]
    public void WeldSeamNormals_NonColocatedVertices_LeftUnchanged()
    {
        var positions = new float[] { 0f, 0f, 0f, 10f, 10f, 10f };
        var normals = new float[] { 1f, 0f, 0f, 0f, 1f, 0f };
        var expected = (float[])normals.Clone();

        FaceGenMeshMorpher.WeldSeamNormals(positions, normals);

        Assert.Equal(expected, normals);
    }

    [Fact]
    public void WeldSeamNormals_ColocatedVerticesAcrossBucketBoundary_StillWeld()
    {
        // Positions 0.0001 apart but straddling a 0.01 bucket boundary; both normals
        // share the same hemisphere so the welder must find them via the neighbor sweep.
        var positions = new float[] { 0.00995f, 0f, 0f, 0.01005f, 0f, 0f };
        var normals = new float[] { 0.7071f, 0f, 0.7071f, 0f, 0.7071f, 0.7071f };

        FaceGenMeshMorpher.WeldSeamNormals(positions, normals);

        Assert.Equal(normals[0], normals[3], precision: 5);
        Assert.Equal(normals[1], normals[4], precision: 5);
        Assert.Equal(normals[2], normals[5], precision: 5);
    }
}
