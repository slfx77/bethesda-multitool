namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A pure (device-independent) circular bump allocator for staging memory whose regions are
///     released in strict FIFO order. Backs <see cref="GpuTerrainStagingRing12" />.
///     <para>
///         FIFO is not a convenience here, it is the invariant that makes a ring correct with a
///         single tail pointer: a staging region may be reused only once the GPU has finished the
///         copy that reads it, and those copies retire in submission order because
///         <see cref="GpuDeletionQueue12" /> is a frame-stamped <c>Queue</c>. Out-of-order release
///         would strand live bytes behind a freed hole, which is precisely what
///         <see cref="GeometryArenaAllocator" />'s free-list exists to handle — at the cost of
///         bookkeeping this path does not need.
///     </para>
///     <para>
///         Allocations never wrap. When a request does not fit before the end of the buffer, the
///         remaining tail bytes are folded into that allocation's <c>Charge</c> as padding and the
///         region starts at offset 0, so releasing the allocation reclaims the padding with it and
///         live bytes stay exactly accountable.
///     </para>
///     <para>
///         Not thread-safe — render thread only, like every other allocator on this path.
///     </para>
/// </summary>
internal sealed class StagingRingAllocator
{
    private readonly int _alignment;
    private readonly Queue<long> _outstanding = new();
    private long _head;
    private ulong _nextAllocationSequence = 1;
    private ulong _nextReleaseSequence = 1;

    /// <summary>Creates a ring that hands out aligned regions and reclaims them in release order.</summary>
    /// <param name="capacity">Total bytes in the ring (rounded down to a multiple of <paramref name="alignment" />).</param>
    /// <param name="alignment">
    ///     Power-of-two alignment applied to every region start. 16 clears the 4-byte
    ///     <c>CopyBufferRegion</c> offset requirement with margin and keeps writes on a vector
    ///     boundary, matching <see cref="GeometryArenaAllocator" />'s default.
    /// </param>
    public StagingRingAllocator(long capacity, int alignment = 16)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Must be > 0.");
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Must be a power of two.");

        _alignment = alignment;
        Capacity = capacity & ~((long)alignment - 1);
        if (Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity is smaller than the alignment.");
    }

    /// <summary>Usable bytes in the ring.</summary>
    public long Capacity { get; }

    /// <summary>
    ///     Opt-in release-order validation (shares FALLOUT_VIEWER_GEOMETRY_VALIDATE with the
    ///     geometry arena). FIFO release is this allocator's load-bearing invariant, and violating
    ///     it hands out a region the GPU is still reading — a corruption that surfaces as garbage
    ///     terrain far from the code that caused it. On, <see cref="Release" /> throws at the
    ///     offending call site instead. Off by default: the sequence counters still advance (two
    ///     increments) but nothing is compared.
    /// </summary>
    public bool StrictValidation { get; set; }

    /// <summary>Bytes held by regions that have been allocated but not yet released, padding included.</summary>
    public long LiveBytes { get; private set; }

    /// <summary>Regions allocated but not yet released. Equals the deletion queue's pending staging depth.</summary>
    public int OutstandingCount => _outstanding.Count;

    /// <summary>
    ///     Carves a contiguous <paramref name="bytes" />-long region, or returns false leaving the
    ///     ring untouched when the outstanding regions leave too little room (or the request simply
    ///     exceeds <see cref="Capacity" />). A false return is a routine outcome, not an error: the
    ///     caller falls back to a transient committed buffer for that one upload.
    /// </summary>
    public bool TryAllocate(long bytes, out long offset, out long charge, out ulong sequence)
    {
        offset = 0;
        charge = 0;
        sequence = 0;
        if (bytes <= 0 || bytes > Capacity)
        {
            return false;
        }

        var aligned = AlignUp(_head, _alignment);
        long start;
        long padding;
        if (aligned + bytes > Capacity)
        {
            // Fold the unusable tail into this allocation rather than tracking it separately: a
            // padding record the caller never releases would leak the tail on every wrap.
            padding = Capacity - _head;
            start = 0;
        }
        else
        {
            padding = aligned - _head;
            start = aligned;
        }

        var required = padding + bytes;
        if (required > Capacity - LiveBytes)
        {
            return false;
        }

        LiveBytes += required;
        _outstanding.Enqueue(required);
        _head = start + bytes == Capacity ? 0 : start + bytes;
        offset = start;
        charge = required;
        sequence = _nextAllocationSequence++;
        return true;
    }

    /// <summary>
    ///     Releases the oldest outstanding region, which <paramref name="sequence" /> must identify.
    ///     Returns the bytes reclaimed (0 when nothing is outstanding, so a stray release cannot
    ///     drive <see cref="LiveBytes" /> negative and hand out a region the GPU is still reading).
    /// </summary>
    public long Release(ulong sequence)
    {
        if (StrictValidation && sequence != _nextReleaseSequence)
        {
            throw new InvalidOperationException(
                $"StagingRingAllocator: out-of-order release (got #{sequence}, expected " +
                $"#{_nextReleaseSequence}). Staging regions must retire in submission order.");
        }

        if (!_outstanding.TryDequeue(out var charge))
        {
            return 0;
        }

        _nextReleaseSequence++;
        LiveBytes -= charge;
        return charge;
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~((long)alignment - 1);
}
