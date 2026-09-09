using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>
///     Unpacks a RenderWare library-id word into a version number.
///     <para>
///         ⚠ Ported deliberately DIFFERENTLY from NeversoftMultitool, which compares the raw header
///         word against a constant. That works for the ids in its own corpus and for ours
///         (<c>0x1802FFFF</c>, <c>0x1C020065</c>) by luck rather than design: both happen to fall on
///         the correct side of the comparison. A build whose id sat the other side would be
///         mis-branched silently, so the real unpack is implemented here instead.
///     </para>
/// </summary>
internal static class RwLibraryVersion
{
    /// <summary>
    ///     The version encoded in <paramref name="libraryId" />. Ids with no high half are the old
    ///     pre-3.1 encoding and are simply shifted; everything else uses the packed form.
    /// </summary>
    public static uint Unpack(uint libraryId)
    {
        if ((libraryId & 0xFFFF0000u) == 0)
        {
            return libraryId << 8;
        }

        return (((libraryId >> 14) & 0x3FF00u) + 0x30000u) | ((libraryId >> 16) & 0x3Fu);
    }

    /// <summary>The major.minor.patch.build form of an unpacked version, for display.</summary>
    public static string Describe(uint libraryId)
    {
        var version = Unpack(libraryId);
        return $"{(version >> 16) & 0xF}.{(version >> 12) & 0xF}.{(version >> 8) & 0xF}.{version & 0x3F}";
    }
}

/// <summary>One per-material index split from a BINMESH plugin chunk.</summary>
/// <param name="MaterialIndex">Index into the owning geometry's material list.</param>
/// <param name="Indices">Vertex indices, as authored — a triangle list or a strip per the owner's flags.</param>
internal readonly record struct RwBinMeshSplit(uint MaterialIndex, uint[] Indices);

/// <summary>
///     A RenderWare BINMESH plugin chunk (<see cref="RwChunk.BinMeshPlugin" />): the per-material
///     index splits a geometry is drawn in.
///     <code>
///     u32 flags          0 = triangle list, 1 = tristrip
///     u32 meshCount
///     u32 totalIndices
///     per mesh: u32 indexCount, u32 materialIndex, u32[indexCount] indices
///     </code>
///     <para>
///         ⚑ Confirmed against the Oblivion PSP data 2026-09-06: **8,578 of 8,578** chunks parse
///         under this layout with the declared <c>totalIndices</c> equal to the summed per-mesh
///         counts — 7,349 reached through WORLD roots and 1,229 through CLUMP roots. Indices are
///         <b>u32</b>: reading them as u16 tiles 0 of 8,578, so the width is settled by the data
///         rather than assumed from the PC layout.
///     </para>
///     <para>
///         ⚠ <b>A mesh count of zero is VALID and common</b> — 2,513 of the retail chunks are
///         exactly twelve bytes: the three header words and nothing else. An earlier probe rejected
///         that as implausible and reported the format as only 71% matched. Guarding against zero
///         here would reintroduce the same false negative.
///     </para>
/// </summary>
internal sealed class RwBinMesh
{
    /// <summary>Bytes of header before the first split.</summary>
    public const int HeaderLength = 12;

    /// <summary>Bytes of per-split header: index count and material index.</summary>
    public const int SplitHeaderLength = 8;

    /// <summary><see cref="Flags" /> value meaning the indices form triangle strips.</summary>
    public const uint TriangleStripFlag = 1;

    private RwBinMesh(uint flags, uint declaredIndexCount, RwBinMeshSplit[] splits)
    {
        Flags = flags;
        DeclaredIndexCount = declaredIndexCount;
        Splits = splits;
    }

    /// <summary>The flags word: 0 for triangle lists, 1 for strips.</summary>
    public uint Flags { get; }

    /// <summary>True when the splits hold triangle strips rather than lists.</summary>
    public bool IsTriangleStrip => (Flags & TriangleStripFlag) != 0;

    /// <summary>The header's own total-index count, kept so a caller can check it against the splits.</summary>
    public uint DeclaredIndexCount { get; }

    /// <summary>The per-material splits, in file order. Empty is valid and common.</summary>
    public IReadOnlyList<RwBinMeshSplit> Splits { get; }

    /// <summary>The summed index count across <see cref="Splits" />.</summary>
    public long TotalIndices
    {
        get
        {
            long total = 0;
            foreach (var split in Splits)
            {
                total += split.Indices.Length;
            }

            return total;
        }
    }

    /// <summary>
    ///     True when the redundant header total agrees with the splits. It does on all 8,578 retail
    ///     chunks, which is what makes the layout more than a plausible fit — a redundant field
    ///     agreeing thousands of times is not coincidence.
    /// </summary>
    public bool TotalAgrees => DeclaredIndexCount == TotalIndices;

    /// <summary>
    ///     Parses a BINMESH body. Returns null rather than throwing when the bytes do not fit the
    ///     layout, because a caller walking a whole pack wants to skip an odd chunk, not abort.
    /// </summary>
    public static RwBinMesh? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < HeaderLength)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var meshCount = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        var declaredTotal = BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);

        // No zero guard: an empty BINMESH is the ordinary "no splits" form.
        if (meshCount > (body.Length - HeaderLength) / SplitHeaderLength)
        {
            return null;
        }

        var splits = new RwBinMeshSplit[meshCount];
        var position = HeaderLength;
        for (var i = 0; i < meshCount; i++)
        {
            if (position + SplitHeaderLength > body.Length)
            {
                return null;
            }

            var indexCount = BinaryPrimitives.ReadUInt32LittleEndian(body[position..]);
            var materialIndex = BinaryPrimitives.ReadUInt32LittleEndian(body[(position + 4)..]);
            position += SplitHeaderLength;

            if (indexCount > (uint)((body.Length - position) / sizeof(uint)))
            {
                return null;
            }

            var indices = new uint[indexCount];
            for (var n = 0; n < indices.Length; n++)
            {
                indices[n] = BinaryPrimitives.ReadUInt32LittleEndian(body[(position + n * sizeof(uint))..]);
            }

            position += (int)indexCount * sizeof(uint);
            splits[i] = new RwBinMeshSplit(materialIndex, indices);
        }

        // The body must tile exactly. A trailing remainder means this is not a BINMESH, and
        // accepting it would let a mis-located chunk through as plausible geometry.
        return position == body.Length ? new RwBinMesh(flags, declaredTotal, splits) : null;
    }
}
