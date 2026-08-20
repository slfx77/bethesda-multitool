namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A pure (device-independent) first-fit free-list sub-allocator over a growable set of
///     fixed-size blocks. Backs <see cref="GpuGeometryArena12" />: instead of one D3D12 committed
///     resource per mesh (the churn the profiler flagged), reference geometry is sub-allocated out
///     of a handful of large arena blocks. All offsets and sizes are kept aligned, so vertex- and
///     index-buffer views built against them satisfy their D3D12 alignment requirements.
///     <para>
///         Free spans within a block are kept offset-sorted and coalesced, so a freed range merges
///         with adjacent free ranges and can be handed back out. Not thread-safe — the arena calls
///         it from the render thread only (allocate on upload, free on eviction / deferred-delete).
///     </para>
/// </summary>
internal sealed class GeometryArenaAllocator
{
    private readonly int _alignment;
    private readonly List<List<FreeSpan>> _blocks = new();
    private readonly List<long> _blockSizes = new();
    private readonly Dictionary<(int Block, long Offset), ulong> _liveAllocations = new();
    private ulong _nextAllocationId = 1;

    /// <summary>Creates an arena that sub-allocates spans from fixed-size blocks.</summary>
    /// <param name="blockSize">Bytes per block (rounded down to a multiple of <paramref name="alignment" />).</param>
    /// <param name="alignment">
    ///     Power-of-two alignment applied to every allocation. 16 satisfies both
    ///     vertex-stride and R16 index-buffer location requirements.
    /// </param>
    public GeometryArenaAllocator(long blockSize, int alignment = 16)
    {
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Must be > 0.");
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Must be a power of two.");

        _alignment = alignment;
        BlockSize = AlignDown(blockSize, alignment);
        if (BlockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size is smaller than the alignment.");
    }

    /// <summary>
    ///     Opt-in free validation (FALLOUT_VIEWER_GEOMETRY_VALIDATE): tracks live allocations by
    ///     (block, offset) → allocation id so <see cref="Free" /> can throw at the exact call site
    ///     of a double-free / stale free / overlapping free instead of silently corrupting the
    ///     free-list, and <see cref="QueryLiveness" /> can distinguish a freed range from one that
    ///     was freed AND re-issued to a different allocation. Off by default — the live map is then
    ///     never populated and the release path allocates nothing extra.
    /// </summary>
    public bool StrictValidation { get; set; }

    /// <summary>Blocks created so far. Grows when an allocation does not fit any existing block.</summary>
    public int BlockCount => _blocks.Count;

    /// <summary>Usable bytes per STANDARD block (oversized allocations get larger dedicated blocks).</summary>
    public long BlockSize { get; }

    /// <summary>Currently-allocated (not freed) bytes across all blocks, including alignment padding.</summary>
    public long AllocatedBytes { get; private set; }

    /// <summary>
    ///     Actual usable bytes of block <paramref name="blockIndex" /> — the standard size, or
    ///     the allocation size that forced a dedicated oversized block.
    /// </summary>
    public long BlockSizeOf(int blockIndex)
    {
        return _blockSizes[blockIndex];
    }

    /// <summary>
    ///     Reserves <paramref name="size" /> bytes (rounded up to the alignment). Uses the first
    ///     existing block with room; if none has room, appends a new block — standard-sized, or a
    ///     DEDICATED larger block when the aligned size exceeds the standard size (monolithic
    ///     meshes like RepBay.NIF run ~25 MB against the 16 MB standard block). Dedicated blocks
    ///     join the normal free-list pool, so their space is reusable once the big mesh is freed.
    /// </summary>
    public ArenaAllocation Allocate(long size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Must be > 0.");

        var alignedSize = AlignUp(size, _alignment);
        for (var b = 0; b < _blocks.Count; b++)
        {
            if (TryAllocateInBlock(b, alignedSize, out var offset))
            {
                AllocatedBytes += alignedSize;
                return Issue(b, offset, size, alignedSize);
            }
        }

        // No existing block had room → append a fresh, fully-free block and allocate at its front.
        var newBlockSize = Math.Max(BlockSize, alignedSize);
        _blocks.Add(new List<FreeSpan> { new(0, newBlockSize) });
        _blockSizes.Add(newBlockSize);
        var blockIndex = _blocks.Count - 1;
        TryAllocateInBlock(blockIndex, alignedSize, out var newOffset);
        AllocatedBytes += alignedSize;
        return Issue(blockIndex, newOffset, size, alignedSize);
    }

    /// <summary>
    ///     Returns an allocation's range to its block's free-list, coalescing with neighbors.
    ///     In strict mode a double-free, a free of a recycled range, or a free whose span overlaps the
    ///     free-list throws HERE, so the offending caller is named by the stack trace.
    /// </summary>
    public void Free(ArenaAllocation allocation)
    {
        if ((uint)allocation.BlockIndex >= (uint)_blocks.Count)
            throw new ArgumentOutOfRangeException(nameof(allocation), "Block index out of range.");

        if (StrictValidation)
        {
            ValidateFree(allocation);
            _liveAllocations.Remove((allocation.BlockIndex, allocation.Offset));
        }

        InsertAndCoalesce(_blocks[allocation.BlockIndex], allocation.Offset, allocation.AlignedSize);
        AllocatedBytes -= allocation.AlignedSize;
    }

    /// <summary>
    ///     Liveness of <paramref name="allocation" /> under strict tracking: <c>Live</c> when its
    ///     (block, offset) still maps to the same allocation id, <c>Recycled</c> when the range was
    ///     freed and re-issued to a DIFFERENT allocation, <c>NotLive</c> when freed and not
    ///     re-issued, <c>Untracked</c> when strict mode is off or the allocation predates it.
    /// </summary>
    public ArenaLiveness QueryLiveness(in ArenaAllocation allocation)
    {
        if (!StrictValidation || allocation.AllocationId == 0)
        {
            return ArenaLiveness.Untracked;
        }

        if (!_liveAllocations.TryGetValue((allocation.BlockIndex, allocation.Offset), out var liveId))
        {
            return ArenaLiveness.NotLive;
        }

        return liveId == allocation.AllocationId ? ArenaLiveness.Live : ArenaLiveness.Recycled;
    }

    private ArenaAllocation Issue(int blockIndex, long offset, long size, long alignedSize)
    {
        var allocation = new ArenaAllocation(blockIndex, offset, size, alignedSize, _nextAllocationId++);
        if (StrictValidation)
        {
            _liveAllocations[(blockIndex, offset)] = allocation.AllocationId;
        }

        return allocation;
    }

    private void ValidateFree(in ArenaAllocation allocation)
    {
        if (!_liveAllocations.TryGetValue((allocation.BlockIndex, allocation.Offset), out var liveId))
        {
            throw new InvalidOperationException(
                $"Geometry arena double-free / stale free: block={allocation.BlockIndex} " +
                $"offset=0x{allocation.Offset:X} alignedSize=0x{allocation.AlignedSize:X} " +
                $"id={allocation.AllocationId} (range is not live)");
        }

        if (liveId != allocation.AllocationId)
        {
            throw new InvalidOperationException(
                $"Geometry arena stale free of a recycled range: block={allocation.BlockIndex} " +
                $"offset=0x{allocation.Offset:X} alignedSize=0x{allocation.AlignedSize:X} " +
                $"id={allocation.AllocationId} (range is live under id {liveId})");
        }

        var end = allocation.Offset + allocation.AlignedSize;
        if (allocation.Offset < 0 || end > _blockSizes[allocation.BlockIndex])
        {
            throw new InvalidOperationException(
                $"Geometry arena free outside its block: block={allocation.BlockIndex} " +
                $"offset=0x{allocation.Offset:X} end=0x{end:X} " +
                $"blockSize=0x{_blockSizes[allocation.BlockIndex]:X} id={allocation.AllocationId}");
        }

        foreach (var span in _blocks[allocation.BlockIndex])
        {
            if (allocation.Offset < span.Offset + span.Length && span.Offset < end)
            {
                throw new InvalidOperationException(
                    $"Geometry arena overlapping free: block={allocation.BlockIndex} " +
                    $"offset=0x{allocation.Offset:X} end=0x{end:X} overlaps free span " +
                    $"[0x{span.Offset:X}, 0x{span.Offset + span.Length:X}) id={allocation.AllocationId}");
            }
        }
    }

    /// <summary>Total free bytes in <paramref name="blockIndex" /> (diagnostics / tests).</summary>
    public long FreeBytesInBlock(int blockIndex)
    {
        var spans = _blocks[blockIndex];
        long total = 0;
        foreach (var span in spans)
        {
            total += span.Length;
        }

        return total;
    }

    private bool TryAllocateInBlock(int blockIndex, long alignedSize, out long offset)
    {
        var spans = _blocks[blockIndex];
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            if (span.Length < alignedSize)
            {
                continue;
            }

            offset = span.Offset;
            if (span.Length == alignedSize)
            {
                spans.RemoveAt(i);
            }
            else
            {
                spans[i] = new FreeSpan(span.Offset + alignedSize, span.Length - alignedSize);
            }

            return true;
        }

        offset = 0;
        return false;
    }

