using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     RE-22's implementation rule (docs/formats/nif-animation-engine-behavior-20260925.md) for NIF 20.2.0.7: the clip
///     clock of an NiControllerSequence and the track clock of a controller, onto Shared's
///     <see cref="SceneAnimationClock" />. Pure.
/// </summary>
/// <remarks>
///     <list type="number">
///         <item>
///             A controller is sequence-driven when its flags carry 0x20 or a controlled block references it. It gets no
///             track clock (the sequence clock replaces its clock; it does not compose), and its tracks are not gated on
///             its active bit. Its frequency, phase, start, stop, cycle, anim type and active bit stay native.
///         </item>
///         <item>
///             A sequence clip clock is <c>SceneAnimationClock(frequency, 0, start, stop, cycle == 0 ? Loop : Clamp)</c>:
///             a sequence has no phase and no REVERSE branch, so REVERSE (and every other nonzero cycle word) plays as
///             Clamp. An inactive NiControllerManager or NiMultiTargetTransformController plays none of its sequences.
///         </item>
///         <item>
///             A free-running controller with its active bit (0x08) clear is blocked. The double sentinel clock (start
///             +FLT_MAX, stop -FLT_MAX) has no clock, and is blocked when it carries a curve. Exactly one sentinel, stop
///             before start, or cycle 3 is blocked. Otherwise the clock is (frequency, phase, start, stop, cycle), with
///             Reverse using phase + start. Play backwards (0x10): Loop and Clamp use -frequency and
///             start + stop - phase; Reverse uses phase + stop.
///         </item>
///     </list>
///     <para>
///         Every tau maps as the engine does within 2e-6 s, except at exact wrap instants (a sequence Loop returns stop at
///         its first wrap and a backwards Loop controller returns stop at start + kL, where Shared returns start). The
///         engine's per-thread scaled-time cache, which ignores bit 4, is not reproduced. Flags bit 0 (APP_TIME or
///         APP_INIT) stays native: both reduce to frequency * tau + phase for a clip that starts at tau = 0. The shifted
///         phases are formed in double and rounded to Float32 once.
///     </para>
/// </remarks>
internal static class NifModelClockMapping
{
    /// <summary>+FLT_MAX, the start-time sentinel of an unset controller clock.</summary>
    public const uint StartSentinelBits = 0x7F7FFFFF;

    /// <summary>-FLT_MAX, the stop-time sentinel of an unset controller clock.</summary>
    public const uint StopSentinelBits = 0xFF7FFFFF;

    /// <summary>The cycle value the engine does not define (flags bits 1-2 equal to 3).</summary>
    public const int UndefinedCycle = 3;

    /// <summary>
    ///     RE-22 rule 1: whether a controller is sequence-driven (flags 0x20, or referenced by a controlled block's
    ///     Controller ref; in the corpus the second test adds nothing to the first).
    /// </summary>
    /// <param name="header">The controller's NiTimeController header.</param>
    /// <param name="referencedByControlledBlock">True when any controlled block in the file references the controller.</param>
    /// <returns>True when the controller's tracks play only inside sequence clips.</returns>
    public static bool IsSequenceDriven(in NifTimeControllerHeader header, bool referencedByControlledBlock)
    {
        return header.IsManagerControlled || referencedByControlledBlock;
    }

    /// <summary>RE-22 rule 2: the clip clock of one NiControllerSequence.</summary>
    /// <param name="sequence">The sequence view.</param>
    /// <param name="driversActive">
    ///     False when the NiControllerManager that owns the sequence, or the NiMultiTargetTransformController that binds
    ///     it, has its active bit (0x08) clear; true otherwise, including a .kf, which stores neither.
    /// </param>
    /// <returns>The clip clock, or <see cref="NifModelCurveBlock.InactiveManager" /> or <see cref="NifModelCurveBlock.DegenerateClock" />.</returns>
    public static NifModelClockResult MapSequence(NifControllerSequenceView sequence, bool driversActive)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (!driversActive)
        {
            return NifModelClockResult.Blocked(NifModelCurveBlock.InactiveManager);
        }

