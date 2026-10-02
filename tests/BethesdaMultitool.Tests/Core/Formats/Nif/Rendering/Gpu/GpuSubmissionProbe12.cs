using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Observes actual recorder callback and release ordering with bounded, explicit injected managed failures.</summary>
internal sealed class GpuSubmissionProbe12 : IDisposable, IGpuCommandSubmissionParticipant12
{
    /// <summary>Gets every attempted release, including deliberately failed attempts.</summary>
    internal int ReleaseAttempts { get; private set; }

    /// <summary>Gets legacy submitted notifications, including conservative uncertain outcomes.</summary>
    internal int SubmittedCount { get; private set; }

    /// <summary>Gets definite-abandonment notifications.</summary>
    internal int AbortedCount { get; private set; }

    /// <summary>Gets the recorder's published fence as observed from the most recent submitted callback.</summary>
    internal ulong ObservedFence { get; private set; }

    /// <summary>Gets or sets the bounded number of release attempts that should deliberately fail.</summary>
    internal int RemainingReleaseFailures { get; set; }

    /// <summary>Gets whether a callback should fail after recording its notification.</summary>
    internal bool ThrowOnNotification { get; init; }

    /// <summary>Gets an optional fence observer invoked synchronously during submitted notification.</summary>
    internal Func<ulong>? ReadFence { get; init; }

    /// <summary>Gets an optional synchronous release callback for observing nested ownership behavior.</summary>
    internal Action? OnRelease { get; init; }

    /// <summary>Records publication before optionally failing so duplicate or skipped callbacks are observable.</summary>
    public void OnCommandListSubmitted()
    {
        SubmittedCount++;
        ObservedFence = ReadFence?.Invoke() ?? 0;
        if (ThrowOnNotification)
        {
            throw new InvalidOperationException("Injected participant failure.");
        }
    }

    /// <summary>Records rollback before optionally failing so sibling isolation is observable.</summary>
    public void OnCommandListAborted()
    {
        AbortedCount++;
        if (ThrowOnNotification)
        {
            throw new InvalidOperationException("Injected participant failure.");
        }
    }

    /// <summary>Counts exact release attempts and fails only the configured number of attempts.</summary>
    public void Dispose()
    {
        ReleaseAttempts++;
        OnRelease?.Invoke();
        if (RemainingReleaseFailures <= 0)
        {
            return;
        }
        RemainingReleaseFailures--;
#pragma warning disable S3877 // Managed fault probe deliberately throws to verify retained cleanup retries.
        throw new InvalidOperationException("Injected resource release failure.");
#pragma warning restore S3877
    }
}
