using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     Deterministic recovery state for an explicit walk-mode jump that descends over an unknown
///     floor. Natural ledge falls are never armed and therefore never teleport. After recovery,
///     launching stays blocked until Space is released so a held key cannot create a jump/restore
///     oscillation.
/// </summary>
internal sealed class WalkVoidRecovery
{
    private Vector3? _lastGroundedPosition;
    private Vector3? _jumpAnchor;
    private bool _launchBlockedUntilRelease;

    /// <summary>For tests/diagnostics: true only after a void recovery while jump remains held.</summary>
    internal bool LaunchBlockedUntilRelease => _launchBlockedUntilRelease;

    /// <summary>Clears all world-relative state on mode changes or when ground sampling disappears.</summary>
    public void Reset()
    {
        _lastGroundedPosition = null;
        _jumpAnchor = null;
        _launchBlockedUntilRelease = false;
    }

    /// <summary>
    ///     Records the exact eye pose above a confirmed floor. The controller may visually ease toward
    ///     this pose on a stair, but recovery returns to the actual floor rather than an intermediate Z.
    /// </summary>
    public void RecordGrounded(Vector3 exactGroundedPosition)
        => _lastGroundedPosition = exactGroundedPosition;

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
        return true;
    }

    /// <summary>Completes a normal landing and disarms the now-successful jump.</summary>
    public void CompleteLanding(Vector3 exactGroundedPosition)
    {
        _lastGroundedPosition = exactGroundedPosition;
        _jumpAnchor = null;
    }

    /// <summary>Re-enables jumping only after the key that caused a recovery is released.</summary>
    public void ObserveJumpKey(bool isDown)
    {
        if (!isDown) _launchBlockedUntilRelease = false;
    }
}
