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
    private readonly long _blockSize;
    private readonly int _alignment;
    private readonly List<List<FreeSpan>> _blocks = new();
    private long _allocatedBytes;

    /// <summary>Creates an arena that sub-allocates spans from fixed-size blocks.</summary>
    /// <param name="blockSize">Bytes per block (rounded down to a multiple of <paramref name="alignment" />).</param>
    /// <param name="alignment">Power-of-two alignment applied to every allocation. 16 satisfies both
    ///     vertex-stride and R16 index-buffer location requirements.</param>
    public GeometryArenaAllocator(long blockSize, int alignment = 16)
    {
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Must be > 0.");
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Must be a power of two.");

        _alignment = alignment;
        _blockSize = AlignDown(blockSize, alignment);
        if (_blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size is smaller than the alignment.");
    }

    /// <summary>Blocks created so far. Grows when an allocation does not fit any existing block.</summary>
    public int BlockCount => _blocks.Count;

    /// <summary>Usable bytes per block.</summary>
    public long BlockSize => _blockSize;

    /// <summary>Currently-allocated (not freed) bytes across all blocks, including alignment padding.</summary>
    public long AllocatedBytes => _allocatedBytes;

    /// <summary>
    ///     Reserves <paramref name="size" /> bytes (rounded up to the alignment). Uses the first
    ///     existing block with room; if none has room, appends a new block. Throws if the aligned
    ///     size exceeds a single block.
    /// </summary>
    public ArenaAllocation Allocate(long size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Must be > 0.");

        var alignedSize = AlignUp(size, _alignment);
        if (alignedSize > _blockSize)
            throw new ArgumentOutOfRangeException(
                nameof(size),
                $"Allocation of {size}B (aligned {alignedSize}B) exceeds arena block size {_blockSize}B.");

        for (var b = 0; b < _blocks.Count; b++)
        {
            if (TryAllocateInBlock(b, alignedSize, out var offset))
            {
                _allocatedBytes += alignedSize;
                return new ArenaAllocation(b, offset, size, alignedSize);
            }
        }

        // No existing block had room → append a fresh, fully-free block and allocate at its front.
        _blocks.Add(new List<FreeSpan> { new(0, _blockSize) });
        var blockIndex = _blocks.Count - 1;
        TryAllocateInBlock(blockIndex, alignedSize, out var newOffset);
        _allocatedBytes += alignedSize;
        return new ArenaAllocation(blockIndex, newOffset, size, alignedSize);
    }

    /// <summary>Returns an allocation's range to its block's free-list, coalescing with neighbours.</summary>
    public void Free(ArenaAllocation allocation)
    {
        if ((uint)allocation.BlockIndex >= (uint)_blocks.Count)
            throw new ArgumentOutOfRangeException(nameof(allocation), "Block index out of range.");

        InsertAndCoalesce(_blocks[allocation.BlockIndex], allocation.Offset, allocation.AlignedSize);
        _allocatedBytes -= allocation.AlignedSize;
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

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~((long)alignment - 1);

    private static long AlignDown(long value, int alignment) => value & ~((long)alignment - 1);

    private readonly record struct FreeSpan(long Offset, long Length);
}

/// <summary>
///     One sub-allocation handed out by <see cref="GeometryArenaAllocator" />. <see cref="Offset" />
///     is the byte offset within block <see cref="BlockIndex" />; <see cref="AlignedSize" /> is the
///     padded span returned to the free-list on <see cref="GeometryArenaAllocator.Free" />.
/// </summary>
internal readonly record struct ArenaAllocation(int BlockIndex, long Offset, long Size, long AlignedSize);
