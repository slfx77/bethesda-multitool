using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     v3 Phase 3 — a single placed-object draw item, baked once per <c>LoadData</c> from a
///     <see cref="PlacedReference" />. The render loop reuses these every frame instead of
///     recomposing the world matrix and re-resolving the filter conditions per cell visit.
///     <para>
///         <see cref="WorldMatrix" /> is the composed model-to-world matrix. Per the
///         Fallout / NifSkope / GECK convention, rotations apply in X→Y→Z body order with Z
///         as the up axis, then translation: <c>world = S · Rx · Ry · Rz · T</c> in
///         row-vector (System.Numerics) algebra. The vertex shader does
///         <c>mul(viewProj, mul(world, position))</c>; CPU-side this matches
///         <c>position · world · viewProj</c>.
///     </para>
///     <para>
///         <see cref="BoundsCenter" /> + <see cref="BoundsRadius" /> are a conservative
///         bounding sphere in world space derived from the base record's OBND. Used for
///         per-REFR cylinder culling on top of the cell-level cull.
///     </para>
/// </summary>
internal readonly record struct RenderableReference(
    uint FormId,
    Matrix4x4 WorldMatrix,
    string ModelPath,
    Vector3 BoundsCenter,
    float BoundsRadius)
{
    /// <summary>
    ///     Builds a <see cref="RenderableReference" /> from a <see cref="PlacedReference" />.
    ///     Returns <c>null</c> for ACHR/ACRE (skinned actors — deferred to v4), refs without a
    ///     resolved model path, or refs the renderer cannot place (e.g. NaN coordinates).
    /// </summary>
    public static RenderableReference? TryBuild(PlacedReference placement)
    {
        // Skip skinned actors — v3 renders static meshes only.
        if (placement.RecordType is "ACHR" or "ACRE") return null;
        if (string.IsNullOrEmpty(placement.ModelPath)) return null;

        // Pathological NaN/Inf coords sometimes appear in DMP-only loads where parser fell back
        // to garbage memory. Defensive skip — better to drop a single REFR than NaN-poison the
        // GPU draw.
        if (!float.IsFinite(placement.X) || !float.IsFinite(placement.Y) || !float.IsFinite(placement.Z))
            return null;

        var world = ComposeWorldMatrix(placement);
        var (center, radius) = ComposeWorldBounds(placement, world);

        return new RenderableReference(
            FormId: placement.FormId,
            WorldMatrix: world,
            ModelPath: placement.ModelPath!,
            BoundsCenter: center,
            BoundsRadius: radius);
    }

    /// <summary>
    ///     <c>world = S · Rx · Ry · Rz · T</c> in row-vector algebra (System.Numerics). The
    ///     rotation order matches the documented Bethesda / NifSkope convention: X applied
    ///     first, then Y, then Z, around the world (post-scale, pre-translate) axes.
    ///     <para>
    ///         If smoke testing reveals a different ordering (rare but possible per build),
    ///         the fix is reordering these three lines — no surrounding changes needed.
    ///     </para>
    /// </summary>
    private static Matrix4x4 ComposeWorldMatrix(PlacedReference p)
    {
        var scale = p.Scale > 0f ? p.Scale : 1f;
        return Matrix4x4.CreateScale(scale)
             * Matrix4x4.CreateRotationX(p.RotX)
             * Matrix4x4.CreateRotationY(p.RotY)
             * Matrix4x4.CreateRotationZ(p.RotZ)
             * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
    }

    /// <summary>
    ///     World-space bounding sphere from the base record's OBND, conservatively wrapped
    ///     around the rotated AABB. Falls back to a fixed-radius sphere when OBND is absent
    ///     (some MSTT / runtime-only refs). Computed once at LoadData so the per-frame cull
    ///     just does a `(centerWorld - cameraXY).LengthSq < (radius + cylinderRadius)^2`.
    /// </summary>
    private static (Vector3 Center, float Radius) ComposeWorldBounds(PlacedReference p, Matrix4x4 world)
    {
        var bounds = p.Bounds;
        if (bounds is null)
        {
            // No OBND — use a generic 256-unit sphere centred at the REFR position. 256 ≈ a
            // human-scale prop; large props without OBND will get over-culled (acceptable for
            // v3 first pass; tighten in v4 if visible artifacts).
            return (new Vector3(p.X, p.Y, p.Z), 256f);
        }

        // OBND is in mesh-local space. The conservative sphere = (centerLocal · world) for the
        // center, and (maxExtent · scale) for the radius — over-approximates but is cheap and
        // never under-culls.
        var localCenter = new Vector3(
            (bounds.X1 + bounds.X2) * 0.5f,
            (bounds.Y1 + bounds.Y2) * 0.5f,
            (bounds.Z1 + bounds.Z2) * 0.5f);
        var localExtents = new Vector3(
            (bounds.X2 - bounds.X1) * 0.5f,
            (bounds.Y2 - bounds.Y1) * 0.5f,
            (bounds.Z2 - bounds.Z1) * 0.5f);

        var worldCenter = Vector3.Transform(localCenter, world);
        var scale = p.Scale > 0f ? p.Scale : 1f;
        // Diagonal of the AABB is the tightest sphere that contains the rotated box.
        var radius = localExtents.Length() * scale;
        // Safety floor — vanishingly small OBNDs (sometimes 0/0/0/0/0/0 in DMP captures) would
        // get culled before they ever appear. 64 ≈ a small prop.
        if (radius < 64f) radius = 64f;
        return (worldCenter, radius);
    }
}
