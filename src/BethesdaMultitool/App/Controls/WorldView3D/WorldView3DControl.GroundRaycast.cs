using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    // How far above the maximum-step plane the down-ray starts, so a surface exactly on the boundary
    // still registers against float error. Hits in this slack band are explicitly rejected below.
    private const float GroundRaycastEpsUp = 8f;

    // Walk-mode player "footprint" radius (world units) — roughly the Bethesda human collision-capsule
    // radius. The camera samples the ground at the center PLUS a ring at this radius and rides the
    // HIGHEST hit, so a single point slipping through a thin seam (the crack between two abutting floor
    // meshes, or a cell border) no longer drops the camera: a point of the footprint still rests on
    // solid ground. Small enough that walking near a low ledge doesn't lift the camera onto it early.
    private const float WalkCapsuleRadius = 24f;

    // Ring samples around the footprint, in addition to the center. 8 (every 45°) catches seams at any
    // orientation; the whole capsule sample is only taken once per frame in walk mode (SnapToGround).
    private const int WalkCapsuleRingSamples = 8;

    // Low obstacles below this height remain step/ground-sampler territory. Horizontal collision starts
    // above it, so stairs and kerbs still raise the camera instead of behaving like vertical walls.
    private const float WalkStepHeight = 48f;

    // Collision promotion shares the renderer's decode/upload machinery, so keep walk-mode's cold
    // path deliberately small. Nearest-first + unique-path selection makes this two useful models per
    // frame instead of an unbounded sweep through every reference in the surrounding nine cells.
    private const int MaxWalkCollisionWarmupRequestsPerFrame = 2;

    /// <summary>
    ///     One walk-capsule candidate: a placed reference whose XY footprint can overlap the capsule,
    ///     with the placement matrix + inverse hoisted out of the per-sample loop (they are per-REF
    ///     constants). <see cref="Collision" /> null = cold mesh → per-sample OBND box fallback.
    /// </summary>
    private readonly record struct GroundCandidate(
        PlacedReference Placement,
        CollisionMesh? Collision,
        Matrix4x4 World,
        Matrix4x4 InverseWorld);

    private readonly record struct ColdGroundCandidate(
        PlacedReference Placement,
        PlacedObjectCategory Category,
        bool AllowsBoundsFallback);

    // Scratch candidate lists (walk-mode is single-threaded on the UI/frame path).
    private readonly List<GroundCandidate> _groundCandidates = new(64);
    private readonly List<GroundCandidate> _ceilingCandidates = new(32);
    private readonly List<GroundCandidate> _horizontalCandidates = new(64);
    private readonly List<WalkCollisionInstance> _horizontalCollisionInstances = new(64);
    private readonly List<ColdGroundCandidate> _coldGroundCandidates = new(64);
    private readonly List<CollisionReferenceCandidate> _walkCollisionWarmupCandidates = new(64);
    private readonly CollisionReferencePriorityResolver _walkCollisionWarmupResolver = new();
    private long _walkCollisionWarmupFrameIndex = -1;
    private int _walkCollisionWarmupRequestsThisFrame;

    /// <summary>
    ///     Sweeps the walk camera's circular footprint through the requested XY move. Warm candidates
    ///     use the cache's Havok-first triangle soup; a cold ordinary solid may temporarily use the same
    ///     policy-gated OBND fallback as ground sampling. The body interval starts above step height,
    ///     preserving the existing stair/ground behavior, and follows camera Z while jumping.
    /// </summary>
    private Vector2 ResolveWalkHorizontalMovement(Vector3 eyePosition, Vector2 requestedDisplacement)
    {
        if (_cellGridLookup is null || requestedDisplacement.LengthSquared() <= float.Epsilon)
        {
            return requestedDisplacement;
        }

        BuildRaycastCandidates(
            eyePosition.X, eyePosition.Y, includeColdObnd: true, _horizontalCandidates,
            additionalReach: requestedDisplacement.Length());

        _horizontalCollisionInstances.Clear();
        foreach (var candidate in _horizontalCandidates)
        {
            if (candidate.Collision is { } mesh)
            {
                _horizontalCollisionInstances.Add(WalkCollisionInstance.FromMesh(mesh, candidate.World));
                continue;
            }

            var placement = candidate.Placement;
            if (placement.Bounds is not { } bounds) continue;
            // Full placement transform (rotation + scale ride candidate.World): the unrotated
            // OBND offset-box walled walkable space beside yaw-rotated long pieces — the
            // road-to-median phantom wall.
            _horizontalCollisionInstances.Add(WalkCollisionInstance.FromPlacedBounds(
                new Vector3(bounds.X1, bounds.Y1, bounds.Z1),
                new Vector3(bounds.X2, bounds.Y2, bounds.Z2),
                candidate.World));
        }

        var bodyMinZ = eyePosition.Z - _controller.EyeHeight + WalkStepHeight;
        var bodyMaxZ = eyePosition.Z + _controller.CeilingHeadroom;
        return WalkHorizontalCollision.Resolve(
            new Vector2(eyePosition.X, eyePosition.Y),
            requestedDisplacement,
            bodyMinZ,
            bodyMaxZ,
            WalkCapsuleRadius,
            _horizontalCollisionInstances);
    }

    /// <summary>
    ///     Capsule-aware ground sample for walk mode: the HIGHEST ground height under the player's
    ///     footprint — the center plus a ring at <see cref="WalkCapsuleRadius" />. A real player has
    ///     width, so taking the max means the camera rides over thin seams instead of falling through a
    ///     single point that slips between two surfaces. Returns <c>null</c> only when neither terrain
    ///     nor an object sits under ANY sample (camera off the loaded grid), preserving
    ///     <c>SnapToGround</c>'s off-edge no-op.
    ///     <para>
    ///         Perf shape: ONE pass over the 3×3 cell neighborhood builds the few candidates whose XY
    ///         footprint can overlap the capsule (matrix + inverse hoisted per candidate); the 9 samples
    ///         then raycast candidates only. The previous shape re-iterated EVERY placement in 9 cells
    ///         per sample — at downtown density (~2,000 placements/cell) that was ~160k filter passes
    ///         and up to thousands of matrix inverts per frame, a multi-ms walk-mode-only tax.
    ///     </para>
    /// </summary>
    private float? SampleGroundHeightCapsule(float worldX, float worldY)
    {
        if (_cellGridLookup is null) return null;
        BuildRaycastCandidates(worldX, worldY, includeColdObnd: true, _groundCandidates);

        var best = SampleGroundAt(worldX, worldY, _groundCandidates);
        for (var i = 0; i < WalkCapsuleRingSamples; i++)
        {
            var angle = MathF.Tau * i / WalkCapsuleRingSamples;
            var h = SampleGroundAt(
                worldX + (MathF.Cos(angle) * WalkCapsuleRadius),
                worldY + (MathF.Sin(angle) * WalkCapsuleRadius),
                _groundCandidates);
            if (h is { } v && (best is null || v > best)) best = v;
        }

        return best;
    }

    /// <summary>
    ///     Single-point ground sample = max(terrain height, highest placed-object surface no more than
    ///     one step above the feet). Warm meshes raycast against real collision triangles
    ///     (rotation/scale exact); cold meshes slab-raycast the placed OBND box through the same
    ///     placement transform (rotation/scale honored; the surface is the box's entry face). Terrain
    ///     remains independent of the placed-object step window so landscape slopes and seams retain
    ///     their existing behavior.
    /// </summary>
    private float? SampleGroundAt(float worldX, float worldY, List<GroundCandidate> candidates)
    {
        var terrain = TerrainHeightSampler.Sample(_cellGridLookup!, worldX, worldY, _data?.RenderCache, _cellSize);

        var probe = WalkGroundProbeWindow.FromEye(
            _camera.Position.Z,
            _controller.EyeHeight,
            WalkStepHeight,
            GroundRaycastEpsUp);
        var origin = new Vector3(worldX, worldY, probe.RayOriginZ);
        var down = new Vector3(0f, 0f, -1f);

        float? objectHit = null;
        foreach (var c in candidates)
        {
            float? hit;
            if (c.Collision is not null)
            {
                // Warm mesh: exact triangle raycast, restricted to faces shallow enough to STAND on.
                // A steep face is a wall (WalkHorizontalCollision owns it); accepting one here let the
                // capsule ring — which samples exactly where the sweep stops the camera — lift the feet
                // one step height per frame up a cave wall until the camera popped out through it.
                // The origin slack can still admit a surface just above the step plane, so every
                // returned world-space hit is explicitly windowed below.
                var localOrigin = Vector3.Transform(origin, c.InverseWorld);
                var localDir = Vector3.TransformNormal(down, c.InverseWorld);
                hit = c.Collision.RaycastNearestWalkable(localOrigin, localDir, c.World, out var tLocal)
                    ? Vector3.Transform(localOrigin + localDir * tLocal, c.World).Z
                    : null;
            }
            else
            {
                var b = c.Placement.Bounds!; // cold candidates are only admitted with bounds
                // Rotation-aware fallback: slab-raycast the placed OBND box through the placement
                // transform (scale rides the matrix). The old unrotated offset-rectangle offered
                // ground beside rotated pieces and none on top of them.
                hit = WalkColdBoundsProbe.TryRaycastDownToTop(
                    new Vector3(b.X1, b.Y1, b.Z1),
                    new Vector3(b.X2, b.Y2, b.Z2),
                    c.World,
                    c.InverseWorld,
                    worldX,
                    worldY,
                    probe.RayOriginZ,
                    out var top)
                    ? top
                    : null;
            }

            if (hit is { } h && probe.AllowsPlacedSurface(h) &&
                (objectHit is null || h > objectHit))
            {
                objectHit = h;
            }
        }

        if (terrain is { } t) return objectHit is { } m && m > t ? m : t;
        return objectHit; // null only when neither terrain nor an object sits under the camera
    }

    /// <summary>
    ///     Scans the camera's cell and its 8 neighbors ONCE, collecting the placements whose XY
    ///     footprint can overlap the walk capsule around (<paramref name="centerX" />,
    ///     <paramref name="centerY" />). The overlap gate is a rotation-safe circumradius derived from
    ///     the scaled OBND (plus capsule radius and slack), so a rotated footprint can't be culled
    ///     wrongly; warm meshes without OBND stay ungated (rare). Placement matrix + inverse are
    ///     computed here, once per candidate, instead of per capsule sample.
    /// </summary>
    private void BuildRaycastCandidates(
        float centerX,
        float centerY,
        bool includeColdObnd,
        List<GroundCandidate> into,
        float additionalReach = 0f)
    {
        into.Clear();
        _coldGroundCandidates.Clear();
        _walkCollisionWarmupCandidates.Clear();
        var gx = (int)MathF.Floor(centerX / _cellSize);
        var gy = (int)MathF.Floor(centerY / _cellSize);
        // Ring reach + eye slack + a safety pad for OBNDs that under-cover their collision mesh.
        var reach = WalkCapsuleRadius + GroundRaycastEpsUp + 64f + MathF.Max(0f, additionalReach);
        var sourceOrder = 0;

        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!_cellGridLookup!.TryGetValue((gx + dx, gy + dy), out var cell)) continue;
                foreach (var p in cell.PlacedObjects)
                {
                    var candidateSourceOrder = sourceOrder++;
                    if (string.IsNullOrEmpty(p.ModelPath)) continue;
                    var authoredDisabled = p.IsInitiallyDisabled ||
                                           _data?.XespDisabledRefs.Contains(p.FormId) == true;
                    if (!_referenceEnabledOverrides.IsVisible(p.FormId, authoredDisabled, _showDisabled)) continue;
                    if (p.RecordType is "ACHR" or "ACRE") continue; // skinned actors carry no static collision
                    if (RenderableReference.IsMarkerModelPath(p.ModelPath) ||
                        RenderableReference.IsImposterModelPath(
                            p.ModelPath, _data?.Game ?? BethesdaGame.Unknown) ||
                        RenderableReference.IsLodDuplicateBaseEditorId(p.BaseEditorId)) continue;
                    // Doors are passable in walk mode: the viewer can't open them, so colliding with a
                    // shut door slab either blocks the doorway or — because the capsule rides the
                    // HIGHEST surface — walks the camera up the slab to the ceiling. Treat every door
                    // as open (excluded from ground AND ceiling candidates); the door FRAME is a
                    // separate static and still collides normally.
                    var category = _data?.CategoryIndex.GetValueOrDefault(
                        p.BaseFormId, PlacedObjectCategory.Unknown) ?? PlacedObjectCategory.Unknown;
                    if (category == PlacedObjectCategory.Door)
                    {
                        continue;
                    }

                    var resolution = _referenceMeshCache12?.ResolveCollisionMesh(
                        p.ModelPath!, category) ?? CollisionMeshResolution.Unresolved;

                    var ddx = centerX - p.X;
                    var ddy = centerY - p.Y;
                    var distanceSquared = (ddx * ddx) + (ddy * ddy);
                    if (resolution.Mesh is { } collision)
                    {
                        // The cache builds ONE ordinary entry per model path under category Unknown, so
                        // CollisionMeshBuilder's vegetation rule never saw this placement's category and
                        // a tree's synthesized canopy soup arrived here as walkable ground ("walk mode
                        // can stand on SPT leaves"). Re-apply the policy at the placement site;
                        // authored Havok is exempt and stays solid.
                        if (WalkCollisionFallbackPolicy.AllowsResolvedCollisionMesh(
                                resolution.Source, p.ModelPath, category))
                        {
                            TryAddWarmRaycastCandidate(p, collision, centerX, centerY, reach, into);
                        }

                        continue;
                    }

                    // A cold solid can promote its authoritative Havok/solid soup even when it has no
                    // usable OBND. FNV CliffVertiC2 is the retail gate: all 67 master placements carry
                    // an all-zero bound, so the old path produced only a point-sized fallback and then
                    // waited indefinitely for the visible-reference renderer to make the mesh resident.
                    // Use the same one-cell transient radius as render culling until real mesh bounds
                    // arrive. Effect paths may receive one bounded decode because some carry authored
                    // Havok, but they never receive speculative OBND/visual collision. A decoded-null
                    // path is remembered by the mesh cache so it cannot steal warmup slots forever.
                    //
                    // The OBND box is a COLD approximation only: a RESOLVED verdict — including
                    // resolved-to-None — is authoritative and must end it. CreosoteBushLg authors NO
                    // bhk blocks at all (its STAT even carries a BRUS passthrough sound: designed
                    // walk-through) and its blend-only visual yields no fallback soup, so the model
                    // resolves to None — yet its valid 245×229×179 OBND kept synthesizing a phantom
                    // collider every frame. (WhiteHorseNettle never hit this only because its ACTI
                    // OBND is all zeros.)
                    var allowsBoundsFallback = !resolution.IsResolved &&
                        WalkCollisionFallbackPolicy.AllowsObjectBoundsFallback(p.ModelPath, category);
                    var allowsWarmup = _referenceMeshCache12 is not null && !resolution.IsResolved;
                    if (!allowsBoundsFallback && !allowsWarmup)
                    {
                        continue;
                    }

                    float coldRadius;
                    if (p.Bounds is { IsDegenerate: false } b)
                    {
                        var scale = p.Scale > 0f ? p.Scale : 1f;
                        var rx = MathF.Max(MathF.Abs(b.X1), MathF.Abs(b.X2)) * scale;
                        var ry = MathF.Max(MathF.Abs(b.Y1), MathF.Abs(b.Y2)) * scale;
                        coldRadius = MathF.Sqrt((rx * rx) + (ry * ry)) + reach;
                    }
                    else
                    {
                        coldRadius = RenderableReference.NoBoundsFallbackRadius + reach;
                    }
                    if (distanceSquared > coldRadius * coldRadius) continue;

                    _coldGroundCandidates.Add(new ColdGroundCandidate(
                        p,
                        category,
                        allowsBoundsFallback));
                    if (allowsWarmup)
                    {
                        _walkCollisionWarmupCandidates.Add(new CollisionReferenceCandidate(
                            p.ModelPath!, p.FormId, distanceSquared, Matrix4x4.Identity,
                            candidateSourceOrder, category));
                    }
                }
            }
        }

        if (_walkCollisionWarmupFrameIndex != _profileFrameIndex)
        {
            _walkCollisionWarmupFrameIndex = _profileFrameIndex;
            _walkCollisionWarmupRequestsThisFrame = 0;
        }

        var remainingWarmups = MaxWalkCollisionWarmupRequestsPerFrame -
                               _walkCollisionWarmupRequestsThisFrame;
        if (remainingWarmups > 0 && _referenceMeshCache12 is { } cache &&
            _walkCollisionWarmupCandidates.Count > 0)
        {
            _walkCollisionWarmupRequestsThisFrame += _walkCollisionWarmupResolver.WarmNearest(
                _walkCollisionWarmupCandidates,
                cache.GetOrWarmCollisionMesh,
                remainingWarmups);
        }

        // Re-resolve after promotion: an already-decoded payload can publish its collision soup
        // synchronously. Otherwise preserve the ordinary valid-OBND fallback for this frame; all-zero
        // bounds are missing data, never a point-sized wall/floor.
        foreach (var cold in _coldGroundCandidates)
        {
            var p = cold.Placement;
            var resolution = _referenceMeshCache12?.ResolveCollisionMesh(
                p.ModelPath!, cold.Category) ?? CollisionMeshResolution.Unresolved;
            if (resolution.Mesh is { } collision)
            {
                // Same placement-site vegetation/SpeedTree gate as the warm pass above.
                if (WalkCollisionFallbackPolicy.AllowsResolvedCollisionMesh(
                        resolution.Source, p.ModelPath, cold.Category))
                {
                    TryAddWarmRaycastCandidate(p, collision, centerX, centerY, reach, into);
                }

                continue;
            }

            // The warmup above can complete synchronously: a model that just resolved to NONE is
            // now authoritative no-collision and must not fall through to the OBND box.
            if (resolution.IsResolved)
            {
                continue;
            }

            if (!includeColdObnd ||
                !cold.AllowsBoundsFallback ||
                p.Bounds is not { IsDegenerate: false })
            {
                continue;
            }
            // Cold candidates carry the real placement transform so the OBND fallback (ground
            // slab probe + horizontal footprint quad) honors rotation and scale.
            var coldWorld = PlacedReferenceTransform.ComposeWorldMatrix(
                p.X, p.Y, p.Z, p.RotX, p.RotY, p.RotZ, p.Scale);
            if (!Matrix4x4.Invert(coldWorld, out var coldInverse)) continue;
            into.Add(new GroundCandidate(
                p,
                null,
                coldWorld,
                coldInverse));
        }
    }

    private static void TryAddWarmRaycastCandidate(
        PlacedReference placement,
        CollisionMesh collision,
        float centerX,
        float centerY,
        float reach,
        List<GroundCandidate> into)
    {
        var world = PlacedReferenceTransform.ComposeWorldMatrix(
            placement.X, placement.Y, placement.Z,
            placement.RotX, placement.RotY, placement.RotZ, placement.Scale);
        if (!Matrix4x4.Invert(world, out var inverse)) return;

        // Gate authoritative collision by its own bounds, not OBND. Some Bethesda statics under-cover
        // their physics soup; using OBND here would decode the correct Havok wall and silently exclude it.
        var scale = placement.Scale > 0f ? placement.Scale : 1f;
        var ax = MathF.Max(MathF.Abs(collision.LocalMin.X), MathF.Abs(collision.LocalMax.X));
        var ay = MathF.Max(MathF.Abs(collision.LocalMin.Y), MathF.Abs(collision.LocalMax.Y));
        var az = MathF.Max(MathF.Abs(collision.LocalMin.Z), MathF.Abs(collision.LocalMax.Z));
        var radius = MathF.Sqrt((ax * ax) + (ay * ay) + (az * az)) * scale + reach;
        var dx = centerX - placement.X;
        var dy = centerY - placement.Y;
        if ((dx * dx) + (dy * dy) > radius * radius) return;

        into.Add(new GroundCandidate(placement, collision, world, inverse));
    }

    /// <summary>
    ///     Resolves a model path to its cached walk-mode collision state for the debug overlay. Reads
    ///     <see cref="_referenceMeshCache12" /> live because the overlay renderer is constructed before
    ///     the reference pipeline.
    /// </summary>
    private CollisionMeshResolution ResolveCollisionMesh(
        string modelPath,
        PlacedObjectCategory category)
        => _referenceMeshCache12?.ResolveCollisionMesh(modelPath, category) ??
           CollisionMeshResolution.Unresolved;

    /// <summary>
    ///     Bounded collision-overlay cold path. The overlay calls this for at most two unique nearby
    ///     models per frame. The shared reference cache re-offers the exact distance to its
    ///     decrease-key decode queue and, when a decoded payload fits the remaining frame upload
    ///     budget, creates the collision mesh immediately as part of the normal upload.
    /// </summary>
    private CollisionMeshResolution ResolveOrWarmCollisionMesh(
        string modelPath,
        PlacedObjectCategory category,
        float distanceSquared)
        => _referenceMeshCache12?.GetOrWarmCollisionMesh(modelPath, category, distanceSquared) ??
           CollisionMeshResolution.Unresolved;

    /// <summary>
    ///     Walk-mode ceiling lookup for jumps: returns the world Z of the nearest placed-object surface
    ///     directly ABOVE the eye under (<paramref name="worldX" />, <paramref name="worldY" />), or
    ///     <c>null</c> when nothing is overhead. Casts a straight-up ray against the cached collision
    ///     meshes (warm only — a not-yet-streamed mesh simply yields no ceiling that frame). No terrain
    ///     term: terrain is never overhead.
    /// </summary>
    private float? SampleCeilingHeight(float worldX, float worldY)
    {
        if (_cellGridLookup is null) return null;
        // Same one-pass candidate build as the ground capsule (warm collision meshes only — a
        // not-yet-streamed mesh simply yields no ceiling that frame), so dense cells aren't
        // re-iterated per call.
        BuildRaycastCandidates(worldX, worldY, includeColdObnd: false, _ceilingCandidates);

        var eyeZ = _camera.Position.Z;
        var origin = new Vector3(worldX, worldY, eyeZ);
        var up = new Vector3(0f, 0f, 1f);

        float? best = null; // lowest surface strictly above the eye
        foreach (var c in _ceilingCandidates)
        {
            var localOrigin = Vector3.Transform(origin, c.InverseWorld);
            var localDir = Vector3.TransformNormal(up, c.InverseWorld);
            if (!c.Collision!.RaycastNearest(localOrigin, localDir, out var tLocal)) continue;

            var h = Vector3.Transform(localOrigin + localDir * tLocal, c.World).Z;
            if (h > eyeZ && (best is null || h < best)) best = h;
        }

        return best;
    }
}
