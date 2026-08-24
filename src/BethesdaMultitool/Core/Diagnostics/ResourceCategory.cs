namespace BethesdaMultitool.Core.Diagnostics;

/// <summary>
///     Coarse classification of a tracked resource. Diagnostics surfaces group rows by category and
///     the <see cref="MemoryBudgetCoordinator" /> applies its byte budget to <see cref="CpuCache" />
///     only — memory-mapped views are file-backed rather than committed RAM, and disk caches enforce
///     their own on-disk caps.
///     <para>
///         GPU residency is governed separately (see <c>GpuResidencyGovernor12</c>) rather than by the
///         CPU budget coordinator. It is NOT "structurally bounded by refcounts / LRU caps" — that
///         claim used to live here and is false: the resident-mesh and terrain-cell LRUs are bounded
///         by entry COUNT, and the terrain cell cache is sized at or above its worldspace's cell
///         count, so it can never evict a cell it built.
///     </para>
/// </summary>
internal enum ResourceCategory
{
    /// <summary>Decoded payloads, resolvers, palettes — rebuildable managed-heap caches.</summary>
    CpuCache,

    /// <summary>GPU-resident bytes (textures, geometry arena blocks, upload heaps).</summary>
    GpuResident,

    /// <summary>
    ///     Bytes that are already counted under another <see cref="GpuResident" /> owner, reported
    ///     again here purely as an attribution breakdown — EXCLUDED from category totals so they are
    ///     never double-counted.
    ///     <para>
    ///         Exists because a pool can be governed by one component and owned by another: the
    ///         resident-mesh LRU must know its own byte size to be trimmable, but those bytes are
    ///         physically the geometry arena's. Reporting 0 (or an entry count) instead would leave
    ///         the pool ungovernable — you cannot trim what you cannot measure.
    ///     </para>
    /// </summary>
    GpuAttributed,

    /// <summary>GPU bookkeeping with negligible bytes (descriptor slots, deletion-queue depth).</summary>
    GpuMeta,

    /// <summary>Persistent on-disk caches — bytes are disk usage, not RAM.</summary>
    DiskCache,

    /// <summary>Memory-mapped file views (file-backed; not committed managed memory).</summary>
    MappedFile,

    /// <summary>Work queues, dispatchers, and background-task runners.</summary>
    Queue,

    /// <summary>Aggregate session holders (a loaded file and its derived data).</summary>
    SessionScope
}

/// <summary>
///     Which physical memory pool a GPU allocation actually occupies, as WDDM accounts for it.
///     <para>
///         This distinction is load-bearing, not bookkeeping. On a discrete adapter an UPLOAD-heap
///         resource lives in system RAM and is read across PCIe; only DEFAULT-heap resources occupy
///         device-local VRAM. Summing the two produces a number that matches neither pool, and a
///         governor that shed <see cref="NonLocal" /> bytes to relieve <see cref="Local" /> pressure
///         would free the wrong memory and move the VRAM budget by approximately zero.
///     </para>
///     <para>
///         The concrete case this was added for: <c>GpuGeometryArena12</c> reports
///         <see cref="ResourceCategory.GpuResident" /> but allocates UPLOAD-heap blocks, so reference
///         geometry is system RAM that used to be counted as VRAM.
///     </para>
/// </summary>
internal enum GpuMemorySegment
{
    /// <summary>Not a GPU allocation, or the owning pool does not distinguish.</summary>
    Unspecified,

    /// <summary>Device-local VRAM (DEFAULT heap) — what <c>MemorySegmentGroup.Local</c> charges.</summary>
    Local,

    /// <summary>Host-visible system memory (UPLOAD / READBACK heaps) read by the GPU over the bus.</summary>
    NonLocal
}
