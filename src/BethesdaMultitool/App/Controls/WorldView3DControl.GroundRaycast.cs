using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    // How far above the eye the down-ray starts, so a surface the camera is already resting on still
    // registers against float error. Also the slack on the "surface must be at/below the eye" rule.
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

    // Scratch candidate lists (walk-mode is single-threaded on the UI/frame path).
    private readonly List<GroundCandidate> _groundCandidates = new(64);
    private readonly List<GroundCandidate> _ceilingCandidates = new(32);

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
    ///     Single-point ground sample = max(terrain height, highest placed-object surface at/below the
    ///     eye). Warm meshes raycast against real collision triangles (rotation/scale exact); cold
    ///     meshes fall back to the rotation-ignoring OBND box top, gated by the same "at/below the eye"
    ///     rule so it never yanks the camera onto a roof.
    /// </summary>
    private float? SampleGroundAt(float worldX, float worldY, List<GroundCandidate> candidates)
    {
        var terrain = TerrainHeightSampler.Sample(_cellGridLookup!, worldX, worldY, _data?.RenderCache, _cellSize);

        var eyeZ = _camera.Position.Z;
        var origin = new Vector3(worldX, worldY, eyeZ + GroundRaycastEpsUp);
        var down = new Vector3(0f, 0f, -1f);

        float? objectHit = null;
        foreach (var c in candidates)
        {
            float? hit;
            if (c.Collision is not null)
            {
                // Warm mesh: exact triangle raycast (down-ray → hits are at/below the eye by construction).
                var localOrigin = Vector3.Transform(origin, c.InverseWorld);
                var localDir = Vector3.TransformNormal(down, c.InverseWorld);
                hit = c.Collision.RaycastNearest(localOrigin, localDir, out var tLocal)
                    ? Vector3.Transform(localOrigin + localDir * tLocal, c.World).Z
                    : null;
            }
            else
            {
                var p = c.Placement;
                var b = p.Bounds!; // cold candidates are only admitted with bounds
                var scale = p.Scale > 0f ? p.Scale : 1f;
                if (worldX < p.X + b.X1 * scale || worldX > p.X + b.X2 * scale ||
                    worldY < p.Y + b.Y1 * scale || worldY > p.Y + b.Y2 * scale)
                {
                    continue;
                }

                var top = p.Z + b.Z2 * scale;
                hit = top <= eyeZ + GroundRaycastEpsUp ? top : null;
            }

            if (hit is { } h && (objectHit is null || h > objectHit)) objectHit = h;
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
    private void BuildRaycastCandidates(float centerX, float centerY, bool includeColdObnd, List<GroundCandidate> into)
    {
        into.Clear();
        var gx = (int)MathF.Floor(centerX / _cellSize);
        var gy = (int)MathF.Floor(centerY / _cellSize);
        // Ring reach + eye slack + a safety pad for OBNDs that under-cover their collision mesh.
        var reach = WalkCapsuleRadius + GroundRaycastEpsUp + 64f;

        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!_cellGridLookup!.TryGetValue((gx + dx, gy + dy), out var cell)) continue;
                foreach (var p in cell.PlacedObjects)
                {
                    if (string.IsNullOrEmpty(p.ModelPath)) continue;
                    if (!_showDisabled && p.IsInitiallyDisabled) continue;
                    if (p.RecordType is "ACHR" or "ACRE") continue; // skinned actors carry no static collision
                    if (RenderableReference.IsMarkerModelPath(p.ModelPath) ||
                        RenderableReference.IsImposterModelPath(p.ModelPath) ||
                        RenderableReference.IsLodDuplicateBaseEditorId(p.BaseEditorId)) continue;

                    CollisionMesh? collision = null;
                    if (_referenceMeshCache12 is not null)
                    {
                        _referenceMeshCache12.TryGetCollisionMesh(p.ModelPath!, out collision);
                    }

                    if (collision is null && (!includeColdObnd || p.Bounds is null))
                    {
                        continue; // nothing raycastable for this candidate
                    }

                    if (p.Bounds is { } b)
                    {
                        var scale = p.Scale > 0f ? p.Scale : 1f;
                        var rx = MathF.Max(MathF.Abs(b.X1), MathF.Abs(b.X2)) * scale;
                        var ry = MathF.Max(MathF.Abs(b.Y1), MathF.Abs(b.Y2)) * scale;
                        var r = MathF.Sqrt((rx * rx) + (ry * ry)) + reach; // rotation-safe circumradius
                        var ddx = centerX - p.X;
                        var ddy = centerY - p.Y;
                        if ((ddx * ddx) + (ddy * ddy) > r * r) continue;
                    }

                    var world = Matrix4x4.Identity;
                    var inverse = Matrix4x4.Identity;
                    if (collision is not null)
                    {
                        world = PlacedReferenceTransform.ComposeWorldMatrix(
                            p.X, p.Y, p.Z, p.RotX, p.RotY, p.RotZ, p.Scale);
                        if (!Matrix4x4.Invert(world, out inverse)) continue;
                    }

                    into.Add(new GroundCandidate(p, collision, world, inverse));
                }
            }
        }
    }

    /// <summary>
    ///     Resolves a model path to its cached walk-mode collision mesh for the debug overlay (null when
    ///     not warm yet). Reads <see cref="_referenceMeshCache12" /> live because the overlay renderer is
    ///     constructed before the reference pipeline.
    /// </summary>
    private CollisionMesh? ResolveCollisionMesh(string modelPath)
        => _referenceMeshCache12 is { } cache && cache.TryGetCollisionMesh(modelPath, out var mesh) ? mesh : null;

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