    private static void InsertAndCoalesce(List<FreeSpan> spans, long offset, long length)
    {
        var i = 0;
        while (i < spans.Count && spans[i].Offset < offset)
        {
            i++;
        }

        var merged = new FreeSpan(offset, length);

        // Coalesce with the preceding span if it ends exactly where this one starts.
        if (i > 0 && spans[i - 1].Offset + spans[i - 1].Length == merged.Offset)
        {
            merged = new FreeSpan(spans[i - 1].Offset, spans[i - 1].Length + merged.Length);
            spans.RemoveAt(i - 1);
            i--;
        }

        // Coalesce with the following span if this one ends exactly where it starts.
        if (i < spans.Count && merged.Offset + merged.Length == spans[i].Offset)
        {
            merged = new FreeSpan(merged.Offset, merged.Length + spans[i].Length);
            spans.RemoveAt(i);
        }

        spans.Insert(i, merged);
    }

    private static long AlignUp(long value, int alignment)
    {
        return (value + alignment - 1) & ~((long)alignment - 1);
    }

    private static long AlignDown(long value, int alignment)
    {
        return value & ~((long)alignment - 1);
    }

    private readonly record struct FreeSpan(long Offset, long Length);
}

/// <summary>
///     One sub-allocation handed out by <see cref="GeometryArenaAllocator" />. <see cref="Offset" />
///     is the byte offset within block <see cref="BlockIndex" />; <see cref="AlignedSize" /> is the
///     padded span returned to the free-list on <see cref="GeometryArenaAllocator.Free" />.
///     <see cref="AllocationId" /> is a process-unique monotonic stamp (0 only for hand-built test
///     values) letting diagnostics tell a live range from one freed and re-issued at the same offset.
/// </summary>
internal readonly record struct ArenaAllocation(
    int BlockIndex,
    long Offset,
    long Size,
    long AlignedSize,
    ulong AllocationId = 0);

/// <summary>Liveness verdicts for <see cref="GeometryArenaAllocator.QueryLiveness" />.</summary>
internal enum ArenaLiveness
{
    /// <summary>Strict tracking is off (or the allocation predates it) — no verdict.</summary>
    Untracked,

    /// <summary>The range is still owned by this allocation.</summary>
    Live,

    /// <summary>The range was freed and has not been re-issued.</summary>
    NotLive,

    /// <summary>
    ///     The range was freed and re-issued to a DIFFERENT allocation — reads through stale
    ///     views now fetch that other allocation's bytes.
    /// </summary>
    Recycled
}
