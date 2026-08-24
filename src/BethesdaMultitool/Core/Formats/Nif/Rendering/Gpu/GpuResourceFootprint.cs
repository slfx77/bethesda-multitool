namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     What a GPU allocation actually costs, as opposed to what was asked for.
///     <para>
///         D3D12 rounds every committed resource up to
///         <c>D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT</c> (64 KiB), so a buffer's requested width
///         understates its real footprint — badly, for many small buffers. Terrain is the worked
///         example: a 33×33 cell asks for 148,104 bytes across two buffers and is charged 262,144,
///         so <b>43% of every Fallout/Oblivion/Skyrim terrain cell is alignment padding</b>. At
///         Fallout 76's 129×129 grid the same two buffers waste ~94 KiB per cell, which is ~3.9 GiB
///         across Appalachia's 41,219 cells.
///     </para>
///     <para>
///         Pure and GUI-free so the renderer (which is <c>WINDOWS_GUI</c>-gated) and the residency
///         policy that must PREDICT a cell's cost before allocating it share one implementation. Two
///         copies of this rule would silently disagree, and a budget computed from the optimistic
///         one would over-commit by exactly the padding it forgot.
///     </para>
/// </summary>
internal static class GpuResourceFootprint
{
    /// <summary>D3D12's default placement alignment for committed buffers (64 KiB).</summary>
    public const long CommittedBufferAlignment = 64L * 1024L;

    /// <summary>
    ///     Bytes actually charged for a committed buffer of <paramref name="requestedBytes" /> —
    ///     the request rounded up to <see cref="CommittedBufferAlignment" />. Zero stays zero;
    ///     negative input is treated as zero.
    /// </summary>
    public static long CommittedBufferBytes(long requestedBytes)
    {
        if (requestedBytes <= 0)
        {
            return 0;
        }

        return (requestedBytes + CommittedBufferAlignment - 1) / CommittedBufferAlignment * CommittedBufferAlignment;
    }

    /// <summary>Combined charge for two committed buffers, each rounded independently (as D3D12 does).</summary>
    public static long CommittedBufferBytes(long firstBytes, long secondBytes) =>
        CommittedBufferBytes(firstBytes) + CommittedBufferBytes(secondBytes);

    /// <summary>
    ///     Sub-region alignment inside a geometry/terrain arena block
    ///     (<c>GeometryArenaAllocator</c>'s default).
    /// </summary>
    public const int ArenaRegionAlignment = 16;

    /// <summary>
    ///     Bytes an arena charges for two streams packed into ONE sub-allocation: the first stream
    ///     padded to <see cref="ArenaRegionAlignment" /> so the second starts on a vector boundary,
    ///     then the whole range padded again (the allocator aligns every allocation).
    ///     <para>
    ///         This is the terrain equivalent of <see cref="CommittedBufferBytes(long, long)" /> and
    ///         is dramatically smaller: 16-byte padding instead of two 64 KiB-rounded resources. Both
    ///         the arena and the residency policy that PREDICTS a cell's cost must use this same
    ///         function — if the predictor kept using the committed-buffer rule after the allocator
    ///         moved to an arena, it would over-estimate every cell by up to 128 KiB, the planned
    ///         budget would sit permanently above actual residency, and the byte bound would never
    ///         evict anything. A budget that can never fire is worse than none, because it looks
    ///         like it is working.
    ///     </para>
    /// </summary>
    public static long ArenaSubAllocationBytes(long firstBytes, long secondBytes)
    {
        var first = Math.Max(0, firstBytes);
        var second = Math.Max(0, secondBytes);
        return AlignUp(AlignUp(first, ArenaRegionAlignment) + second, ArenaRegionAlignment);
    }

    private static long AlignUp(long value, int alignment) =>
        (value + alignment - 1) & ~((long)alignment - 1);
}
