namespace BethesdaMultitool;

/// <summary>
///     The ONE streaming-quiescence predicate for capture/export gates. Four hand-rolled copies of
///     this check used to live in the top-down overlay, the profiler capture hook, the headless
///     renderer, and the export loops — and they drifted: the export gates omitted
///     <see cref="WorldRenderStats.ReferenceTexturePending" />, so a tile could pass while submeshes
///     were still withheld waiting on textures (a resolved texture's async copy-queue upload flips
///     TexturesReady a frame or two AFTER PendingResolves/PendingUploads hit zero; that window is
///     visible ONLY as ReferenceTexturePending) and bake placeholder-white leaf cards into the PNG.
///     <para>
///         <b>Strict vs loose.</b> Loose ("nothing actively loading") is for LIVE convergence loops —
///         it deliberately ignores <c>ReferenceTexturePending</c> and <c>ReferenceMeshMissing</c>,
///         which count assets that returned null/pending THIS frame and never reach zero in regions
///         with permanently-missing/cut content (negative-cached, not re-queued). Strict additionally
///         requires <c>ReferenceTexturePending == 0</c> — right for one-shot captures/exports, but it
///         MUST be time-boxed by the caller because a permanently-missing texture can pin the counter.
///     </para>
///     <para>
///         <b>Never</b> add a <c>ReferenceMeshMissing == 0</c> term: dense regions hold 10–28k
///         permanently-unresolvable meshes (FO4 downtown) and any gate on it deadlocks.
///     </para>
///     <para>
///         A null stats parameter means that layer wasn't rendered this frame (its
///         <c>LastStats</c> would be STALE, left over from the last live frame) — it imposes no
///         requirement. Callers are responsible for passing null for disabled layers.
///     </para>
/// </summary>
internal static class StreamingQuiescence
{
    /// <summary>Time box for strict settle loops — the profiler's evidence-derived bound for heavy
    /// scenes (cold camera moves needed up to ~20s before reference streaming quiesced).</summary>
    public static readonly TimeSpan DefaultSettleTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Single-frame quiescence over the stats of the layers that rendered this frame.</summary>
    public static bool IsQuiesced(WorldRenderStats? references, WorldRenderStats? terrain, bool strict)
    {
        if (references is not null)
        {
            if (references.ReferenceGpuUploads != 0
                || references.ReferenceQueuedDecodes != 0
                || references.ReferenceActiveDecodes != 0
                || references.ReferenceTexturePendingResolves != 0
                || references.ReferenceTexturePendingUploads != 0)
            {
                return false;
            }
            if (strict && references.ReferenceTexturePending != 0) return false;
        }
        return terrain is null || terrain.NewUploads == 0;
    }

    /// <summary>
    ///     Polls <paramref name="isQuiesced" /> until it holds for <paramref name="consecutive" />
    ///     successive polls or <paramref name="timeout" /> elapses; true = settled, false = timed
    ///     out. The consecutive requirement exists because every counter in the predicate is a
    ///     SINGLE-FRAME sample: a transient zero between terrain LOD refinements (or between a
    ///     decode wave draining and the next enqueue) passes one poll while the scene is still
    ///     loading — captures taken on such a gap shipped half-streamed terrain (2026-08-10).
    ///     For callers whose frames are driven EXTERNALLY (e.g. the profiler's scenario loop) —
    ///     stats only advance when frames render, so a pure delay-poll against a paused render loop
    ///     spins on frozen stats forever. Gates that own their rendering must keep a
    ///     render→check→delay loop instead.
    /// </summary>
    public static async Task<bool> PollAsync(
        Func<bool> isQuiesced, TimeSpan timeout, TimeSpan interval, int consecutive = 1,
        CancellationToken ct = default)
    {
        var waited = TimeSpan.Zero;
        var streak = 0;
        while (true)
        {
            streak = isQuiesced() ? streak + 1 : 0;
            if (streak >= Math.Max(1, consecutive)) return true;
            if (waited >= timeout) return false;
            await Task.Delay(interval, ct);
            waited += interval;
        }
    }
}
