using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

internal enum PhysicsLiteSwaySkipReason
{
    None,
    AtRest,
    UnsupportedLayout,
    OrdinaryAnimation,
    UnsupportedConstraint,
    MotorizedConstraint,
    NoDrivenSubtree,
    InvalidAuthoredFrame,
    InvalidAuthoredLimits,
    InvalidTime,
}

internal readonly record struct PhysicsLiteSwaySample(
    Matrix4x4 Transform,
    float AngleRadians,
    bool Applied,
    PhysicsLiteSwaySkipReason SkipReason);

/// <summary>
///     Cache-safe, phase-independent FNV sway data for one routed visual submesh. Pivot and axis are
///     in the same scene-root-local frame as the extractor's baked vertices. A placed-reference seed
///     is supplied only when the frame matrix is copied, so repeated instances keep batching while
///     still moving out of phase.
/// </summary>
internal readonly record struct PhysicsLiteSwayDescriptor(
    int ConstraintBlockIndex,
    Vector3 Pivot,
    Vector3 Axis,
    float MinimumAngle,
    float MaximumAngle,
    float AmplitudeFraction,
    float CyclesPerSecond)
{
    internal PhysicsLiteSwaySample Evaluate(
        double elapsedSeconds,
        uint placedReferenceSeed,
        bool isAtRest = false) =>
        PhysicsLiteSway.Evaluate(
            Pivot,
            Axis,
            MinimumAngle,
            MaximumAngle,
            AmplitudeFraction,
            CyclesPerSecond,
            PhysicsLiteSway.StablePhase(
                PhysicsLiteSway.CombineStableSeed(placedReferenceSeed, ConstraintBlockIndex)),
            elapsedSeconds,
            isAtRest);
}

/// <summary>
///     Immutable one-axis pendulum plan. It is intentionally a deterministic visual approximation,
///     not a Havok solver: it never mutates physics state and can be shared across frames.
/// </summary>
internal sealed class PhysicsLiteSwayPlan
{
    private readonly Vector3 _pivot;
    private readonly Vector3 _axis;
    private readonly float _minimumAngle;
    private readonly float _maximumAngle;
    private readonly float _amplitudeFraction;
    private readonly float _phase;
    private readonly float _cyclesPerSecond;

    internal PhysicsLiteSwayPlan(
        PhysicsLiteSwaySkipReason skipReason,
        int constraintBlockIndex = -1,
        int drivenBodyBlockIndex = -1,
        int targetNodeBlockIndex = -1,
        IReadOnlyList<int>? targetSubtree = null,
        Vector3 pivot = default,
        Vector3 axis = default,
        float minimumAngle = 0f,
        float maximumAngle = 0f,
        float amplitudeFraction = 0f,
        float cyclesPerSecond = 0f,
        float phase = 0f)
    {
        SkipReason = skipReason;
        ConstraintBlockIndex = constraintBlockIndex;
        DrivenBodyBlockIndex = drivenBodyBlockIndex;
        TargetNodeBlockIndex = targetNodeBlockIndex;
        TargetSubtree = targetSubtree ?? Array.Empty<int>();
        _pivot = pivot;
        _axis = axis;
        _minimumAngle = minimumAngle;
        _maximumAngle = maximumAngle;
        _amplitudeFraction = amplitudeFraction;
        _cyclesPerSecond = cyclesPerSecond;
        _phase = phase;
    }

    internal PhysicsLiteSwaySkipReason SkipReason { get; }
    internal bool IsSupported => SkipReason == PhysicsLiteSwaySkipReason.None;
    internal int ConstraintBlockIndex { get; }
    internal int DrivenBodyBlockIndex { get; }
    internal int TargetNodeBlockIndex { get; }
    internal IReadOnlyList<int> TargetSubtree { get; }
    internal float MinimumAngle => _minimumAngle;
    internal float MaximumAngle => _maximumAngle;
    internal PhysicsLiteSwayDescriptor? Descriptor => IsSupported
        ? new PhysicsLiteSwayDescriptor(
            ConstraintBlockIndex,
            _pivot,
            _axis,
            _minimumAngle,
            _maximumAngle,
            _amplitudeFraction,
            _cyclesPerSecond)
        : null;

    internal PhysicsLiteSwaySample Evaluate(double elapsedSeconds, bool isAtRest = false)
    {
        if (!IsSupported)
        {
            return Identity(SkipReason);
        }

        return PhysicsLiteSway.Evaluate(
            _pivot,
            _axis,
            _minimumAngle,
            _maximumAngle,
            _amplitudeFraction,
            _cyclesPerSecond,
            _phase,
            elapsedSeconds,
            isAtRest);
    }

    private static PhysicsLiteSwaySample Identity(PhysicsLiteSwaySkipReason reason) =>
        new(Matrix4x4.Identity, 0f, false, reason);
}

