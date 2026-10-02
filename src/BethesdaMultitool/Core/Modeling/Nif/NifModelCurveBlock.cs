namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Why a NIF animation source (a key group, a B-spline channel, a controller or sequence clock) cannot be mapped onto
///     the Shared G13 animation vocabulary (cut-1b slice 2). Every reason is fail closed: the source stays native state and
///     nothing is approximated, resampled or substituted.
/// </summary>
/// <remarks>
///     Since slices 13 and 16b every rotation key type maps (XYZ_ROTATION through the Euler form, TBC and QUADRATIC
///     quaternions through the Squad form), so the former 'pending a Shared form' reasons are gone; what remains for
///     those kinds are the engine-behavior guards below (RE-20 rules 2 and 7, RE-24).
/// </remarks>
internal enum NifModelCurveBlock
{
    /// <summary>Not blocked: the source mapped, or held nothing to map.</summary>
    None = 0,

    /// <summary>
    ///     The stored key type is not one the mapping knows (anything but LINEAR 1, QUADRATIC 2, TBC 3 and CONST 5 for a
    ///     component group, or 1 to 5 for a rotation). Fail closed rather than guess a stride or an evaluator.
    /// </summary>
    UnknownKeyType,

    /// <summary>
    ///     An XYZ_ROTATION (key type 4) rotation whose stored record count is not 1: the engine evaluates record 0 only,
    ///     and retail stores one record on all 70,104 blocks, so the other count fails closed (RE-20 rule 2).
    /// </summary>
    EulerRecordCount,

    /// <summary>An XYZ_ROTATION axis whose key type is outside LINEAR, QUADRATIC, TBC and CONST (RE-20 rule 2).</summary>
    EulerAxisKeyType,

    /// <summary>
    ///     An XYZ_ROTATION channel whose effective clock could sample a time below the first key of a multi-key axis:
    ///     there the engine extrapolates the first segment and Shared holds the first key (RE-20 rule 7). Never reached
    ///     at retail clock starts (start equals the first key on all 210,294 axis and driver pairs).
    /// </summary>
    EulerSampledBeforeFirstKey,

    /// <summary>
    ///     A TBC or QUADRATIC quaternion group with two keys at one time: the engine's inner point is NaN (CalculateDVals
    ///     divides by the span) and GenInterp's u is undefined (RE-24). Retail has none.
    /// </summary>
    SquadZeroLengthSpan,

    /// <summary>
    ///     A TBC or QUADRATIC quaternion group in a file shipped for the PlayStation 3: RE-24 measured the GECK (PC) and
    ///     the X360 build and examined no PS3 binary, so no Shared normalization policy is established for it.
    /// </summary>
    SquadPolicyPs3,

    /// <summary>
    ///     A rotation key, or a Squad inner point derived from the keys, that is the zero quaternion, which Shared refuses
    ///     (a Squad track retains raw amplitudes and cannot normalize it away).
    /// </summary>
    ZeroQuaternion,

    /// <summary>A key time is not finite, or the key times are not strictly increasing (Shared's key rule).</summary>
    InvalidKeyTimes,

    /// <summary>A key value, tangent, TBC parameter, endpoint tangent or control point is not finite.</summary>
    NonFiniteValue,

    /// <summary>
    ///     A rotation key is still outside Shared's Float32 unit tolerance after RE-17's exact normalization (never seen on
    ///     retail data; reachable only through extreme stored magnitudes).
    /// </summary>
    NonUnitRotation,

    /// <summary>A B-spline channel has a handle but its NiBSplineData or NiBSplineBasisData was not supplied.</summary>
    BsplineMissingData,

    /// <summary>
    ///     The NiBSplineBasisData control-point count is below the degree-3 minimum of four, or above Shared's
    ///     <c>SceneBSplineCurve.MaximumControlPointCount</c>.
    /// </summary>
    BsplineControlPointCount,

    /// <summary>The B-spline start or stop time is not finite, or the stop is not after the start.</summary>
    BsplineInvalidInterval,

    /// <summary>The channel's controls (handle plus count times width) run past the NiBSplineData array.</summary>
    BsplineControlsOutOfRange,

    /// <summary>
    ///     A compact channel's Half Range (the multiplier) is negative: BMT's existing admission rule
    ///     (NifBsplineTransformReader), which the plan keeps. Every retail half range is positive.
    /// </summary>
    BsplineNegativeHalfRange,

    /// <summary>
    ///     A free-running controller whose active bit (flags 0x08) is clear (RE-22 rule 3, NativeOnly 'inactive
    ///     controller'): the engine updates it only for consumers that call StartAnimations, which the file cannot reveal.
    /// </summary>
    InactiveController,

    /// <summary>
    ///     The NiControllerManager or NiMultiTargetTransformController that drives a sequence has its active bit clear
    ///     (RE-22 rule 1 exception): none of the sequences it drives play.
    /// </summary>
    InactiveManager,

    /// <summary>
    ///     A free-running controller with the double sentinel clock (start +FLT_MAX, stop -FLT_MAX) that carries a curve
    ///     (RE-22 rule 3a, NativeOnly 'sentinel clock with curve').
    /// </summary>
    SentinelClockWithCurve,

    /// <summary>
    ///     A clock whose engine output is degenerate (RE-22 rule 3b): exactly one sentinel, stop before start, the
    ///     undefined cycle 3, or a non-finite frequency, phase, start or stop.
    /// </summary>
    DegenerateClock
}
