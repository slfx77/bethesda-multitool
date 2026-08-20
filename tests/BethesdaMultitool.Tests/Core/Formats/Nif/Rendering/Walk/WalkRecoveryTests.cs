using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     Walk-mode collision recovery: the three defects reported together — clipping through cave
///     walls/ceilings, falling forever after clipping through, and standing on SpeedTree leaf cards.
/// </summary>
public sealed class WalkRecoveryTests
{
    // Eye at 112 above the feet, step height 48 → the horizontal sweep's body interval.
    private const float BodyMinZ = 48f;
    private const float BodyMaxZ = 120f;
    private const float Radius = 24f;

    [Fact]
    public void SweptStep_CannotTunnelThroughAThinWall()
    {
        // A 2-unit-thick wall across x = 0. A single frame at scroll-boosted sprint speed asks for a
        // 1000-unit step — far longer than the wall is thick — so a discrete end-point test would land
        // cleanly on the far side.
        var wall = BuildThinWallMesh(2f, 400f, 400f);
        var instance = WalkCollisionInstance.FromMesh(wall, Matrix4x4.Identity);

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-500f, 0f), new Vector2(1000f, 0f), BodyMinZ, BodyMaxZ, Radius, [instance]);

        var endX = -500f + allowed.X;
        Assert.True(endX < -Radius + 1f, $"swept step tunneled through the wall (ended at x={endX})");
    }

    [Fact]
    public void SteepWallFace_IsNotAcceptedAsGround()
    {
        // A near-vertical cave wall face standing on a floor. The capsule's ring samples land ON the
        // wall (the sweep stops the camera exactly one radius away), so accepting the wall face as
        // ground ratcheted the feet one step height up the wall every frame until the camera popped
        // out through it. The probe must skip the wall and report the floor beneath instead.
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        // ~87° face rising from z = 0 at x = -10 to z = 400 at x = +10, over a floor at z = 0.
        AppendSteepQuad(positions, triangles, -10f, 10f, 0f, 400f, 200f);
        AppendHorizontalQuad(positions, triangles, 0f, 200f);
        var mesh = new CollisionMesh(positions.ToArray(), triangles.ToArray());

        // Straight down: the nearest hit is the wall face at z = 200; the nearest WALKABLE hit is the
        // floor at z = 0, 300 units down.
        Assert.True(mesh.RaycastNearest(new Vector3(0f, 10f, 300f), -Vector3.UnitZ, out var nearest));
        Assert.Equal(100f, nearest, 1e-2f);

        Assert.True(mesh.RaycastNearestWalkable(
            new Vector3(0f, 10f, 300f), -Vector3.UnitZ, Matrix4x4.Identity, out var walkable));
        Assert.Equal(300f, walkable, 1e-2f);
    }

    [Fact]
    public void ShallowRamp_RemainsWalkableGround()
    {
        // 30° ramp (the ray stays in mesh-local space; the placement only tips the face normal):
        // comfortably inside the ground threshold, so the slope filter must not turn ordinary authored
        // ramps into holes the camera falls through.
        var quad = BuildHorizontalQuadMesh(0f, 200f);
        var world = Matrix4x4.CreateRotationX(MathF.PI / 6f);

        Assert.True(quad.RaycastNearestWalkable(new Vector3(0f, 0f, 300f), -Vector3.UnitZ, world, out var t));
        Assert.Equal(300f, t, 1e-2f);
    }

    [Fact]
    public void PlacementRotation_TurnsAWalkableFaceIntoAWall()
    {
        // The slope test has to run on the WORLD normal: the same local quad is ground unplaced and a
        // wall once the placement tips it past vertical-ish.
        var quad = BuildHorizontalQuadMesh(0f, 200f);
        var tipped = Matrix4x4.CreateRotationX(MathF.PI / 2.2f);

        Assert.True(quad.RaycastNearest(new Vector3(0f, 0f, 300f), -Vector3.UnitZ, out _));
        Assert.False(quad.RaycastNearestWalkable(new Vector3(0f, 0f, 300f), -Vector3.UnitZ, tipped, out _));
    }

    [Fact]
    public void GroundlessDescent_RecoversToTheLastConfirmedFloorInsteadOfFallingForever()
    {
        var recovery = new WalkVoidRecovery();
        var floor = new Vector3(1000f, 2000f, 312f);
        recovery.RecordGrounded(floor);

        // No armed jump — this is the clip-through-a-wall case that used to fall without end.
        var z = floor.Z;
        for (var step = 1; step < WalkVoidRecovery.MaxGroundlessFallSteps; step++)
        {
            z -= 10f;
            Assert.False(recovery.TryObserveFallStep(true, false, z, out _, out _));
        }

        Assert.True(recovery.TryObserveFallStep(true, false, z - 10f, out var outcome, out var restored));
        Assert.Equal(WalkFallOutcome.Restore, outcome);
        Assert.Equal(floor, restored);
    }

    [Fact]
    public void GroundlessDescent_BeyondTheDropBudget_RecoversBeforeTheStepBudget()
    {
        // A low frame rate covers thousands of units per step; the distance guard has to fire first.
        var recovery = new WalkVoidRecovery();
        var floor = new Vector3(0f, 0f, 0f);
        recovery.RecordGrounded(floor);

        Assert.True(recovery.TryObserveFallStep(
            true, false, floor.Z - WalkVoidRecovery.MaxGroundlessFallDrop - 1f, out var outcome, out var restored));
        Assert.Equal(WalkFallOutcome.Restore, outcome);
        Assert.Equal(floor, restored);
    }

    [Fact]
    public void GroundlessDescent_WithoutAnyConfirmedFloor_HaltsRatherThanTeleporting()
    {
        var recovery = new WalkVoidRecovery();

        for (var step = 1; step < WalkVoidRecovery.MaxGroundlessFallSteps; step++)
        {
            Assert.False(recovery.TryObserveFallStep(true, false, -100f * step, out _, out _));
        }

        Assert.True(recovery.TryObserveFallStep(true, false, -10_000f, out var outcome, out var restored));
        Assert.Equal(WalkFallOutcome.Halt, outcome);
        Assert.Equal(Vector3.Zero, restored);
    }

    [Fact]
    public void OrdinaryFall_WithAKnownFloorNeverTripsTheWatchdog()
    {
        // Falling down a deep shaft where the floor IS known must keep the normal gravity path, however
        // long the descent lasts.
        var recovery = new WalkVoidRecovery();
        recovery.RecordGrounded(new Vector3(0f, 0f, 5000f));

        for (var step = 0; step < WalkVoidRecovery.MaxGroundlessFallSteps * 4; step++)
        {
            Assert.False(recovery.TryObserveFallStep(true, true, 5000f - 10f * step, out _, out _));
        }
    }

    [Fact]
    public void GroundlessDescent_ResetsOnceAFloorReappears()
    {
        var recovery = new WalkVoidRecovery();
        recovery.RecordGrounded(new Vector3(0f, 0f, 100f));

        for (var step = 0; step < WalkVoidRecovery.MaxGroundlessFallSteps - 1; step++)
        {
            Assert.False(recovery.TryObserveFallStep(true, false, 100f - step, out _, out _));
        }

        Assert.False(recovery.TryObserveFallStep(true, true, 0f, out _, out _)); // floor re-acquired
        Assert.False(recovery.TryObserveFallStep(true, false, -1f, out _, out _)); // counter restarted
    }

    [Fact]
    public void SpeedTreeVisualFallback_IsRejectedButAuthoredHavokWins()
    {
        const string spt = @"trees\treecottonwood01.spt";

        // The synthesized visual soup (leaf billboards + fronds) must never become ground …
        Assert.False(WalkCollisionFallbackPolicy.AllowsResolvedCollisionMesh(
            CollisionMeshSource.VisualFallback, spt, PlacedObjectCategory.Tree));
        // … including when the placement was never categorized as vegetation.
        Assert.False(WalkCollisionFallbackPolicy.AllowsResolvedCollisionMesh(
            CollisionMeshSource.VisualFallback, spt, PlacedObjectCategory.Unknown));
        // … and the speculative OBND box (which spans the whole canopy) is refused too.
        Assert.False(WalkCollisionFallbackPolicy.AllowsObjectBoundsFallback(spt));

        // A .spt never synthesizes a collision soup in the first place.
        var entry = CollisionCacheEntry.Create(
            spt, HavokCollisionProvenance.AbsentOrUnsupported, null, null,
            static () => throw new InvalidOperationException(
                "SpeedTree geometry must not reach the visual fallback."));
        var built = entry.Resolve(spt, PlacedObjectCategory.Unknown);
        Assert.Null(built.Mesh);
        Assert.Equal(CollisionMeshSource.None, built.Source);

        // Authored Havok is checked before the path-invariant .spt exclusion.
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        int[] triangles = [0, 1, 2];
        var authoredEntry = CollisionCacheEntry.Create(
            spt, HavokCollisionProvenance.AuthoredMesh, positions, triangles,
            static () => throw new InvalidOperationException(
                "Authored Havok must win before the visual fallback."));
        var authored = authoredEntry.Resolve(spt, PlacedObjectCategory.Tree);
        Assert.NotNull(authored.Mesh);
        Assert.Equal(CollisionMeshSource.AuthoredHavok, authored.Source);
    }

    [Fact]
    public void VegetationVisualSoup_IsNotWalkableGroundButAuthoredHavokStaysSolid()
    {
        // The shared cache stores category-independent collision sources, so the vegetation rule is
        // applied where the placement's category is known.
        const string nifTree = @"meshes\landscape\trees\treeclusterlg01.nif";
        Assert.False(WalkCollisionFallbackPolicy.AllowsResolvedCollisionMesh(
            CollisionMeshSource.VisualFallback, nifTree, PlacedObjectCategory.Tree));
        Assert.False(WalkCollisionFallbackPolicy.AllowsResolvedCollisionMesh(
            CollisionMeshSource.VisualFallback, nifTree, PlacedObjectCategory.Plants));

        // Authored Havok on a tree (a solid trunk) is authoritative and remains collidable.
        Assert.True(WalkCollisionFallbackPolicy.AllowsResolvedCollisionMesh(
            CollisionMeshSource.AuthoredHavok, nifTree, PlacedObjectCategory.Tree));
        Assert.True(WalkCollisionFallbackPolicy.AllowsResolvedCollisionMesh(
            CollisionMeshSource.AuthoredHavok, @"trees\treecottonwood01.spt", PlacedObjectCategory.Tree));

        // Ordinary statics are unaffected by all of this.
        Assert.True(WalkCollisionFallbackPolicy.AllowsResolvedCollisionMesh(
            CollisionMeshSource.VisualFallback,
            @"meshes\architecture\urban\wall01.nif",
            PlacedObjectCategory.Unknown));
    }

    [Fact]
    public void SlopePartition_LeavesNoFaceThatIsNeitherGroundNorWall()
    {
        // A face rejected by BOTH halves is one the camera can neither stand on nor be stopped by.
        Assert.True(WalkSurfaceSlopePolicy.MinGroundNormalZ <= WalkSurfaceSlopePolicy.MaxWallNormalZ);
    }

    private static CollisionMesh BuildThinWallMesh(float thickness, float halfSpan, float height)
    {
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        AppendVerticalQuad(positions, triangles, -thickness * 0.5f, 0f, height, halfSpan);
        AppendVerticalQuad(positions, triangles, thickness * 0.5f, 0f, height, halfSpan);
        return new CollisionMesh(positions.ToArray(), triangles.ToArray());
    }

    private static CollisionMesh BuildHorizontalQuadMesh(float z, float halfSize)
    {
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        AppendHorizontalQuad(positions, triangles, z, halfSize);
        return new CollisionMesh(positions.ToArray(), triangles.ToArray());
    }

    /// <summary>
    ///     Near-vertical quad rising from <paramref name="zLow" /> at <paramref name="xLow" /> to
    ///     <paramref name="zHigh" /> at <paramref name="xHigh" />. Steep enough to be a wall, but not
    ///     coplanar with a down-ray, so the ray genuinely crosses it.
    /// </summary>
    private static void AppendSteepQuad(
        List<Vector3> positions, List<int> triangles,
        float xLow, float xHigh, float zLow, float zHigh, float halfSpan)
    {
        var b = positions.Count;
        positions.Add(new Vector3(xLow, -halfSpan, zLow));
        positions.Add(new Vector3(xLow, halfSpan, zLow));
        positions.Add(new Vector3(xHigh, halfSpan, zHigh));
        positions.Add(new Vector3(xHigh, -halfSpan, zHigh));
        triangles.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
    }

    /// <summary>Quad in the x = <paramref name="x" /> plane (a wall face): normal is horizontal.</summary>
    private static void AppendVerticalQuad(
        List<Vector3> positions, List<int> triangles, float x, float zLow, float zHigh, float halfSpan)
    {
        var b = positions.Count;
        positions.Add(new Vector3(x, -halfSpan, zLow));
        positions.Add(new Vector3(x, halfSpan, zLow));
        positions.Add(new Vector3(x, halfSpan, zHigh));
        positions.Add(new Vector3(x, -halfSpan, zHigh));
        triangles.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
    }

    private static void AppendHorizontalQuad(
        List<Vector3> positions, List<int> triangles, float z, float halfSize)
    {
        var b = positions.Count;
        positions.Add(new Vector3(-halfSize, -halfSize, z));
        positions.Add(new Vector3(halfSize, -halfSize, z));
        positions.Add(new Vector3(halfSize, halfSize, z));
        positions.Add(new Vector3(-halfSize, halfSize, z));
        triangles.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
    }
}