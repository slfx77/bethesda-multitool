using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     Covers the broadphase's shadow-caster ring admission. Before it existed the renderer had to
///     pass a NULL frustum whenever the sun-shadow ring was armed — which is the default — so the
///     bucket rejection was effectively disabled and the whole placement set reached the per-reference
///     loop (measured at ~540k candidates/frame against ~190k with the filter armed).
/// </summary>
public sealed class ReferencePlacementBroadphaseRingTests
{
    /// <summary>
    ///     A tight explicit OBND matters here: a placement with no bounds gets a cell-sized fallback
    ///     cull radius, which inflates its bucket AABB by 4096 units and lets it reach the frustum from
    ///     well behind the camera — masking exactly the rejection these tests are asserting.
    /// </summary>
    private static readonly ObjectBounds SmallBounds = new()
    {
        X1 = -32, Y1 = -32, Z1 = -32, X2 = 32, Y2 = 32, Z2 = 32
    };

    private static CellRecord CellWith(params (float X, float Y)[] placements)
    {
        var placed = new List<PlacedReference>();
        foreach (var (x, y) in placements)
        {
            placed.Add(new PlacedReference
            {
                FormId = (uint)(0x2000 + placed.Count),
                BaseFormId = 0x3000,
                ModelPath = @"meshes\clutter\testcrate01.nif",
                X = x,
                Y = y,
                Z = 0f,
                Scale = 1f,
                Bounds = SmallBounds
            });
        }

        return new CellRecord { FormId = 0x1234, GridX = 0, GridY = 0, PlacedObjects = placed };
    }

    /// <summary>Looks down +Y from the origin, so anything at large -Y is behind the camera.</summary>
    private static Frustum ForwardFrustum()
    {
        return Frustum.FromViewProjection(
            Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ) *
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 16f, 400_000f));
    }

    [Fact]
    public void Query_WithoutARing_RejectsBucketsBehindTheCamera()
    {
        var cache = new WorldRenderCache();
        var cell = CellWith((0f, 20_000f), (0f, -20_000f));
        var results = new List<RenderableReference>();

        cache.QueryPlacementCandidates(cell, 0f, 0f, 100_000f, ForwardFrustum(), 512f, results);

        Assert.NotEmpty(results);
        Assert.All(results,
            r => Assert.True(r.BoundsCenter.Y > 0f, "a reference behind the camera survived a ring-free broadphase"));
    }

    [Fact]
    public void Query_WithARing_KeepsBucketsTheFrustumRejects()
    {
        var cache = new WorldRenderCache();
        // In front, and behind but inside an 8192-unit caster ring: the second is invisible yet must
        // still reach the per-reference loop so it can be collected as a shadow-only caster.
        var cell = CellWith((0f, 20_000f), (0f, -4_000f));
        var withoutRing = new List<RenderableReference>();
        var withRing = new List<RenderableReference>();

        cache.QueryPlacementCandidates(cell, 0f, 0f, 100_000f, ForwardFrustum(), 512f, withoutRing);
        cache.QueryPlacementCandidates(cell, 0f, 0f, 100_000f, ForwardFrustum(), 512f, withRing, 8_192f);

        Assert.Contains(withRing, r => r.BoundsCenter.Y < 0f);
        Assert.True(withRing.Count > withoutRing.Count);
    }

    [Fact]
    public void Query_RingDoesNotAdmitBucketsBeyondIt()
    {
        var cache = new WorldRenderCache();
        var cell = CellWith((0f, -40_000f));
        var results = new List<RenderableReference>();

        // Behind the camera and far outside the ring — the frustum rejection must still stand.
        cache.QueryPlacementCandidates(cell, 0f, 0f, 100_000f, ForwardFrustum(), 512f, results, 8_192f);

        Assert.Empty(results);
    }

    [Fact]
    public void Query_RingRadiusZero_MatchesTheLegacyFrustumOnlyBehaviour()
    {
        var cache = new WorldRenderCache();
        var cell = CellWith((0f, 20_000f), (0f, -4_000f), (5_000f, 30_000f));
        var legacy = new List<RenderableReference>();
        var explicitZero = new List<RenderableReference>();

        cache.QueryPlacementCandidates(cell, 0f, 0f, 100_000f, ForwardFrustum(), 512f, legacy);
        cache.QueryPlacementCandidates(cell, 0f, 0f, 100_000f, ForwardFrustum(), 512f, explicitZero, 0f);

        Assert.Equal(legacy.Count, explicitZero.Count);
    }
}