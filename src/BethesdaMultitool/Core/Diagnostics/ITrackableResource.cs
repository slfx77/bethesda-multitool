namespace BethesdaMultitool.Core.Diagnostics;

/// <summary>
///     Universal contract for anything that registers with the <see cref="ResourceRegistry" /> —
///     caches, buffers, queues, dispatchers, and session scopes.
///     <para>
///         <b>Threading contract for <see cref="GetStats" />:</b> it is called from arbitrary
///         threads (the GUI diagnostics timer, the CLI end-of-run reporter, the memory budget
///         coordinator) and must be cheap and lock-free with respect to the owner's hot path.
///         Render-thread-owned resources keep plain counter fields written only by the owner thread;
///         snapshot readers tolerate slightly stale values (the process is x64, so aligned 64-bit
///         reads are atomic — no torn reads). Concurrent resources use <c>Interlocked</c> counters.
///         A resource whose stats are expensive to compute must cache them internally and return the
///         cached value here.
///     </para>
/// </summary>
internal interface ITrackableResource
{
    /// <summary>
    ///     Stable type-level name, e.g. <c>"GpuTextureCache12"</c>. Instances of the same type are
    ///     disambiguated at registration time via the instance tag, not here.
    /// </summary>
    string ResourceName { get; }

    ResourceCategory Category { get; }

    /// <summary>Returns a point-in-time view of the resource's counters. See the threading contract above.</summary>
    ResourceStats GetStats();
}
