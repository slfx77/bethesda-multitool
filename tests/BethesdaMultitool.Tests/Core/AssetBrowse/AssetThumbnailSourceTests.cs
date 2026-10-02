using System.Buffers.Binary;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Pins the description <see cref="AssetThumbnailSource.TryRender(AssetBrowseSession, AssetNode, int, DdsThumbnailProducer?, double, CancellationToken, out AssetImageInfo?)" />
///     publishes beside its thumbnail: the asset's own first-frame size and frame count, never the scaled cell size.
/// </summary>
public sealed class AssetThumbnailSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bmt-thumb-info-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A three-frame 6x4 FRM thumbnails to a 2-pixel cell yet is described at its stored size with all three frames.</summary>
    [Fact]
    public void SpriteIsDescribedAtItsStoredSizeAndFrameCount()
    {
        Write("ART.FRM", Frm(frames: 3, width: 6, height: 4));
        Write("COLOR.PAL", Palette768(7, 63, 0, 0));
        using var session = Open();
        var thumbnail = AssetThumbnailSource.TryRender(session, Node(session, "ART.FRM"), 2, null, 1,
            TestContext.Current.CancellationToken, out var info);

        Assert.True(thumbnail.HasValue);
        Assert.True(thumbnail.Value.Width <= 2 && thumbnail.Value.Height <= 2);
        Assert.Equal(new AssetImageInfo(6, 4, 3), info);
    }

    /// <summary>A DDS decoded on the direct path is described as one frame at its header size.</summary>
    [Fact]
    public void TextureIsDescribedAsOneFrameAtItsHeaderSize()
    {
        Write("texture.dds", Dds(width: 4, height: 4));
        using var session = Open();
        var thumbnail = AssetThumbnailSource.TryRender(session, Node(session, "texture.dds"), 2, null, 1,
            TestContext.Current.CancellationToken, out var info);

        Assert.True(thumbnail.HasValue);
        Assert.Equal(new AssetImageInfo(4, 4, 1), info);
    }

    /// <summary>The shorter overload still renders and a node with no picture describes nothing.</summary>
    [Fact]
    public void NonPictureNodesDescribeNothing()
    {
        Write("notes.txt", [1, 2, 3]);
        using var session = Open();
        var node = Node(session, "notes.txt");

        Assert.False(AssetThumbnailSource.CanRender(node));
        Assert.Null(AssetThumbnailSource.TryRender(session, node, 2, null, 1, TestContext.Current.CancellationToken, out var info));
        Assert.Null(info);
        Assert.Null(node.ImageInfo);
    }

    private void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_root, name), bytes);

    private AssetBrowseSession Open()
    {
        var fileSystem = new LooseFileSystem(_root);
        return new AssetBrowseSession(fileSystem, "fixture", _root, AssetTreeBuilder.Build(fileSystem, "fixture"));
    }

    private static AssetNode Node(AssetBrowseSession session, string name) =>
        Assert.Single(session.Root.Children, node => node.Name == name);

    /// <summary>An FRM with <paramref name="frames" /> equal frames of palette index seven in direction zero.</summary>
    private static byte[] Frm(int frames, int width, int height)
    {
        var header = new byte[FalloutFrmFile.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, FalloutFrmFile.RetailVersion);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)frames);
        var body = new List<byte>();
        for (var i = 0; i < frames; i++)
        {
            var frame = new byte[FalloutFrmFile.FrameHeaderLength];
            BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)height);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)(width * height));
            body.AddRange(frame);
            var pixels = new byte[width * height];
            Array.Fill(pixels, (byte)7);
            body.AddRange(pixels);
        }
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(FalloutFrmFile.FrameAreaSizePosition), (uint)body.Count);
        return [.. header, .. body];
    }

    /// <summary>A 768-byte 6-bit palette where entry <paramref name="index" /> is a known colour.</summary>
    private static byte[] Palette768(int index, byte r, byte g, byte b)
    {
        var rgb = new byte[768];
        rgb[index * 3] = r;
        rgb[index * 3 + 1] = g;
        rgb[index * 3 + 2] = b;
        return rgb;
    }

    /// <summary>A complete 136-byte solid BC1 DDS of the given size (one 4x4 block).</summary>
    private static byte[] Dds(int width, int height)
    {
        var bytes = new byte[136];
        "DDS "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x21007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 4);
        "DXT1"u8.CopyTo(bytes.AsSpan(84));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(128), 0xf800);
        return bytes;
    }
}
