using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

internal enum OpaqueFrontToBackFallbackReason
{
    None = 0,
    InvalidView = 1,
    InvalidBounds = 2,
    NoEligibleBatches = 3,
}

/// <summary>
///     One immutable camera snapshot for a staged opaque-batch build. The ordering is only a
///     publication-time performance hint, so later tolerant camera drift does not invalidate it.
/// </summary>
internal readonly record struct OpaqueFrontToBackBuildView(
    bool Requested,
    bool Valid,
    Vector3 Eye,
    Vector3 Forward,
    OpaqueFrontToBackFallbackReason FallbackReason)
{
    internal OpaqueFrontToBackBuildView Fail(OpaqueFrontToBackFallbackReason reason) =>
        this with { Valid = false, FallbackReason = reason };
}

/// <summary>
///     Fail-closed depth-key math for the optional within-PSO opaque publication order.
/// </summary>
internal static class OpaqueFrontToBackPolicy
{
    internal static OpaqueFrontToBackBuildView CreateBuildView(
        bool requested,
        Vector3 eye,
        Vector3 forward)
    {
        if (!requested)
        {
            return new OpaqueFrontToBackBuildView(
                Requested: false,
                Valid: false,
                Eye: default,
                Forward: default,
                FallbackReason: OpaqueFrontToBackFallbackReason.None);
        }

        if (!IsFinite(eye) || !IsFinite(forward))
        {
            return InvalidView(eye);
        }

        // Accumulate in double so a malformed very-large float vector cannot overflow to infinity
        // before validation. Real camera forwards are unit length; the floor rejects zero and
        // denormal inputs rather than manufacturing an unstable sort direction.
        var lengthSquared = ((double)forward.X * forward.X)
                            + ((double)forward.Y * forward.Y)
                            + ((double)forward.Z * forward.Z);
        if (!double.IsFinite(lengthSquared) || lengthSquared < 1e-12)
        {
            return InvalidView(eye);
        }

        var inverseLength = 1.0 / Math.Sqrt(lengthSquared);
        var normalized = new Vector3(
            (float)(forward.X * inverseLength),
            (float)(forward.Y * inverseLength),
            (float)(forward.Z * inverseLength));
        if (!IsFinite(normalized))
        {
            return InvalidView(eye);
        }

        return new OpaqueFrontToBackBuildView(
            Requested: true,
            Valid: true,
            Eye: eye,
            Forward: normalized,
            FallbackReason: OpaqueFrontToBackFallbackReason.None);
    }

    /// <summary>
    ///     Returns the sphere's nearest projected view depth: dot(center-eye, forward)-radius.
    ///     Double intermediates preserve a finite verdict across the full finite-float range.
    /// </summary>
    internal static bool TryGetNearestViewDepth(
        in OpaqueFrontToBackBuildView view,
        in Vector4 bounds,
        out double depth)
    {
        depth = double.PositiveInfinity;
        if (!view.Requested || !view.Valid ||
            !float.IsFinite(bounds.X) ||
            !float.IsFinite(bounds.Y) ||
            !float.IsFinite(bounds.Z) ||
            !float.IsFinite(bounds.W) ||
            bounds.W < 0f)
        {
            return false;
        }

        depth = (((double)bounds.X - view.Eye.X) * view.Forward.X)
                + (((double)bounds.Y - view.Eye.Y) * view.Forward.Y)
                + (((double)bounds.Z - view.Eye.Z) * view.Forward.Z)
                - bounds.W;
        return double.IsFinite(depth);
    }

    private static OpaqueFrontToBackBuildView InvalidView(Vector3 eye) => new(
        Requested: true,
        Valid: false,
        Eye: eye,
        Forward: default,
        FallbackReason: OpaqueFrontToBackFallbackReason.InvalidView);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
