using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers the walk-mode ground-snap math (3D-4): <see cref="RayTriangleIntersector" /> and
///     <see cref="CollisionMesh" />. The 3D viewer transforms a world-space down-ray into mesh-local
///     space, raycasts here, and maps the hit back to world to read its Z. The control-side transform
///     wiring (<c>WorldView3DControl.TryRaycastReferenceGround</c>) is GUI-gated; this exercises the
///     non-gated geometry kernel it depends on, including a pre-transformed rotated-mesh ray.
/// </summary>
public sealed class GroundRaycastTests
{
    private const float Tol = 1e-3f;

    [Fact]
    public void Intersect_RayThroughTriangleCenter_ReturnsDistance()
    {
        // Triangle in the z=10 plane; ray straight down from above its centroid.
        var v0 = new Vector3(0, 0, 10);
        var v1 = new Vector3(10, 0, 10);
        var v2 = new Vector3(0, 10, 10);
        var origin = new Vector3(2, 2, 50);

        var hit = RayTriangleIntersector.Intersect(origin, new Vector3(0, 0, -1), v0, v1, v2, out var t);

        Assert.True(hit);
        Assert.Equal(40f, t, Tol); // 50 - 10
    }

    [Fact]
    public void Intersect_RayOutsideTriangle_Misses()
    {
        var v0 = new Vector3(0, 0, 10);
        var v1 = new Vector3(10, 0, 10);
        var v2 = new Vector3(0, 10, 10);
        // (9, 9) is outside the lower-left triangle (u + v > 1).
        var origin = new Vector3(9, 9, 50);

        Assert.False(RayTriangleIntersector.Intersect(origin, new Vector3(0, 0, -1), v0, v1, v2, out _));
    }

    [Fact]
    public void Intersect_TriangleBehindOrigin_Misses()
    {
        var v0 = new Vector3(0, 0, 10);
        var v1 = new Vector3(10, 0, 10);
        var v2 = new Vector3(0, 10, 10);
        // Ray points UP, triangle is below the origin → behind the ray → miss.
        var origin = new Vector3(2, 2, 50);

        Assert.False(RayTriangleIntersector.Intersect(origin, new Vector3(0, 0, 1), v0, v1, v2, out _));
    }

    [Fact]
    public void Intersect_RayParallelToTriangle_Misses()
    {
        var v0 = new Vector3(0, 0, 10);
        var v1 = new Vector3(10, 0, 10);
        var v2 = new Vector3(0, 10, 10);
        // Horizontal ray in a different plane never meets the z=10 triangle.
        var origin = new Vector3(2, 2, 50);

        Assert.False(RayTriangleIntersector.Intersect(origin, new Vector3(1, 0, 0), v0, v1, v2, out _));
    }

    [Fact]
    public void Intersect_BackFace_StillHits()
    {
        // Reversed winding (normal points away from the ray) must still register — floors are authored
        // with either winding and the camera must land on them regardless.
        var v0 = new Vector3(0, 0, 10);
        var v1 = new Vector3(0, 10, 10);
        var v2 = new Vector3(10, 0, 10);
        var origin = new Vector3(2, 2, 50);

        Assert.True(RayTriangleIntersector.Intersect(origin, new Vector3(0, 0, -1), v0, v1, v2, out var t));
        Assert.Equal(40f, t, Tol);
    }

    [Fact]
    public void CollisionMesh_DownRayOntoQuad_HitsTop()
    {
        var mesh = BuildHorizontalQuad(100f, 50f);
        var hit = mesh.RaycastNearest(new Vector3(0, 0, 300), new Vector3(0, 0, -1), out var t);

        Assert.True(hit);
        Assert.Equal(200f, t, Tol); // 300 - 100
    }

    [Fact]
    public void CollisionMesh_StackedQuads_ReturnsNearest()
    {
        // Two floors; a down-ray from above should hit the HIGHER one first (smallest t).
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        AppendQuad(positions, triangles, 100f, 50f);
        AppendQuad(positions, triangles, 200f, 50f);
        var mesh = new CollisionMesh(positions.ToArray(), triangles.ToArray());

        Assert.True(mesh.RaycastNearest(new Vector3(0, 0, 500), new Vector3(0, 0, -1), out var t));
        Assert.Equal(300f, t, Tol); // 500 - 200 (the upper quad)
    }

