using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>Small malformed wrappers must not escape their containing entry or hide later complete resources.</summary>
public sealed class OblivionPspResourceBoundsTests
{
    /// <summary>Large declarations and incomplete ordinary headers remain bounded nonstream resources.</summary>
    /// <param name="headerLength">Original unsigned wrapper declaration.</param>
    /// <param name="payloadOffset">Independent expected offset in the forty-four-byte entry.</param>
    /// <param name="payloadLength">Remaining bounded bytes after that offset.</param>
    [Theory]
    [InlineData(0u, 20, 24)]
    [InlineData(16u, 36, 8)]
    [InlineData(4096u, 44, 0)]
    [InlineData(2147483628u, 44, 0)]
    [InlineData(2147483639u, 44, 0)]
    [InlineData(2147483640u, 44, 0)]
    [InlineData(2147483647u, 44, 0)]
    [InlineData(uint.MaxValue, 44, 0)]
    public void InvalidWrapperKeepsBoundedNonstreamProvenance(uint headerLength, int payloadOffset, int payloadLength)
    {
        var body = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(body, headerLength);
        var entry = Chunk(OblivionPspResourceReader.NamedResourceChunk, (uint)body.Length, body);
        var original = (byte[])entry.Clone();

        var resource = Assert.Single(OblivionPspResourceReader.ReadResources(entry));

        Assert.False(resource.IsRenderWareStream);
        Assert.Equal(0u, resource.RootChunkType);
        Assert.Equal(0u, resource.LibraryId);
        Assert.Equal((int)Math.Min(headerLength, int.MaxValue), resource.HeaderLength);
        Assert.InRange(resource.PayloadOffset, 12, entry.Length);
        Assert.InRange(resource.PayloadLength, 0, body.Length);
        Assert.Equal(entry.Length, resource.PayloadOffset + resource.PayloadLength);
        Assert.Equal(payloadOffset, resource.PayloadOffset);
        Assert.Equal(payloadLength, resource.PayloadLength);
        Assert.Equal(original, entry);
    }

    /// <summary>Caller offsets outside a complete header return the established invalid-size sentinel.</summary>
    /// <param name="position">Invalid start relative to a twelve-byte buffer.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(2147483636)]
    [InlineData(1)]
    [InlineData(13)]
    public void InvalidChunkOffsetDeclinesWithoutSlicing(int position)
    {
        var header = Chunk(OblivionPspResourceReader.BulkDataChunk, 100, []);
        var original = (byte[])header.Clone();
        Assert.Equal(-1, OblivionPspResourceReader.ReadChunkSize(header, position));
        Assert.Equal(original, header);
    }

    /// <summary>The last complete header remains admissible, including its unchanged ordinary size word.</summary>
    [Fact]
    public void LastCompleteHeaderKeepsDeclaredSize()
    {
        var bytes = new byte[19];
        Chunk(OblivionPspResourceReader.BulkDataChunk, 100, []).CopyTo(bytes, 7);
        Assert.Equal(100, OblivionPspResourceReader.ReadChunkSize(bytes, 7));
        Assert.Equal(-1, OblivionPspResourceReader.ReadChunkSize(bytes, 8));
    }

    /// <summary>An invalid wrapper between valid wrappers does not truncate the catalog or change later offsets.</summary>
    [Fact]
    public void OverflowingWrapperDoesNotHideFollowingResource()
    {
        var first = StreamWrapper(0x10);
        var badBody = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(badBody, 2147483639u);
        var malformed = Chunk(OblivionPspResourceReader.NamedResourceChunk, (uint)badBody.Length, badBody);
        var last = StreamWrapper(0x16);
        byte[] entry = [.. first, .. malformed, .. last];
        var original = (byte[])entry.Clone();

        var resources = OblivionPspResourceReader.ReadResources(entry);

        Assert.Equal(3, resources.Count);
        Assert.True(resources[0].IsRenderWareStream);
        Assert.Equal(0x10u, resources[0].RootChunkType);
        Assert.False(resources[1].IsRenderWareStream);
        Assert.Equal(first.Length + malformed.Length, resources[1].PayloadOffset);
        Assert.Equal(0, resources[1].PayloadLength);
        Assert.True(resources[2].IsRenderWareStream);
        Assert.Equal(0x16u, resources[2].RootChunkType);
        Assert.Equal(first.Length + malformed.Length + 36, resources[2].PayloadOffset);
        Assert.Equal(16, resources[2].PayloadLength);
        Assert.Equal(original, entry);
    }

    /// <summary>A partial final header keeps the existing complete prefix without inventing a new resource.</summary>
    [Fact]
    public void IncompleteTailPreservesCompletePrefix()
    {
        var prefix = StreamWrapper(0x10);
        byte[] entry = [.. prefix, 0x16, 0x07, 0, 0, 255, 255, 255, 255, 0, 0, 0];
        var resource = Assert.Single(OblivionPspResourceReader.ReadResources(entry));
        Assert.True(resource.IsRenderWareStream);
        Assert.Equal(36, resource.PayloadOffset);
        Assert.Equal(16, resource.PayloadLength);
    }

    /// <summary>The inherited undersized 0x1300 correction stays permissive and does not consume the next header.</summary>
    /// <param name="declaredSize">A declaration at or below the measured eight-byte overshoot.</param>
    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]
    [InlineData(8u)]
    public void UndersizedOvershootPolicyRemainsUnchanged(uint declaredSize)
    {
        var overshoot = Chunk(OblivionPspResourceReader.OvershootingChunk, declaredSize, []);
        Assert.Equal(0, OblivionPspResourceReader.ReadChunkSize(overshoot, 0));
        byte[] entry = [.. overshoot, .. StreamWrapper(0x16)];
        var resource = Assert.Single(OblivionPspResourceReader.ReadResources(entry));
        Assert.True(resource.IsRenderWareStream);
        Assert.Equal(0x16u, resource.RootChunkType);
        Assert.Equal(48, resource.PayloadOffset);
    }

    /// <summary>Creates a minimal valid wrapper with a sixteen-byte RenderWare root.</summary>
    /// <param name="root">Known resource root kind.</param>
    /// <returns>One complete named-resource chunk.</returns>
    private static byte[] StreamWrapper(uint root)
    {
        var body = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 16);
        Chunk(root, 4, new byte[4]).CopyTo(body, 24);
        return Chunk(OblivionPspResourceReader.NamedResourceChunk, (uint)body.Length, body);
    }

    /// <summary>Writes one source chunk while allowing a deliberately inconsistent declared body size.</summary>
    /// <param name="type">Source chunk identifier.</param>
    /// <param name="declaredSize">Serialized size word.</param>
    /// <param name="body">Actual bounded source body.</param>
    /// <returns>The complete synthetic source bytes.</returns>
    private static byte[] Chunk(uint type, uint declaredSize, byte[] body)
    {
        var bytes = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), declaredSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), OblivionPspResourceReader.RetailLibraryId);
        body.CopyTo(bytes, 12);
        return bytes;
    }
}
