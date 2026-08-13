using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

/// <summary>What the endless-fall watchdog wants the walk controller to do this step.</summary>
internal enum WalkFallOutcome
{
    /// <summary>Keep integrating gravity — this is still an ordinary fall.</summary>
    Continue,

    /// <summary>Stop the fall and move the camera back to the returned last-known-good pose.</summary>
    Restore,

    /// <summary>Stop the fall where the camera is; no confirmed floor was ever recorded.</summary>
    Halt
}

/// <summary>
///     Deterministic recovery state for an explicit walk-mode jump that descends over an unknown
///     floor. Natural ledge falls are never armed and therefore never teleport. After recovery,
///     launching stays blocked until Space is released so a held key cannot create a jump/restore
///     oscillation.
///     <para>
///         Independently of the jump path, <see cref="TryObserveFallStep" /> is the floor of last resort
///         for ANY descent: clipping through geometry used to leave the camera falling forever because
///         only an armed jump could recover. The watchdog ends that fall and returns the last confirmed
///         floor pose — never a synthesized height.
///     </para>
/// </summary>
internal sealed class WalkVoidRecovery
{
    /// <summary>
    ///     Consecutive descending steps with no floor under the camera before the fall is declared
    ///     endless. ~1.5 s at 60 Hz: long enough that a legitimate drop through an unloaded seam
    ///     re-acquires ground first, short enough that the user is not left falling.
    /// </summary>
    public const int MaxGroundlessFallSteps = 90;

    /// <summary>
    ///     Drop below the last confirmed floor (world units, two exterior cells) that also ends the
    ///     fall. Covers a low frame rate, where the step budget alone would allow a huge descent.
    /// </summary>
    public const float MaxGroundlessFallDrop = 8192f;

    private Vector3? _lastGroundedPosition;
    private Vector3? _jumpAnchor;
    private bool _launchBlockedUntilRelease;
    private int _groundlessFallSteps;

    /// <summary>For tests/diagnostics: true only after a void recovery while jump remains held.</summary>
    internal bool LaunchBlockedUntilRelease => _launchBlockedUntilRelease;

    /// <summary>Clears all world-relative state on mode changes or when ground sampling disappears.</summary>
    public void Reset()
    {
        _lastGroundedPosition = null;
        _jumpAnchor = null;
        _launchBlockedUntilRelease = false;
        _groundlessFallSteps = 0;
    }

    /// <summary>
    ///     Endless-fall watchdog, called once per descending vertical step. Returns
    ///     <see cref="WalkFallOutcome.Continue" /> while the fall is ordinary — ascending, or a floor is
    ///     known at the current XY, both of which clear the counter. A run of
    ///     <see cref="MaxGroundlessFallSteps" /> floorless steps, or a drop of more than
    ///     <see cref="MaxGroundlessFallDrop" /> below the last confirmed floor, ends the fall:
    ///     <see cref="WalkFallOutcome.Restore" /> with the last grounded pose, or
    ///     <see cref="WalkFallOutcome.Halt" /> when no floor was ever confirmed (the caller freezes the
    ///     camera in place rather than teleporting it to an invented height).
    /// </summary>
    public bool TryObserveFallStep(
        bool descending,
        bool floorKnown,
        float currentZ,
        out WalkFallOutcome outcome,
        out Vector3 restoredPosition)
    {
        restoredPosition = default;
        outcome = WalkFallOutcome.Continue;
        if (!descending || floorKnown)
        {
            _groundlessFallSteps = 0;
            return false;
        }

        _groundlessFallSteps++;
        var droppedTooFar = _lastGroundedPosition is { } floor &&
                            float.IsFinite(currentZ) &&
                            currentZ < floor.Z - MaxGroundlessFallDrop;
        if (_groundlessFallSteps < MaxGroundlessFallSteps && !droppedTooFar) return false;

        _groundlessFallSteps = 0;
        _jumpAnchor = null;
        if (_lastGroundedPosition is { } safe)
        {
            restoredPosition = safe;
            outcome = WalkFallOutcome.Restore;
            return true;
        }

        outcome = WalkFallOutcome.Halt;
        return true;
    }

    /// <summary>
    ///     Records the exact eye pose above a confirmed floor. The controller may visually ease toward
    ///     this pose on a stair, but recovery returns to the actual floor rather than an intermediate Z.
    /// </summary>
    public void RecordGrounded(Vector3 exactGroundedPosition)
    {
        _lastGroundedPosition = exactGroundedPosition;
        _groundlessFallSteps = 0;
    }

    /// <summary>
    ///     Arms one explicit jump. Prefer the floor confirmed at the current post-movement XY; when
    ///     movement crossed the edge in the launch frame, fall back to the preceding grounded pose.
    ///     Returns false when no safe pose exists or recovery is latched until key release.
    /// </summary>
    public bool TryArmJump(Vector3? currentGroundedPosition)
    {
        if (_launchBlockedUntilRelease) return false;
        var anchor = currentGroundedPosition ?? _lastGroundedPosition;
        if (anchor is null) return false;

        _jumpAnchor = anchor;
        return true;
    }

    /// <summary>
    ///     Restores an armed jump only after it starts descending and the sampler reports no floor.
    ///     A known floor—whether immediately landable or far below—preserves the normal jump/fall path.
    /// </summary>
    public bool TryRecover(bool descending, bool floorKnown, out Vector3 position)
    {
        if (!descending || floorKnown || _jumpAnchor is not { } anchor)
        {
            position = default;
            return false;
        }

        position = anchor;
        _lastGroundedPosition = anchor;
        _jumpAnchor = null;
        _launchBlockedUntilRelease = true;
        _groundlessFallSteps = 0;
        return true;
    }

    /// <summary>Completes a normal landing and disarms the now-successful jump.</summary>
    public void CompleteLanding(Vector3 exactGroundedPosition)
    {
        _lastGroundedPosition = exactGroundedPosition;
        _jumpAnchor = null;
        _groundlessFallSteps = 0;
    }

    /// <summary>Re-enables jumping only after the key that caused a recovery is released.</summary>
    public void ObserveJumpKey(bool isDown)
    {
        if (!isDown) _launchBlockedUntilRelease = false;
    }
}
