using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The three axes of an XYZ-Euler rotation (RE-20, cut-1b slice 13) as <see cref="NifModelCurveMapping.MapEulerRotation" />
///     maps record 0 of an NiTransformData or NiKeyframeData rotation part, ready to become Shared's
///     <see cref="SceneEulerRotationTrack" />: three independent scalar curves composed as
///     <see cref="SceneEulerOrder.Xyz" /> (q = qZ * qY * qX, RE-20 rule 9), angles in radians as stored (rule 8).
/// </summary>
/// <remarks>
///     <para>
///         The interpolator's static rotation is irrelevant to the engine's output when the data block stores an Euler
///         record (the engine evaluates the record even with every axis empty), so it is carried here for native state
///         only, never as a Shared static or fallback.
///     </para>
///     <para>
///         RE-20 rule 7: below an axis's first key the engine extrapolates the first segment while Shared holds the first
///         key, so a channel whose effective clock could sample a time below the first key of a multi-key axis is refused
///         (<see cref="SamplesBeforeFirstKey" />). A one-key axis holds its value at every time in both (rule 4) and an
///         empty axis is 0 (rule 3), so only axes with two or more keys take part. Retail never reaches the refusal: the
///         clock start equals each axis's first key on all 210,294 axis and driver pairs.
///     </para>
/// </remarks>
/// <param name="X">The X-axis angle.</param>
/// <param name="Y">The Y-axis angle.</param>
/// <param name="Z">The Z-axis angle.</param>
/// <param name="StaticRotation">
///     The interpolator's static rotation in Shared's layout (X, Y, Z, W) when it is not the sentinel; native state
///     only. Null when the static is the sentinel or has not been classified.
/// </param>
internal sealed record NifModelEulerRotation(
    NifModelEulerAxis X,
    NifModelEulerAxis Y,
    NifModelEulerAxis Z,
    float[]? StaticRotation)
{
    /// <summary>The composition order of every NIF Euler rotation (RE-20 rule 9).</summary>
    public const SceneEulerOrder Order = SceneEulerOrder.Xyz;

    /// <summary>The axes in file order X, Y, Z.</summary>
    public IEnumerable<NifModelEulerAxis> Axes
    {
        get
        {
            yield return X;
            yield return Y;
            yield return Z;
        }
    }

    /// <summary>
    ///     The earliest first-key time over the axes with two or more keys (the axes Shared and the engine can disagree
    ///     on below their first key); null when no axis has two or more keys.
    /// </summary>
    public float? EarliestExtrapolatingKey
    {
        get
        {
            float? earliest = null;
            foreach (var axis in Axes)
            {
                if (axis.KeyCount >= 2 && axis.FirstKeyTime is { } first && (earliest is null || first < earliest))
                {
                    earliest = first;
                }
            }

            return earliest;
        }
    }

    /// <summary>
    ///     RE-20 rule 7's guard: true when the effective clock could sample a time below the first key of a multi-key
    ///     axis, where the engine extrapolates and Shared holds. A Shared clock maps every input into [start, stop], so
    ///     its start is the earliest sample; without a clock the evaluator samples from 0.
    /// </summary>
    /// <param name="effectiveClock">The clip clock of a sequence track, the track clock of a free-running controller, or null.</param>
    /// <returns>True when the channel must stay native.</returns>
    public bool SamplesBeforeFirstKey(SceneAnimationClock? effectiveClock)
    {
        if (EarliestExtrapolatingKey is not { } first)
        {
            return false;
        }

        var earliest = effectiveClock?.StartSeconds ?? 0f;
        return earliest < first;
    }

    /// <summary>The Shared track on one exact node.</summary>
    /// <param name="nodeIndex">The exact target node.</param>
    /// <param name="clock">The track clock, or null (a sequence-driven or clock-less controller).</param>
    /// <param name="sourcePolicy">The controlled block's priority, or null.</param>
    /// <returns>The track; Shared validates it with its document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The node index is negative.</exception>
    public SceneEulerRotationTrack ToTrack(
        int nodeIndex, SceneAnimationClock? clock = null, SceneAnimationTrackSourcePolicy? sourcePolicy = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nodeIndex);
        return new SceneEulerRotationTrack(nodeIndex, Order, X.ToCurve(), Y.ToCurve(), Z.ToCurve(), clock,
            sourcePolicy);
    }
}