    [Fact]
    public void CollisionMesh_RayOutsideFootprint_MissesViaAabb()
    {
        var mesh = BuildHorizontalQuad(100f, 50f);
        // X=500 is well outside the ±50 footprint → AABB slab rejects before any triangle test.
        Assert.False(mesh.RaycastNearest(new Vector3(500, 0, 300), new Vector3(0, 0, -1), out _));
    }

    [Fact]
    public void CollisionMesh_RotatedPlacement_HitMapsBackToWorldZ()
    {
        // A quad authored at local z=0, placed by a 45°-about-X tilt + translation (a ramp). A naive
        // axis-aligned box top would be wrong; the local-ray path must recover the true world hit.
        var mesh = BuildHorizontalQuad(0f, 100f);
        var world = PlacedReferenceTransform.ComposeWorldMatrix(
            1000f, 2000f, 300f, MathF.PI / 4f, 0f, 0f, 1f);
        Assert.True(Matrix4x4.Invert(world, out var inv));

        // World down-ray straight above the placed origin.
        var worldOrigin = new Vector3(1000f, 2000f, 900f);
        var worldDir = new Vector3(0, 0, -1);
        var localOrigin = Vector3.Transform(worldOrigin, inv);
        var localDir = Vector3.TransformNormal(worldDir, inv);

        Assert.True(mesh.RaycastNearest(localOrigin, localDir, out var tLocal));
        var worldHit = Vector3.Transform(localOrigin + localDir * tLocal, world);

        // Above the placement origin the tilted ramp passes through the placement point exactly, so
        // the down-ray meets it at world (1000, 2000, 300) — not the box top a footprint test gives.
        Assert.Equal(1000f, worldHit.X, 1e-2f);
        Assert.Equal(2000f, worldHit.Y, 1e-2f);
        Assert.Equal(300f, worldHit.Z, 1e-2f);
    }

    [Fact]
    public void CollisionMesh_GapInVisualMesh_BridgedByGaplessHavokQuad()
    {
        // The bug this fixes: a plank bridge's VISUAL mesh has gaps between planks, so a walk-mode
        // down-ray at a gap falls through. The HAVOK collision mesh is gapless and bridges it.
        // Two "planks" with a gap across x∈[-5,5]; a down-ray at x=0 misses the visual soup …
        var visual = new List<Vector3>();
        var visualTris = new List<int>();
        AppendQuadXSpan(visual, visualTris, -50f, -5f, 100f);
        AppendQuadXSpan(visual, visualTris, 5f, 50f, 100f);
        var visualMesh = new CollisionMesh(visual.ToArray(), visualTris.ToArray());
        Assert.False(visualMesh.RaycastNearest(new Vector3(0, 0, 300), new Vector3(0, 0, -1), out _));

        // … but the gapless Havok quad spanning the whole bridge is hit at the expected Z.
        var havok = new List<Vector3>();
        var havokTris = new List<int>();
        AppendQuadXSpan(havok, havokTris, -50f, 50f, 100f);
        var havokMesh = new CollisionMesh(havok.ToArray(), havokTris.ToArray());
        Assert.True(havokMesh.RaycastNearest(new Vector3(0, 0, 300), new Vector3(0, 0, -1), out var t));
        Assert.Equal(200f, t, Tol); // 300 - 100
    }

    private static void AppendQuadXSpan(List<Vector3> positions, List<int> triangles, float x0, float x1, float z)
    {
        var b = positions.Count;
        positions.Add(new Vector3(x0, -50f, z));
        positions.Add(new Vector3(x1, -50f, z));
        positions.Add(new Vector3(x1, 50f, z));
        positions.Add(new Vector3(x0, 50f, z));
        triangles.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
    }

    private static CollisionMesh BuildHorizontalQuad(float z, float halfSize)
    {
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        AppendQuad(positions, triangles, z, halfSize);
        return new CollisionMesh(positions.ToArray(), triangles.ToArray());
    }

    private static void AppendQuad(List<Vector3> positions, List<int> triangles, float z, float halfSize)
    {
        var b = positions.Count;
        positions.Add(new Vector3(-halfSize, -halfSize, z));
        positions.Add(new Vector3(halfSize, -halfSize, z));
        positions.Add(new Vector3(halfSize, halfSize, z));
        positions.Add(new Vector3(-halfSize, halfSize, z));
        triangles.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
    }
}