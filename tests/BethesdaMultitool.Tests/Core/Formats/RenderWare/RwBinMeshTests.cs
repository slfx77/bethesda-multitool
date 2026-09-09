using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.RenderWare;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.RenderWare;

/// <summary>
///     The RenderWare chunk walk and the BINMESH plugin reader, ported from NeversoftMultitool with
///     two corrections.
///     <para>
///         Both corrections are pinned here because both are silent when wrong: the 0x1300 size
///         overshoot ends a walk early with no error, and the library-id branch takes the right path
///         by accident for every id in the current corpus.
///     </para>
/// </summary>
public sealed class RwBinMeshTests
{
    private static byte[] Chunk(uint type, uint declaredSize, uint libraryId, byte[] payload)
    {
        var bytes = new byte[RwChunk.HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), declaredSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), libraryId);
        payload.CopyTo(bytes, RwChunk.HeaderLength);
        return bytes;
    }

    private static byte[] BinMeshBody(uint flags, params (uint Material, uint[] Indices)[] splits)
    {
        var body = new List<byte>();

        void U32(uint v)
        {
            var w = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(w, v);
            body.AddRange(w);
        }

        U32(flags);
        U32((uint)splits.Length);
        U32((uint)splits.Sum(s => s.Indices.Length));
        foreach (var (material, indices) in splits)
        {
            U32((uint)indices.Length);
            U32(material);
            foreach (var index in indices)
            {
                U32(index);
            }
        }

        return [.. body];
    }

    // ---------------------------------------------------------------- BINMESH

    [Fact]
    public void ABinMeshParsesItsSplitsAndAgreesWithItsOwnTotal()
    {
        var body = BinMeshBody(RwBinMesh.TriangleStripFlag, (2u, [0u, 1u, 2u, 3u]), (5u, [9u, 8u]));

        var mesh = RwBinMesh.TryParse(body);

        Assert.NotNull(mesh);
        Assert.True(mesh.IsTriangleStrip);
        Assert.Equal(2, mesh.Splits.Count);
        Assert.Equal(2u, mesh.Splits[0].MaterialIndex);
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, mesh.Splits[0].Indices);
        Assert.Equal(5u, mesh.Splits[1].MaterialIndex);
        Assert.Equal(6, mesh.TotalIndices);
        Assert.True(mesh.TotalAgrees);
    }

    /// <summary>
    ///     ⚠ An empty BINMESH — twelve bytes, zero meshes — is VALID and is 2,513 of the 8,578
    ///     retail chunks. Guarding against a zero mesh count reports the format as only 71% matched
    ///     and sends a reader looking for a variant that does not exist. That is exactly the false
    ///     negative this test exists to prevent recurring.
    /// </summary>
    [Fact]
    public void AnEmptyBinMeshIsValidNotAFailure()
    {
        var body = BinMeshBody(0);

        var mesh = RwBinMesh.TryParse(body);

        Assert.NotNull(mesh);
        Assert.Equal(RwBinMesh.HeaderLength, body.Length);
        Assert.Empty(mesh.Splits);
        Assert.Equal(0, mesh.TotalIndices);
        Assert.True(mesh.TotalAgrees);
        Assert.False(mesh.IsTriangleStrip);
    }

    /// <summary>
    ///     A body that does not tile exactly is rejected. Accepting a trailing remainder would let a
    ///     mis-located chunk through as plausible geometry, which is far worse than a null.
    /// </summary>
    [Fact]
    public void ABodyWithATrailingRemainderIsRejected()
    {
        var body = BinMeshBody(0, (0u, [1u, 2u, 3u])).Concat(new byte[] { 1, 2, 3, 4 }).ToArray();

        Assert.Null(RwBinMesh.TryParse(body));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(11)]
    public void ATruncatedBodyIsRejected(int length)
    {
        Assert.Null(RwBinMesh.TryParse(new byte[length]));
    }

    /// <summary>An index count that runs past the body is rejected rather than read out of range.</summary>
    [Fact]
    public void AnIndexCountPastTheBodyIsRejected()
    {
        var body = BinMeshBody(0, (0u, [1u, 2u]));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(RwBinMesh.HeaderLength), 1_000_000);

        Assert.Null(RwBinMesh.TryParse(body));
    }

    /// <summary>
    ///     The header's redundant total is preserved rather than recomputed, so a caller can detect
    ///     a stream where the two disagree. On retail they never do.
    /// </summary>
    [Fact]
    public void ADisagreeingTotalIsReportedNotSilentlyCorrected()
    {
        var body = BinMeshBody(0, (0u, [1u, 2u, 3u]));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 99);

        var mesh = RwBinMesh.TryParse(body);

        Assert.NotNull(mesh);
        Assert.Equal(99u, mesh.DeclaredIndexCount);
        Assert.Equal(3, mesh.TotalIndices);
        Assert.False(mesh.TotalAgrees);
    }

    // ---------------------------------------------------------------- the chunk walk

    /// <summary>
    ///     ⚠ 0x1300 declares a size eight bytes larger than its body. Untreated, the walk steps past
    ///     the next header and the remainder of the stream vanishes with no error at all.
    /// </summary>
    [Fact]
    public void TheOvershootingChunkDoesNotSwallowItsSuccessor()
    {
        var overshooting = Chunk(RwChunk.SizeOvershootType, 16 + RwChunk.SizeOvershoot, 0, new byte[16]);
        var next = Chunk(RwChunk.Geometry, 4, 0x1C020065, new byte[4]);
        var stream = overshooting.Concat(next).ToArray();

        var chunks = RwChunk.Siblings(stream, 0, stream.Length).ToArray();

        Assert.Equal(2, chunks.Length);
        Assert.Equal(RwChunk.SizeOvershootType, chunks[0].Type);
        Assert.Equal(16, chunks[0].Size);
        Assert.Equal(RwChunk.Geometry, chunks[1].Type);
    }

    [Fact]
    public void AHeaderWhosePayloadRunsPastTheEndIsRejected()
    {
        var stream = Chunk(RwChunk.Clump, 9_999, 0, new byte[8]);

        Assert.False(RwChunk.TryRead(stream, 0, stream.Length, out _));
        Assert.Empty(RwChunk.Siblings(stream, 0, stream.Length));
    }

    [Fact]
    public void SiblingsStopsAtTheFirstMalformedHeaderRatherThanThrowing()
    {
        var good = Chunk(RwChunk.Struct, 4, 0, new byte[4]);
        var bad = Chunk(RwChunk.Clump, 9_999, 0, new byte[4]);
        var stream = good.Concat(bad).ToArray();

        var chunks = RwChunk.Siblings(stream, 0, stream.Length).ToArray();

        Assert.Single(chunks);
        Assert.Equal(RwChunk.Struct, chunks[0].Type);
    }

    [Fact]
    public void TryFindChildLocatesADirectChildOnly()
    {
        var first = Chunk(RwChunk.String, 4, 0, new byte[4]);
        var second = Chunk(RwChunk.BinMeshPlugin, 12, 0, BinMeshBody(0));
        var stream = first.Concat(second).ToArray();

        Assert.True(RwChunk.TryFindChild(stream, 0, stream.Length, RwChunk.BinMeshPlugin, out var found));
        Assert.Equal(first.Length + RwChunk.HeaderLength, found.PayloadOffset);
        Assert.False(RwChunk.TryFindChild(stream, 0, stream.Length, RwChunk.Atomic, out _));
    }

    [Fact]
    public void ContainerTypesAreTheOnesWhosePayloadNests()
    {
        Assert.True(RwChunk.IsContainer(RwChunk.Clump));
        Assert.True(RwChunk.IsContainer(RwChunk.World));
        Assert.True(RwChunk.IsContainer(RwChunk.Extension));
        Assert.False(RwChunk.IsContainer(RwChunk.BinMeshPlugin));
        Assert.False(RwChunk.IsContainer(RwChunk.TextureNative));
    }

    // ---------------------------------------------------------------- the library-id correction

    /// <summary>
    ///     ⚠ The upstream code compares the RAW library word against a constant. Every id in both
    ///     corpora happens to land on the correct side, so the bug is invisible — which is why the
    ///     real unpack is implemented and pinned instead of the comparison.
    /// </summary>
    [Fact]
    public void TheLibraryIdIsUnpackedNotCompared()
    {
        // The id every resolved Oblivion PSP payload carries.
        var version = RwLibraryVersion.Unpack(0x1C020065);

        Assert.NotEqual(0x1C020065u, version);
        Assert.Equal(3u, (version >> 16) & 0xF);
        Assert.Equal("3.7.0.2", RwLibraryVersion.Describe(0x1C020065));
    }

    /// <summary>An id with no high half is the old pre-3.1 encoding and is shifted, not unpacked.</summary>
    [Fact]
    public void AnOldStyleLibraryIdUsesTheShiftedForm()
    {
        Assert.Equal(0x0310u << 8, RwLibraryVersion.Unpack(0x0310));
    }
}