using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers <see cref="SkyDomeMesh" />: the procedural celestial-sphere geometry the dome sky renderer
///     centers on the camera. Verifiable without a GPU — counts, unit-length directions, lat-long UV
///     ranges, full nadir→zenith coverage, and finite values.
/// </summary>
public sealed class SkyDomeMeshTests
{
    [Theory]
    [InlineData(24, 48)]
    [InlineData(8, 12)]
    [InlineData(2, 3)]
    public void Generate_ProducesExpectedCounts(int rings, int segments)
    {
        var mesh = SkyDomeMesh.Generate(rings, segments);

        Assert.Equal((rings + 1) * (segments + 1), mesh.Vertices.Length);
        Assert.Equal(rings * segments * 6, mesh.Indices.Length);
    }

    [Fact]
    public void Generate_AllDirectionsUnitLength_AndFinite()
    {
        var mesh = SkyDomeMesh.Generate();

        foreach (var v in mesh.Vertices)
        {
            Assert.True(float.IsFinite(v.Direction.X) && float.IsFinite(v.Direction.Y) &&
                        float.IsFinite(v.Direction.Z));
            Assert.Equal(1f, v.Direction.Length(), 3);
            Assert.InRange(v.Uv.X, 0f, 1f);
            Assert.InRange(v.Uv.Y, 0f, 1f);
        }
    }

    [Fact]
    public void Generate_CoversNadirToZenith()
    {
        var mesh = SkyDomeMesh.Generate();

        var minZ = float.MaxValue;
        var maxZ = float.MinValue;
        foreach (var v in mesh.Vertices)
        {
            minZ = MathF.Min(minZ, v.Direction.Z);
            maxZ = MathF.Max(maxZ, v.Direction.Z);
        }

        // Full sphere: a vertex straight down (nadir, Z ≈ -1) and straight up (zenith, Z ≈ +1) so the
        // dome always covers the whole view from the camera at the centre.
        Assert.True(minZ < -0.99f, $"expected a nadir vertex (Z≈-1), got minZ={minZ}");
        Assert.True(maxZ > 0.99f, $"expected a zenith vertex (Z≈+1), got maxZ={maxZ}");
    }

    [Fact]
    public void Generate_IndicesInRange()
    {
        var mesh = SkyDomeMesh.Generate(10, 16);
        foreach (var idx in mesh.Indices)
        {
            Assert.InRange(idx, 0, mesh.Vertices.Length - 1);
        }
    }

    [Fact]
    public void Generate_InwardWinding_TrianglesFaceCentre()
    {
        // The camera sits at the sphere centre, so each triangle's geometric normal should point roughly
        // TOWARD the centre (opposite its outward position) — i.e. the dot of the face normal with the
        // outward direction at the face centroid is negative for inward winding.
        var mesh = SkyDomeMesh.Generate(12, 24);
        var inwardCount = 0;
        var total = 0;
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.Indices[i]].Direction;
            var b = mesh.Vertices[mesh.Indices[i + 1]].Direction;
            var c = mesh.Vertices[mesh.Indices[i + 2]].Direction;
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared() < 1e-10f)
            {
                continue; // degenerate pole triangle
            }

            var centroid = (a + b + c) / 3f;
            total++;
            if (Vector3.Dot(normal, centroid) < 0f)
            {
                inwardCount++;
            }
        }

        // The vast majority of non-degenerate triangles wind inward (a handful near the poles can be
        // degenerate and are skipped above).
        Assert.True(inwardCount >= total * 0.95, $"expected inward winding; {inwardCount}/{total} inward");
    }
}