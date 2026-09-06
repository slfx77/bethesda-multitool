namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;

internal enum CaptureShadowPrimingDecision
{
    Ready,
    Retry,
    Fail
}

/// <summary>Bounds coherent capture retries without treating empty shadow cascades as failures.</summary>
internal static class CaptureShadowPrimingPolicy
{
    internal const int MaxPrimeAttempts = 4;
    internal const int CompleteCascadeMask = 0xF;

    // Each of four cascades needs reference b0 (352 bytes, aligned to 512), terrain b0
    // (64 bytes, aligned to 256), and terrain b2 (16 bytes, aligned to 256). One more
    // alignment block covers the ring's starting offset. Keep this API-neutral.
    internal const uint RingReservationBytes = (4u * 4u + 1u) * 256u;

    /// <summary>Decides whether the completed prime may be sampled, retried, or rejected.</summary>
    /// <param name="coherent">Whether capture requires all shadow replays to finish before rendering.</param>
    /// <param name="completedCascadeMask">
    ///     Authoritative replay completion, not submitted-draw availability or occupied depth.
    ///     An empty but completed replay sets its bit just like a populated completed replay.
    /// </param>
    /// <param name="primeAttempts">Number of completed priming attempts, starting at one.</param>
    internal static CaptureShadowPrimingDecision Decide(
        bool coherent, int completedCascadeMask, int primeAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(primeAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(primeAttempts, MaxPrimeAttempts);

        if (!coherent || completedCascadeMask == CompleteCascadeMask)
        {
            return CaptureShadowPrimingDecision.Ready;
        }

        return primeAttempts < MaxPrimeAttempts
            ? CaptureShadowPrimingDecision.Retry
            : CaptureShadowPrimingDecision.Fail;
    }
}
