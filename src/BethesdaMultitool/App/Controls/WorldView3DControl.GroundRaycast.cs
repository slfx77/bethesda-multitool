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
    ///     Capsule-aware ground sample for walk mode: the HIGHEST ground height under the player's
    ///     footprint — the center plus a ring at <see cref="WalkCapsuleRadius" />. A real player has
    ///     width, so taking the max means the camera rides over thin seams instead of falling through a
    ///     single point that slips between two surfaces. Returns <c>null</c> only when neither terrain
    ///     nor an object sits under ANY sample (camera off the loaded grid), preserving
    ///     <c>SnapToGround</c>'s off-edge no-op. Reuses the single-point <see cref="SampleGroundHeight" />
    ///     for each sample, so terrain + warm-mesh triangle raycasts apply at every footprint point.
    /// </summary>
    private float? SampleGroundHeightCapsule(float worldX, float worldY)
    {
        var best = SampleGroundHeight(worldX, worldY);
        for (var i = 0; i < WalkCapsuleRingSamples; i++)
        {
            var angle = MathF.Tau * i / WalkCapsuleRingSamples;
            var h = SampleGroundHeight(
                worldX + (MathF.Cos(angle) * WalkCapsuleRadius),
                worldY + (MathF.Sin(angle) * WalkCapsuleRadius));
            if (h is { } v && (best is null || v > best)) best = v;
        }

        return best;
    }

    private float? SampleGroundHeight(float worldX, float worldY)
    {
        if (_cellGridLookup is null) return null;
        var terrain = TerrainHeightSampler.Sample(_cellGridLookup, worldX, worldY, _data?.RenderCache, _cellSize);

        // Real downward triangle raycast so walk mode rides ON the actual surface of placed meshes
        // (floors, walkways, rocks, roofs) instead of their axis-aligned bounding box — rotation is
        // respected, and a roof ABOVE the eye is never grabbed because the ray starts at the eye and
        // casts down. Ground = max(terrain, highest object surface at/below the eye).
        var objectHit = RaycastObjectGround(worldX, worldY, _camera.Position.Z);
        if (terrain is { } t) return objectHit is { } m && m > t ? m : t;
        return objectHit; // null only when neither terrain nor an object sits under the camera
    }

    /// <summary>
    ///     Casts a ray straight down from just above the eye and returns the world Z of the highest
    ///     placed-object surface at/below the eye under (<paramref name="worldX" />,
    ///     <paramref name="worldY" />), or <c>null</c> when nothing is hit. Scans the camera's cell and
    ///     its 8 neighbors (a ref whose origin sits in an adjacent cell can still overlap the camera
    ///     footprint). Warm meshes raycast against real triangles; cold meshes fall back to the OBND box
    ///     for that frame. Only called once per frame in walk mode (<c>SnapToGround</c>).
    /// </summary>
    private float? RaycastObjectGround(float worldX, float worldY, float eyeZ)
    {
        if (_cellGridLookup is null) return null;
        var gx = (int)MathF.Floor(worldX / _cellSize);
        var gy = (int)MathF.Floor(worldY / _cellSize);

        var origin = new Vector3(worldX, worldY, eyeZ + GroundRaycastEpsUp);
        var down = new Vector3(0f, 0f, -1f);

        float? best = null;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!_cellGridLookup.TryGetValue((gx + dx, gy + dy), out var cell)) continue;
                foreach (var p in cell.PlacedObjects)
                {
                    if (string.IsNullOrEmpty(p.ModelPath)) continue;
                    if (!_showDisabled && p.IsInitiallyDisabled) continue;
                    if (p.RecordType is "ACHR" or "ACRE") continue; // skinned actors carry no static collision
                    if (RenderableReference.IsMarkerModelPath(p.ModelPath) ||
                        RenderableReference.IsImposterModelPath(p.ModelPath)) continue;

                    var hit = TryRaycastReferenceGround(p, origin, down, eyeZ);
                    if (hit is { } h && (best is null || h > best)) best = h;
                }
            }
        }

        return best;
    }

    /// <summary>
    ///     Intersects the world-space down-ray with one placed reference. Transforms the ray into the
    ///     mesh's local space (inverting the placement world matrix — rotation/scale exact), raycasts
    ///     the cached collision triangles, then maps the local hit point back to world to read its Z.
    ///     Falls back to the rotation-ignoring OBND box top when the collision mesh isn't cached yet.
    /// </summary>
    private float? TryRaycastReferenceGround(PlacedReference p, Vector3 worldOrigin, Vector3 worldDir, float eyeZ)
    {
        if (_referenceMeshCache12 is not null &&
            _referenceMeshCache12.TryGetCollisionMesh(p.ModelPath!, out var collision) &&
            collision is not null)
        {
            // Warm mesh: exact triangle raycast. Already constrained to the down-ray → at/below the eye.
            return RaycastReferenceCollisionWorldZ(p, collision, worldOrigin, worldDir);
        }

        // Cold-mesh fallback: axis-aligned OBND box top placed at the ref origin (rotation ignored),
        // gated by the same "at/below the eye" rule so it never yanks the camera onto a roof.
        if (p.Bounds is not { } b) return null;
        var scale = p.Scale > 0f ? p.Scale : 1f;
        if (worldOrigin.X < p.X + b.X1 * scale || worldOrigin.X > p.X + b.X2 * scale) return null;
        if (worldOrigin.Y < p.Y + b.Y1 * scale || worldOrigin.Y > p.Y + b.Y2 * scale) return null;
        var top = p.Z + b.Z2 * scale;
        return top <= eyeZ + GroundRaycastEpsUp ? top : null;
    }

    /// <summary>
    ///     Transforms a world ray into one ref's mesh-local space (inverting the placement world
    ///     matrix — rotation/scale exact), raycasts the cached collision triangles, and maps the hit
    ///     point back to a world Z. Shared by the down-ray (ground) and up-ray (ceiling) samplers.
    ///     Returns null when the placement matrix is non-invertible or the ray misses.
    /// </summary>
    /// <summary>
    ///     Resolves a model path to its cached walk-mode collision mesh for the debug overlay (null when
    ///     not warm yet). Reads <see cref="_referenceMeshCache12" /> live because the overlay renderer is
    ///     constructed before the reference pipeline.
    /// </summary>
    private CollisionMesh? ResolveCollisionMesh(string modelPath)
        => _referenceMeshCache12 is { } cache && cache.TryGetCollisionMesh(modelPath, out var mesh) ? mesh : null;

    private static float? RaycastReferenceCollisionWorldZ(
        PlacedReference p, CollisionMesh collision, Vector3 worldOrigin, Vector3 worldDir)
    {
        var world = PlacedReferenceTransform.ComposeWorldMatrix(p.X, p.Y, p.Z, p.RotX, p.RotY, p.RotZ, p.Scale);
        if (!Matrix4x4.Invert(world, out var inv)) return null;

        var localOrigin = Vector3.Transform(worldOrigin, inv);
        var localDir = Vector3.TransformNormal(worldDir, inv);
        if (!collision.RaycastNearest(localOrigin, localDir, out var tLocal)) return null;

        return Vector3.Transform(localOrigin + localDir * tLocal, world).Z;
    }

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
        var eyeZ = _camera.Position.Z;
        var gx = (int)MathF.Floor(worldX / _cellSize);
        var gy = (int)MathF.Floor(worldY / _cellSize);

        var origin = new Vector3(worldX, worldY, eyeZ);
        var up = new Vector3(0f, 0f, 1f);

        float? best = null; // lowest surface strictly above the eye
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!_cellGridLookup.TryGetValue((gx + dx, gy + dy), out var cell)) continue;
                foreach (var p in cell.PlacedObjects)
                {
                    if (string.IsNullOrEmpty(p.ModelPath)) continue;
                    if (!_showDisabled && p.IsInitiallyDisabled) continue;
                    if (p.RecordType is "ACHR" or "ACRE") continue; // skinned actors carry no static collision
                    if (RenderableReference.IsMarkerModelPath(p.ModelPath) ||
                        RenderableReference.IsImposterModelPath(p.ModelPath)) continue;

                    if (_referenceMeshCache12 is null ||
                        !_referenceMeshCache12.TryGetCollisionMesh(p.ModelPath!, out var collision) ||
                        collision is null) continue;

                    var hit = RaycastReferenceCollisionWorldZ(p, collision, origin, up);
                    if (hit is { } h && h > eyeZ && (best is null || h < best)) best = h;
                }
            }
        }

        return best;
    }
}
