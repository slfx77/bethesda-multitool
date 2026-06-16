using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     The single source of truth for how a placed reference's DATA rotation maps to a rendered
///     orientation, shared by BOTH the real-time 3D world viewer and the 2D top-down map. Keeping the
///     convention here means a change to it updates both views together.
///     <para>
///         <b>Engine truth (decompiled):</b> <c>TESObjectREFR::GetOrientation</c> builds
///         <c>M = Rx(RotX)·Ry(RotY)·Rz(RotZ)</c> from the raw DATA Euler angles and renders
///         <c>M·v</c>. This renderer's world frame is a chirality flip of the engine's, so it applies
///         <c>M(−θ)</c> — negate ALL THREE angles in the same build order. (Full derivation +
///         dead-ends in <c>refr_rotation_convention</c> memory / the unit tests
///         <c>EngineRotationConventionTests</c>.)
///     </para>
///     <para>
///         The 2D map draws on a Y-flipped canvas (screen = (X, −Y)). The flip negates the apparent
///         yaw a second time, so the 3D world's −RotZ heading reproduces as <b>+RotZ</b> on the canvas
///         — the same convention, viewed through the flip. <see cref="MapCanvasYawRadians" /> is that
///         derivation, so the 2D footprint/hit-test code never re-encodes the sign locally.
///     </para>
/// </summary>
internal static class PlacedReferenceTransform
{
    /// <summary>
    ///     The model-to-world matrix for the 3D viewer: <c>Scale · Rx(−RotX)·Ry(−RotY)·Rz(−RotZ) ·
    ///     Translation</c> (System.Numerics row-vector form, evaluated under <c>Vector3.Transform</c>).
    ///     Note this is the REFR <i>placement</i> only; the mesh scene-root node's own transform is
    ///     handled (discarded) at bake time — see
    ///     <c>NifSceneGraphWalker.ComputeWorldTransforms(treatRootsAsIdentity)</c>.
    /// </summary>
    public static Matrix4x4 ComposeWorldMatrix(
        float x, float y, float z, float rotX, float rotY, float rotZ, float scale)
    {
        var s = scale > 0f ? scale : 1f;
        var rotation =
            Matrix4x4.CreateRotationZ(-rotZ)
            * Matrix4x4.CreateRotationY(-rotY)
            * Matrix4x4.CreateRotationX(-rotX);
        return Matrix4x4.CreateScale(s)
             * rotation
             * Matrix4x4.CreateTranslation(x, y, z);
    }

    /// <summary>
    ///     The 2D top-down map canvas yaw (radians) for an object footprint, derived from the same
    ///     convention as <see cref="ComposeWorldMatrix" /> (see type remarks for the Y-flip
    ///     reconciliation that makes this <c>+rotZ</c> rather than <c>−rotZ</c>).
    ///     <para>
    ///         <paramref name="rootNodeYawRadians" /> is the mesh scene-root node's Z rotation, which
    ///         the 3D bake discards (the placed mesh is the RAW geometry). The OBND that sizes the 2D
    ///         footprint is authored in root-applied space, so subtracting the root yaw re-aligns the
    ///         footprint with the raw-mesh orientation the 3D viewer now renders. It defaults to 0 —
    ///         correct for the overwhelming majority of meshes (identity root); wiring a real per-model
    ///         value requires NIF access the ESM-only map pipeline does not yet have.
    ///     </para>
    /// </summary>
    public static float MapCanvasYawRadians(float rotZ, float rootNodeYawRadians = 0f)
        => rotZ - rootNodeYawRadians;
}