/// <summary>
///     Converts decoded FNV angular constraints into deterministic, authored-limit-safe pendulum
///     transforms. Ordinary keyframe/controller animation always wins; unlimited hinges, motors,
///     unresolved ownership, and malformed frames explicitly remain at identity.
/// </summary>
internal static class PhysicsLiteSway
{
    private const float MinimumAxisLengthSquared = 1e-8f;

    internal static PhysicsLiteSwayPlan CreatePlan(
        FnvHavokConstraintSet constraintSet,
        FnvHavokAngularConstraint constraint,
        uint stableSeed,
        float amplitudeFraction = 0.35f,
        float cyclesPerSecond = 0.18f)
    {
        ArgumentNullException.ThrowIfNull(constraintSet);
        ArgumentNullException.ThrowIfNull(constraint);

        if (!constraintSet.IsSupportedLayout)
        {
            return Skipped(PhysicsLiteSwaySkipReason.UnsupportedLayout);
        }

        if (constraintSet.HasOrdinaryTransformAnimation)
        {
            return Skipped(PhysicsLiteSwaySkipReason.OrdinaryAnimation);
        }

        if (constraint.MotorType != 0)
        {
            return Skipped(PhysicsLiteSwaySkipReason.MotorizedConstraint);
        }

        var driven = constraint.DrivenEntity;
        if (driven?.TargetNodeBlockIndex is not int targetNode || driven.TargetSubtree.Count == 0)
        {
            return Skipped(PhysicsLiteSwaySkipReason.NoDrivenSubtree);
        }

        if (driven.BodyToRootTransform is not { } bodyToRoot)
        {
            return Skipped(PhysicsLiteSwaySkipReason.InvalidAuthoredFrame);
        }

        Vector3 pivot;
        Vector3 axis;
        float minimumAngle;
        float maximumAngle;

        switch (constraint.Kind)
        {
            case FnvHavokAngularConstraintKind.LimitedHinge:
            {
                var frame = SelectHingeFrame(constraint);
                if (frame is null || constraint.MinimumAngle is not float min ||
                    constraint.MaximumAngle is not float max)
                {
                    return Skipped(PhysicsLiteSwaySkipReason.InvalidAuthoredFrame);
                }

                pivot = Vector3.Transform(frame.Value.Pivot, bodyToRoot);
                axis = Vector3.TransformNormal(frame.Value.Axis, bodyToRoot);
                minimumAngle = min;
                maximumAngle = max;
                break;
            }
            case FnvHavokAngularConstraintKind.Ragdoll:
            {
                var frame = SelectRagdollFrame(constraint);
                if (frame is null || constraint.RagdollLimits is not { } limits)
                {
                    return Skipped(PhysicsLiteSwaySkipReason.InvalidAuthoredFrame);
                }

                // The approximation rotates only around Plane A/B. Intersect the authored plane
                // interval with the symmetric cone and require zero twist to be legal; this keeps
                // all other ragdoll degrees of freedom at their authored zero pose.
                if (limits.TwistMinAngle > 0f || limits.TwistMaxAngle < 0f)
                {
                    return Skipped(PhysicsLiteSwaySkipReason.InvalidAuthoredLimits);
                }

                pivot = Vector3.Transform(frame.Value.Pivot, bodyToRoot);
                axis = Vector3.TransformNormal(frame.Value.PlaneAxis, bodyToRoot);
                minimumAngle = Math.Max(limits.PlaneMinAngle, -limits.ConeMaxAngle);
                maximumAngle = Math.Min(limits.PlaneMaxAngle, limits.ConeMaxAngle);
                break;
            }
            default:
                // bhkHingeConstraint is intentionally decoded, but it has no authored angular
                // bounds. A bounded visual approximation cannot claim to honor limits that do not
                // exist, so it remains identity until a renderer opts into a separate policy.
                return Skipped(PhysicsLiteSwaySkipReason.UnsupportedConstraint);
        }

        if (!IsFinite(pivot) || !IsFinite(axis) || axis.LengthSquared() < MinimumAxisLengthSquared)
        {
            return Skipped(PhysicsLiteSwaySkipReason.InvalidAuthoredFrame);
        }

        if (!float.IsFinite(minimumAngle) || !float.IsFinite(maximumAngle) ||
            minimumAngle > maximumAngle ||
            !float.IsFinite(amplitudeFraction) || amplitudeFraction <= 0f ||
            !float.IsFinite(cyclesPerSecond) || cyclesPerSecond <= 0f)
        {
            return Skipped(PhysicsLiteSwaySkipReason.InvalidAuthoredLimits);
        }

        var normalizedAxis = Vector3.Normalize(axis);
        var boundedAmplitude = Math.Clamp(amplitudeFraction, 0f, 1f);
        return new PhysicsLiteSwayPlan(
            PhysicsLiteSwaySkipReason.None,
            constraint.BlockIndex,
            driven.BodyBlockIndex,
            targetNode,
            driven.TargetSubtree,
            pivot,
            normalizedAxis,
            minimumAngle,
            maximumAngle,
            boundedAmplitude,
            cyclesPerSecond,
            StablePhase(stableSeed));
    }

