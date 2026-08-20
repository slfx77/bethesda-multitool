namespace BethesdaMultitool.Core.Diagnostics;

/// <summary>How aggressively a participant should shed rebuildable memory.</summary>
internal enum TrimLevel
{
    /// <summary>Shed down to a comfortable watermark; keep hot entries.</summary>
    Gentle,

    /// <summary>Shed everything that can be rebuilt on demand.</summary>
    Aggressive
}

/// <summary>Which thread is allowed to execute <see cref="IMemoryPressureParticipant.Trim" />.</summary>
internal enum TrimAffinity
{
    /// <summary>The coordinator may invoke <c>Trim</c> directly from its own thread.</summary>
    AnyThread,

    /// <summary>
    ///     Only the owner thread may trim (render-thread caches). The coordinator posts the request
    ///     on the resource's <see cref="ResourceRegistration" />; the owner consumes it in its
    ///     per-frame tick via <see cref="ResourceRegistration.TryConsumePendingTrim" />.
    /// </summary>
    OwnerThread
}

/// <summary>
///     Opt-in contract for caches that can shed rebuildable memory when the
///     <see cref="MemoryBudgetCoordinator" /> detects pressure. Resources whose entries are pinned
///     by live references (refcounted GPU textures, geometry arenas, descriptor slots) must NOT
///     implement this — evicting a pinned entry is a correctness bug, not a memory win; they remain
///     tracking-only <see cref="ITrackableResource" />s.
/// </summary>
internal interface IMemoryPressureParticipant : ITrackableResource
{
    /// <summary>Lower trims first (cheapest to rebuild). Convention: resolvers 10, decoded-payload caches 20, palettes 30.</summary>
    int TrimPriority { get; }

    TrimAffinity TrimAffinity { get; }

    /// <summary>
    ///     Releases rebuildable memory and returns the estimated bytes released. For
    ///     <see cref="TrimAffinity.OwnerThread" /> participants this is only ever called on the
    ///     owner thread.
    /// </summary>
    long Trim(TrimLevel level);
}
