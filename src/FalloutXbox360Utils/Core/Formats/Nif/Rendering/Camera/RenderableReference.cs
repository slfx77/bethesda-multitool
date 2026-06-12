using System.IO;
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
///         Gamebryo / NifSkope Euler convention the world rotation is column-vector
///         <c>Rx * Ry * Rz</c> (radians, right-handed CCW; Z up). System.Numerics is
///         row-vector, so the equivalent is <c>world = S * Rz * Ry * Rx * T</c> in
///         row-vector algebra. The vertex shader does
///         <c>mul(viewProj, mul(world, position))</c>; CPU-side this matches
///         <c>position * world * viewProj</c>.
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
    float BoundsRadius,
    uint MeshId,
    bool IsInitiallyDisabled)
{
    /// <summary>
    ///     4-pre Item B — computes the stable per-process MeshId from a ModelPath. Used to
    ///     dedupe the per-REFR mesh-cache lookup in the cull loop: instead of doing a
    ///     case-insensitive string hash + dict lookup per REFR (~80 ns × 5000 REFRs), the
    ///     cull loop reads from a per-frame <c>Dictionary&lt;uint, CachedNifMesh12?&gt;</c>
    ///     by the int MeshId (~25 ns hash). Same-process guarantee is sufficient: the
    ///     registry / per-frame resolve map both live and die with the process.
    /// </summary>
    public static uint ComputeMeshId(string modelPath)
        => (uint)string.GetHashCode(modelPath, StringComparison.OrdinalIgnoreCase);

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

        if (DumpFilter is { Length: > 0 } && placement.ModelPath!.Contains(DumpFilter, StringComparison.OrdinalIgnoreCase))
            DumpRefr(placement, world);

        return new RenderableReference(
            FormId: placement.FormId,
            WorldMatrix: world,
            ModelPath: placement.ModelPath!,
            BoundsCenter: center,
            BoundsRadius: radius,
            MeshId: ComputeMeshId(placement.ModelPath!),
            IsInitiallyDisabled: placement.IsInitiallyDisabled);
    }

    /// <summary>
    ///     The composed model-to-world matrix. The engine's REFR orientation is decompiled from the
    ///     Xbox 360 MemDebug XEX (tools/GhidraProject/refr_rotation_decompiled.txt):
    ///     <list type="bullet">
    ///       <item>
    ///         <c>TESObjectREFR::GetOrientation</c> (VA 0x823A3738) calls
    ///         <c>NiMatrix3::FromEulerAnglesXYZ(rotX, rotY, rotZ)</c> (VA 0x82E20B38).
    ///       </item>
    ///       <item>
    ///         <c>FromEulerAnglesXYZ</c> builds <c>M = Rx · (Ry · Rz)</c> — column-vector
    ///         standard right-handed rotations (each <c>Make*Rotation</c> is the textbook RH
    ///         matrix, e.g. <c>MakeZRotation = [c,-s,0; s,c,0; 0,0,1]</c>).
    ///       </item>
    ///     </list>
    ///     On-screen the rotation is <c>W = Rx(RotX)·Ry(RotY)·Rz(−RotZ)</c> — the engine matrix with
    ///     the YAW angle negated (the heading is the opposite hand from this renderer's world Z;
    ///     pitch/roll are unchanged). See <see cref="ComposeWorldMatrix" /> for the empirical
    ///     derivation (plain <c>M</c> = yaw wrong; full transpose <c>Mᵀ</c> = pitch/roll wrong;
    ///     negating RotZ alone satisfies both). Pinned by <c>EngineRotationConventionTests</c>.
    /// </summary>
    // Live ground-truth diagnostic. Set FALLOUT_VIEWER_DUMP_REFR=<substring> (matched against the
    // ModelPath, e.g. "road" or "dome") to append, for every matching placed object the live viewer
    // loads, the parsed rotation AND the world-space bearing its local +X/+Y axes end up pointing —
    // so we can compare the GUI's actual computed orientation against the engine/data. Output:
    // %TEMP%\fallout_refr_dump.txt. Capped to avoid runaway writes.
    private static readonly string? DumpFilter =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.DumpReference);
    private static readonly object DumpLock = new();
    private static int _dumpCount;

    private static void DumpRefr(PlacedReference p, Matrix4x4 world)
    {
        lock (DumpLock)
        {
            if (_dumpCount >= 800) return;
            _dumpCount++;
            var origin = Vector3.Transform(Vector3.Zero, world);
            var lx = Vector3.Transform(Vector3.UnitX, world) - origin;
            var ly = Vector3.Transform(Vector3.UnitY, world) - origin;
            var lz = Vector3.Transform(Vector3.UnitZ, world) - origin;
            static float Bearing(Vector3 v) => MathF.Atan2(v.Y, v.X) * 180f / MathF.PI;
            var line =
                $"0x{p.FormId:X8} '{p.ModelPath}' pos=({p.X:F0},{p.Y:F0},{p.Z:F0}) " +
                $"rotZdeg={p.RotZ * 180f / MathF.PI:F1} rotX={p.RotX:F3} rotY={p.RotY:F3} scale={p.Scale:F2} " +
                $"|+X->bearing {Bearing(lx):F1} (z {lx.Z:F2}) |+Y->bearing {Bearing(ly):F1} (z {ly.Z:F2}) " +
                $"|+Z->({lz.X:F2},{lz.Y:F2},{lz.Z:F2})";
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "fallout_refr_dump.txt"), line + Environment.NewLine);
            }
            catch
            {
                // Diagnostic only — never let a logging failure break rendering.
            }
        }
    }

    private static Matrix4x4 ComposeWorldMatrix(PlacedReference p)
    {
        var scale = p.Scale > 0f ? p.Scale : 1f;
        // The engine BUILDS its orientation as M = Rx·Ry·Rz (NiMatrix3::FromEulerAnglesXYZ,
        // VA 0x82E20B38 — decompile-proven), with standard right-handed column-vector per-axis
        // builders. The ONLY discrepancy vs the on-screen result is the sign of the YAW angle
        // (RotZ): the engine's heading is the opposite hand from this renderer's world Z, so the
        // correct on-screen rotation is W = Rx(RotX)·Ry(RotY)·Rz(−RotZ). Pitch (RotX) and roll
        // (RotY) are kept as built.
        //
        // This was pinned by two empirical states, which together admit only this solution:
        //   • plain M           → yaw WRONG, pitch/roll right   (Rz(+c) is the wrong-hand heading)
        //   • full transpose Mᵀ → yaw right, pitch/roll WRONG   (inverting ALL axes, the pipes
        //                                                          in Lucky38World)
        // Mᵀ only appeared to fix yaw because for a pure-yaw object Mᵀ = Rz(−c) = W; for a
        // pitched/rolled object Mᵀ also flips pitch/roll. Negating RotZ alone fixes the heading
        // without disturbing pitch/roll.
        //
        // System.Numerics is row-vector (v·A), and Vector3.Transform(v, CreateRotationZ(θ)) ==
        // MakeZRotation(θ)·v. So CreateRotationZ(−RotZ)·CreateRotationY(RotY)·CreateRotationX(RotX)
        // evaluates under Vector3.Transform to Rx·Ry·Rz(−c)·v = W·v — exactly the engine matrix
        // with the yaw angle negated. No transpose.
        var rotation =
            Matrix4x4.CreateRotationZ(-p.RotZ)
            * Matrix4x4.CreateRotationY(p.RotY)
            * Matrix4x4.CreateRotationX(p.RotX);
        return Matrix4x4.CreateScale(scale)
             * rotation
             * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
    }

    /// <summary>
    ///     World-space bounding sphere from the base record's OBND, conservatively wrapped
    ///     around the rotated AABB. Falls back to a fixed-radius sphere when OBND is absent
    ///     (some MSTT / runtime-only refs). Computed once at LoadData so the per-frame cull
    ///     just does a <c>(centerWorld - cameraXY).LengthSq &lt; (radius + cylinderRadius)^2</c>.
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