    /// <summary>
    ///     Resolves source-block routing once per decoded NIF. If two supported constraints claim
    ///     the same block, that block is deliberately omitted: composing an unverified joint chain
    ///     would be less faithful than leaving the visual at rest.
    /// </summary>
    internal static IReadOnlyDictionary<int, PhysicsLiteSwayDescriptor> BuildSourceBlockRoutes(
        FnvHavokConstraintSet constraintSet)
    {
        ArgumentNullException.ThrowIfNull(constraintSet);

        var routes = new Dictionary<int, PhysicsLiteSwayDescriptor>();
        var ambiguous = new HashSet<int>();
        foreach (var constraint in constraintSet.Constraints)
        {
            var plan = CreatePlan(constraintSet, constraint, stableSeed: 0);
            if (plan.Descriptor is not { } descriptor)
            {
                continue;
            }

            foreach (var sourceBlockIndex in plan.TargetSubtree)
            {
                if (ambiguous.Contains(sourceBlockIndex))
                {
                    continue;
                }

                if (routes.TryAdd(sourceBlockIndex, descriptor))
                {
                    continue;
                }

                routes.Remove(sourceBlockIndex);
                ambiguous.Add(sourceBlockIndex);
            }
        }

        return routes;
    }

    internal static PhysicsLiteSwaySample Evaluate(
        Vector3 pivot,
        Vector3 axis,
        float minimumAngle,
        float maximumAngle,
        float amplitudeFraction,
        float cyclesPerSecond,
        float phase,
        double elapsedSeconds,
        bool isAtRest)
    {
        if (isAtRest)
        {
            return new PhysicsLiteSwaySample(
                Matrix4x4.Identity, 0f, false, PhysicsLiteSwaySkipReason.AtRest);
        }

        if (!double.IsFinite(elapsedSeconds))
        {
            return new PhysicsLiteSwaySample(
                Matrix4x4.Identity, 0f, false, PhysicsLiteSwaySkipReason.InvalidTime);
        }

        var oscillationPhase = elapsedSeconds * cyclesPerSecond * MathF.Tau + phase;
        if (!double.IsFinite(oscillationPhase))
        {
            return new PhysicsLiteSwaySample(
                Matrix4x4.Identity, 0f, false, PhysicsLiteSwaySkipReason.InvalidTime);
        }

        var neutral = Math.Clamp(0f, minimumAngle, maximumAngle);
        var wave = (float)Math.Sin(oscillationPhase);
        var angle = wave >= 0f
            ? neutral + wave * (maximumAngle - neutral) * amplitudeFraction
            : neutral + wave * (neutral - minimumAngle) * amplitudeFraction;
        angle = Math.Clamp(angle, minimumAngle, maximumAngle);

        // System.Numerics uses row-vector composition: translate to the pivot origin, rotate, then
        // translate back. The root-local authored pivot therefore remains fixed before placement.
        var transform = Matrix4x4.CreateTranslation(-pivot) *
                        Matrix4x4.CreateFromAxisAngle(axis, angle) *
                        Matrix4x4.CreateTranslation(pivot);
        return new PhysicsLiteSwaySample(transform, angle, true, PhysicsLiteSwaySkipReason.None);
    }

    private static FnvHavokHingeFrame? SelectHingeFrame(FnvHavokAngularConstraint constraint)
    {
        if (constraint.DrivenBodyBlockIndex == constraint.EntityABlockIndex)
        {
            return constraint.HingeFrameA;
        }

        return constraint.DrivenBodyBlockIndex == constraint.EntityBBlockIndex
            ? constraint.HingeFrameB
            : null;
    }

    private static FnvHavokRagdollFrame? SelectRagdollFrame(FnvHavokAngularConstraint constraint)
    {
        if (constraint.DrivenBodyBlockIndex == constraint.EntityABlockIndex)
        {
            return constraint.RagdollFrameA;
        }

        return constraint.DrivenBodyBlockIndex == constraint.EntityBBlockIndex
            ? constraint.RagdollFrameB
            : null;
    }

    private static PhysicsLiteSwayPlan Skipped(PhysicsLiteSwaySkipReason reason) => new(reason);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    internal static uint CombineStableSeed(uint placedReferenceSeed, int constraintBlockIndex)
    {
        unchecked
        {
            return placedReferenceSeed ^ ((uint)constraintBlockIndex * 0x9E3779B9u);
        }
    }

    internal static float StablePhase(uint seed)
    {
        // Fixed integer avalanche: identical placed reference/constraint seeds produce identical
        // motion on every run and platform without retaining mutable simulation state.
        var hash = seed;
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;
        return (hash & 0x00FFFFFFu) * (MathF.Tau / 0x01000000u);
    }
}
