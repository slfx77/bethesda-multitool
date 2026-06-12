namespace FalloutXbox360Utils.Core.Diagnostics;

/// <summary>
///     Coarse classification of a tracked resource. Diagnostics surfaces group rows by category and
///     the <see cref="MemoryBudgetCoordinator" /> applies its byte budget to <see cref="CpuCache" />
///     only — GPU residency is structurally bounded (refcounts / LRU caps), memory-mapped views are
///     file-backed rather than committed RAM, and disk caches enforce their own on-disk caps.
/// </summary>
internal enum ResourceCategory
{
    /// <summary>Decoded payloads, resolvers, palettes — rebuildable managed-heap caches.</summary>
    CpuCache,

    /// <summary>GPU-resident bytes (textures, geometry arena blocks, upload heaps).</summary>
    GpuResident,

    /// <summary>GPU bookkeeping with negligible bytes (descriptor slots, deletion-queue depth).</summary>
    GpuMeta,

    /// <summary>Persistent on-disk caches — bytes are disk usage, not RAM.</summary>
    DiskCache,

    /// <summary>Memory-mapped file views (file-backed; not committed managed memory).</summary>
    MappedFile,

    /// <summary>Work queues, dispatchers, and background-task runners.</summary>
    Queue,

    /// <summary>Aggregate session holders (a loaded file and its derived data).</summary>
    SessionScope,
}