        var frequency = BitConverter.UInt32BitsToSingle(sequence.FrequencyBits);
        var start = BitConverter.UInt32BitsToSingle(sequence.StartTimeBits);
        var stop = BitConverter.UInt32BitsToSingle(sequence.StopTimeBits);
        if (!float.IsFinite(frequency) || !float.IsFinite(start) || !float.IsFinite(stop) || stop < start)
        {
            return NifModelClockResult.Blocked(NifModelCurveBlock.DegenerateClock);
        }

        var cycle = sequence.RawCycle == 0 ? SceneAnimationCycle.Loop : SceneAnimationCycle.Clamp;
        return NifModelClockResult.Mapped(new SceneAnimationClock(frequency, 0f, start, stop, cycle));
    }

    /// <summary>
    ///     RE-22 rules 1 and 3: a controller's track clock. A sequence-driven controller gets none and is never blocked
    ///     here; a free-running one follows <see cref="MapFreeRunning" />.
    /// </summary>
    /// <param name="header">The controller's NiTimeController header.</param>
    /// <param name="referencedByControlledBlock">True when any controlled block in the file references the controller.</param>
    /// <param name="hasCurve">True when the controller's interpolator carries keys or a B-spline curve.</param>
    /// <returns>The track clock, no clock, or the blocked reason.</returns>
    public static NifModelClockResult MapController(
        in NifTimeControllerHeader header, bool referencedByControlledBlock, bool hasCurve)
    {
        return IsSequenceDriven(header, referencedByControlledBlock)
            ? NifModelClockResult.NoClock
            : MapFreeRunning(header, hasCurve);
    }

    /// <summary>RE-22 rule 3: the track clock of a free-running (not sequence-driven) controller.</summary>
    /// <param name="header">The controller's NiTimeController header.</param>
    /// <param name="hasCurve">True when the controller's interpolator carries keys or a B-spline curve.</param>
    /// <returns>The track clock, no clock (double sentinel without a curve), or the blocked reason.</returns>
    public static NifModelClockResult MapFreeRunning(in NifTimeControllerHeader header, bool hasCurve)
    {
        if (!header.IsActive)
        {
            return NifModelClockResult.Blocked(NifModelCurveBlock.InactiveController);
        }

        var startSentinel = header.StartTimeBits == StartSentinelBits;
        var stopSentinel = header.StopTimeBits == StopSentinelBits;
        if (startSentinel && stopSentinel)
        {
            return hasCurve
                ? NifModelClockResult.Blocked(NifModelCurveBlock.SentinelClockWithCurve)
                : NifModelClockResult.NoClock;
        }

        var start = header.StartTime;
        var stop = header.StopTime;
        var frequency = header.Frequency;
        var phase = header.Phase;
        if (startSentinel || stopSentinel || header.RawCycle == UndefinedCycle ||
            !float.IsFinite(frequency) || !float.IsFinite(phase) || !float.IsFinite(start) || !float.IsFinite(stop) ||
            stop < start)
        {
            return NifModelClockResult.Blocked(NifModelCurveBlock.DegenerateClock);
        }

        var cycle = header.RawCycle switch
        {
            0 => SceneAnimationCycle.Loop,
            1 => SceneAnimationCycle.Reverse,
            _ => SceneAnimationCycle.Clamp
        };
        if (cycle == SceneAnimationCycle.Reverse)
        {
            // The engine's REVERSE takes fmod(s, 2L) anchored at 0, not at start; played backwards it is the same
            // triangle wave shifted by one interval.
            phase = (float)((double)phase + (header.PlayBackwards ? stop : start));
        }
        else if (header.PlayBackwards)
        {
            frequency = -frequency;
            phase = (float)((double)start + stop - phase);
        }

        if (!float.IsFinite(phase))
        {
            return NifModelClockResult.Blocked(NifModelCurveBlock.DegenerateClock);
        }

        return NifModelClockResult.Mapped(new SceneAnimationClock(frequency, phase, start, stop, cycle));
    }
}
