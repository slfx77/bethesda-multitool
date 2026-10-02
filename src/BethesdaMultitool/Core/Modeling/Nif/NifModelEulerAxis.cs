using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One axis of an XYZ-Euler rotation (RE-20) as <see cref="NifModelCurveMapping.MapEulerRotation" /> maps it: the
///     axis's own stored key type and, when it stores keys, its scalar curve through the slice-2 float mapping (RE-18
///     Hermite and RE-19 TBC rules included). An axis with no keys is angle 0 at every time (RE-20 rule 3).
/// </summary>
/// <param name="KeyType">The axis's stored key type (1 LINEAR, 2 QUADRATIC, 3 TBC, 5 CONST), or 0 when it stores no keys.</param>
/// <param name="Curve">The width-1 curve of the stored keys; null when the axis stores none.</param>
internal sealed record NifModelEulerAxis(uint KeyType, NifModelCurve? Curve)
{
    /// <summary>An axis with no keys: angle 0 (RE-20 rule 3).</summary>
    public static NifModelEulerAxis Empty { get; } = new(0, null);

    /// <summary>True when the axis stores no keys.</summary>
    public bool IsEmpty => Curve is null;

    /// <summary>The number of stored keys (0 for an empty axis).</summary>
    public int KeyCount => Curve?.Times.Length ?? 0;

    /// <summary>The time of the first stored key, or null for an empty axis.</summary>
    public float? FirstKeyTime => Curve is { } curve ? curve.Times[0] : null;

    /// <summary>
    ///     The Shared curve: a Constant curve with static value [0] for an empty axis (RE-20 rule 3), otherwise a Keyed
    ///     curve holding the stored keys exactly, one key included (rule 4: Shared holds a single key, as the engine
    ///     returns it at every time), with the axis's interpolation, TBC parameters and RE-19 endpoints.
    /// </summary>
    /// <returns>A new curve over the owned arrays (Shared copies them).</returns>
    public SceneCurve ToCurve()
    {
        if (Curve is not { } curve)
        {
            return new SceneCurve(1, [], [], SceneInterpolation.Linear, SceneAnimationChannelState.Constant, [0f]);
        }

        return new SceneCurve(1, curve.Times, curve.Values, curve.Interpolation, SceneAnimationChannelState.Keyed,
            null, null, curve.TbcParameters, curve.TbcEndpoints);
    }
}
