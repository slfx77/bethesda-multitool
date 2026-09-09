using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for the Oblivion PSP descriptor-to-payload join.
///     <para>
///         The rule under test — a <c>0x0716</c> payload begins at <c>headerLength + 8</c> — was
///         settled against the retail builds, where it resolves 2,463 resources and, crucially,
///         resolves EXACTLY the three RenderWare-payload types and none of the other 6,485. These
///         tests pin the mechanics; <see cref="OblivionPspResourceRetailTests" /> re-derives that
///         population split from the bytes.
///     </para>
/// </summary>
public sealed class OblivionPspResourceReaderTests
{
    /// <summary>The three RenderWare-payload type names, in the order the walk returns them.</summary>
    private static readonly string[] RenderWareTypeNames = ["rwID_CLUMP", "rwID_TEXDICTIONARY", "rwID_WORLD"];

    /// <summary>Builds a 12-byte chunk header plus body.</summary>
    private static byte[] Chunk(uint type, uint declaredSize, uint libraryId, byte[] body)
    {
        var bytes = new byte[OblivionPspResourceReader.ChunkHeaderLength + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), declaredSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), libraryId);
        body.CopyTo(bytes, OblivionPspResourceReader.ChunkHeaderLength);
        return bytes;
    }

    /// <summary>
    ///     A named-resource wrapper: header length, a version word, a GUID, the type name, the
    ///     authoring path, padding to headerLength, then the payload at headerLength + 8.
    /// </summary>
    private static byte[] Wrapper(string typeName, string path, byte[] payload, int headerLength = 0x60)
    {
        var body = new byte[headerLength + OblivionPspResourceReader.PayloadGap + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)headerLength);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 4);
        for (var i = 8; i < 24; i++)
        {
            body[i] = (byte)(0xA0 + i);
        }

        var name = Encoding.ASCII.GetBytes(typeName);
        name.CopyTo(body, 28);
        var pathBytes = Encoding.ASCII.GetBytes(path);
        pathBytes.CopyTo(body, 28 + name.Length + 2);

        payload.CopyTo(body, headerLength + OblivionPspResourceReader.PayloadGap);
        return body;
    }

    /// <summary>A minimal RenderWare stream: one chunk of the given root id.</summary>
    private static byte[] RwStream(uint rootId, int payloadBytes = 16)
    {
        return Chunk(rootId, (uint)payloadBytes, OblivionPspResourceReader.RetailLibraryId, new byte[payloadBytes]);
    }

    // ---------------------------------------------------------------- the join

    /// <summary>
    ///     ⚑ The payload starts at headerLength + 8, and the root chunk and library id come back
    ///     with it.
    /// </summary>
    [Fact]
    public void APayloadIsFoundAtHeaderLengthPlusEight()
    {
        var wrapper = Wrapper("rwID_CLUMP", @"z:\oblivion\design\zones\a.max", RwStream(0x10, 32));
        var entry = Chunk(
            OblivionPspResourceReader.NamedResourceChunk, (uint)wrapper.Length, 0, wrapper);

        var resource = Assert.Single(OblivionPspResourceReader.ReadResources(entry));

        Assert.Equal("rwID_CLUMP", resource.TypeName);
        Assert.Equal(@"z:\oblivion\design\zones\a.max", resource.AuthoringPath);
        Assert.True(resource.IsRenderWareStream);
        Assert.Equal(0x10u, resource.RootChunkType);
        Assert.Equal(OblivionPspResourceReader.RetailLibraryId, resource.LibraryId);
        Assert.Equal(
            OblivionPspResourceReader.ChunkHeaderLength + 0x60 + OblivionPspResourceReader.PayloadGap,
            resource.PayloadOffset);
    }

    /// <summary>
    ///     ⚠ A resource whose payload is NOT RenderWare is reported as such rather than forced
    ///     through a chunk walk. On retail this is the majority — animations, audio, conversations
    ///     and quests — and calling them malformed streams would be worse than useless.
    /// </summary>
    [Fact]
    public void ANonRenderWarePayloadIsReportedNotForced()
    {
        var payload = Encoding.ASCII.GetBytes("this is not a chunk stream at all, just bytes");
        var wrapper = Wrapper("rwID_CONVERSATION", @"z:\oblivion\text\conv.xml", payload);
        var entry = Chunk(
            OblivionPspResourceReader.NamedResourceChunk, (uint)wrapper.Length, 0, wrapper);

        var resource = Assert.Single(OblivionPspResourceReader.ReadResources(entry));

        Assert.Equal("rwID_CONVERSATION", resource.TypeName);
        Assert.False(resource.IsRenderWareStream);
        Assert.Equal(0u, resource.RootChunkType);
        Assert.True(resource.PayloadLength > 0, "The payload is still located even when it is not a stream.");
    }

    [Theory]
    [InlineData(0x10u)] // CLUMP
    [InlineData(0x0Bu)] // WORLD
    [InlineData(0x16u)] // TEXDICTIONARY
    public void TheThreeRenderWareRootsAreRecognised(uint rootId)
    {
        var wrapper = Wrapper("rwID_X", @"z:\a\b", RwStream(rootId));
        var entry = Chunk(OblivionPspResourceReader.NamedResourceChunk, (uint)wrapper.Length, 0, wrapper);

        Assert.True(Assert.Single(OblivionPspResourceReader.ReadResources(entry)).IsRenderWareStream);
    }

    /// <summary>
    ///     The root-id set is closed. A permissive check would call any four bytes a chunk header
    ///     and report a stream where there is none, which is exactly how a decoder ends up
    ///     confidently walking garbage.
    /// </summary>
    [Fact]
    public void AnUnknownRootIdIsNotAStream()
    {
        var wrapper = Wrapper("rwID_X", @"z:\a\b", RwStream(0x0704));
        var entry = Chunk(OblivionPspResourceReader.NamedResourceChunk, (uint)wrapper.Length, 0, wrapper);

        Assert.False(Assert.Single(OblivionPspResourceReader.ReadResources(entry)).IsRenderWareStream);
    }

    // ---------------------------------------------------------------- the 0x1300 trap

    /// <summary>
    ///     ⚠ <c>0x1300</c> declares a size eight bytes larger than its body. Trusting it walks the
    ///     reader past the next header and silently drops the rest of the entry — the failure is
    ///     invisible because the walk simply ends early with no error.
    /// </summary>
    [Fact]
    public void TheOvershootingChunkDoesNotDesynchroniseTheWalk()
    {
        var body = new byte[16];
        var overshooting = Chunk(
            OblivionPspResourceReader.OvershootingChunk,
            (uint)(body.Length + OblivionPspResourceReader.SizeOvershoot), 0, body);

        var wrapper = Wrapper("rwID_WORLD", @"z:\w", RwStream(0x0B));
        var resourceChunk = Chunk(
            OblivionPspResourceReader.NamedResourceChunk, (uint)wrapper.Length, 0, wrapper);

        var entry = new byte[overshooting.Length + resourceChunk.Length];
        overshooting.CopyTo(entry, 0);
        resourceChunk.CopyTo(entry, overshooting.Length);

        var resources = OblivionPspResourceReader.ReadResources(entry);

        Assert.Single(resources);
        Assert.Equal("rwID_WORLD", resources[0].TypeName);
    }

    [Fact]
    public void ReadChunkSize_AppliesTheOvershootOnlyToThatChunk()
    {
        var overshooting = Chunk(OblivionPspResourceReader.OvershootingChunk, 100, 0, []);
        var ordinary = Chunk(OblivionPspResourceReader.BulkDataChunk, 100, 0, []);

        Assert.Equal(100 - OblivionPspResourceReader.SizeOvershoot,
            OblivionPspResourceReader.ReadChunkSize(overshooting, 0));
        Assert.Equal(100, OblivionPspResourceReader.ReadChunkSize(ordinary, 0));
    }

    // ---------------------------------------------------------------- walking

    /// <summary>Several resources in one entry all come back, in order.</summary>
    [Fact]
    public void EveryNamedResourceInAnEntryIsReturned()
    {
        var parts = new List<byte>();
        foreach (var (type, root) in new[]
                 {
                     ("rwID_CLUMP", 0x10u), ("rwID_TEXDICTIONARY", 0x16u), ("rwID_WORLD", 0x0Bu)
                 })
        {
            var wrapper = Wrapper(type, @"z:\p", RwStream(root));
            parts.AddRange(Chunk(
                OblivionPspResourceReader.NamedResourceChunk, (uint)wrapper.Length, 0, wrapper));

            // Interleave bulk chunks, which must be walked past and not reported.
            parts.AddRange(Chunk(OblivionPspResourceReader.BulkDataChunk, 8, 0, new byte[8]));
        }

        var resources = OblivionPspResourceReader.ReadResources([.. parts]);

        Assert.Equal(RenderWareTypeNames, resources.Select(r => r.TypeName));
        Assert.All(resources, r => Assert.True(r.IsRenderWareStream));
    }

    /// <summary>
    ///     A truncated entry surfaces what it has rather than throwing: these are cancelled-build
    ///     packs and a partial read is more useful than an exception.
    /// </summary>
    [Fact]
    public void ATruncatedEntryEndsTheWalkWithoutThrowing()
    {
        var wrapper = Wrapper("rwID_CLUMP", @"z:\a", RwStream(0x10));
        var full = Chunk(OblivionPspResourceReader.NamedResourceChunk, (uint)wrapper.Length, 0, wrapper);
        var truncated = full[..(full.Length - 8)];

        var resources = OblivionPspResourceReader.ReadResources(truncated);

        Assert.Empty(resources);
    }

    [Fact]
    public void AnEmptyEntryYieldsNothing()
    {
        Assert.Empty(OblivionPspResourceReader.ReadResources([]));
        Assert.Empty(OblivionPspResourceReader.ReadResources(new byte[6]));
    }

    /// <summary>The type-name scan is bounded to the header and cannot reach into the payload.</summary>
    [Fact]
    public void TheTypeNameScanCannotReachThePayload()
    {
        var payload = Encoding.ASCII.GetBytes("rwID_NOTTHEHEADER");
        var wrapper = Wrapper(string.Empty, string.Empty, payload);
        var entry = Chunk(OblivionPspResourceReader.NamedResourceChunk, (uint)wrapper.Length, 0, wrapper);

        Assert.Equal(string.Empty, Assert.Single(OblivionPspResourceReader.ReadResources(entry)).TypeName);
    }
}