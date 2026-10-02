using System.Buffers.Binary;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Pins the full-size preview decode: every frame at its stored size through the thumbnail's own decode
///     path, described by <see cref="AssetImageInfo" />, and declined rather than decoded above the pixel cap.
/// </summary>
public sealed class AssetImagePreviewSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bmt-preview-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A 4x4 BC1 DDS comes back as one unscaled frame of solid red, which is what separates this from the thumbnail.</summary>
    [Fact]
    public void TextureDecodesToOneFrameAtItsStoredSize()
    {
        Write("texture.dds", Dds(0xf800));
        using var session = Open();
        var node = Node(session, "texture.dds");

        var preview = AssetImagePreviewSource.TryLoad(session, node, TestContext.Current.CancellationToken);

        Assert.NotNull(preview);
        Assert.False(preview.IsDeclined);
        Assert.Null(preview.DeclineReasonKey);
        var frame = Assert.Single(preview.Frames);
        Assert.Equal("texture.dds", frame.Label);
        Assert.Equal(4, frame.Width);
        Assert.Equal(4, frame.Height);
        Assert.Equal(64, frame.Rgba.Length);
        Assert.Equal(AssetImageInfo.Single(4, 4), preview.Info);
        for (var pixel = 0; pixel < 16; pixel++)
        {
            Assert.Equal(255, frame.Rgba[pixel * 4]);
            Assert.Equal(0, frame.Rgba[pixel * 4 + 1]);
            Assert.Equal(0, frame.Rgba[pixel * 4 + 2]);
            Assert.Equal(255, frame.Rgba[pixel * 4 + 3]);
        }

        // The thumbnail of the same node is scaled to its cell; the preview is not.
        var thumbnail = AssetThumbnailSource.TryRender(session, node, 2, null, 1, TestContext.Current.CancellationToken);
        Assert.True(thumbnail.HasValue);
        Assert.True(thumbnail.Value.Width < frame.Width);
    }

    /// <summary>A three-frame FRM yields every frame with its stored geometry and palette-resolved red, described as three frames.</summary>
    [Fact]
    public void SpriteDecodesEveryFrame()
    {
        Write("ART.FRM", Frm(frames: 3, width: 6, height: 4));
        Write("COLOR.PAL", Palette768(7, 63, 0, 0));
        using var session = Open();

        var preview = AssetImagePreviewSource.TryLoad(session, Node(session, "ART.FRM"), TestContext.Current.CancellationToken);

        Assert.NotNull(preview);
        Assert.False(preview.IsDeclined);
        Assert.Equal(3, preview.Frames.Count);
        Assert.Equal(new AssetImageInfo(6, 4, 3), preview.Info);
        Assert.All(preview.Frames, frame =>
        {
            Assert.Equal(6, frame.Width);
            Assert.Equal(4, frame.Height);
            Assert.Equal(frame.Width * frame.Height * 4, frame.Rgba.Length);
            Assert.Equal(255, frame.Rgba[0]);
            Assert.Equal(0, frame.Rgba[1]);
            Assert.Equal(0, frame.Rgba[2]);
        });
        Assert.Equal(3, preview.Frames.Select(frame => frame.Label).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A sound is not a picture, so nothing is loaded for it.</summary>
    [Fact]
    public void NonPictureNodeLoadsNothing()
    {
        Write("beep.wav", [1, 2, 3, 4]);
        using var session = Open();
        var node = Node(session, "beep.wav");
        Assert.False(AssetThumbnailSource.CanRender(node));

        Assert.Null(AssetImagePreviewSource.TryLoad(session, node, TestContext.Current.CancellationToken));
    }

    /// <summary>A picture whose bytes have gone missing since the tree was built loads nothing rather than throwing.</summary>
    [Fact]
    public void MissingBytesLoadNothing()
    {
        Write("gone.dds", Dds(0xf800));
        using var session = Open();
        var node = Node(session, "gone.dds");
        File.Delete(Path.Combine(_root, "gone.dds"));
        Assert.Null(session.FileSystem.TryReadAllBytes(node.VirtualPath));

        Assert.Null(AssetImagePreviewSource.TryLoad(session, node, TestContext.Current.CancellationToken));
    }

    /// <summary>A file named .dds that is not a DDS loads nothing, exactly as its thumbnail would.</summary>
    [Fact]
    public void GarbageTextureLoadsNothing()
    {
        var garbage = new byte[200];
        for (var index = 0; index < garbage.Length; index++)
        {
            garbage[index] = (byte)(index * 31 + 7);
        }

        Write("garbage.dds", garbage);
        using var session = Open();
        var node = Node(session, "garbage.dds");

        Assert.Null(AssetImagePreviewSource.TryLoad(session, node, TestContext.Current.CancellationToken));
        Assert.Null(AssetThumbnailSource.TryRender(session, node, 2, null, 1, TestContext.Current.CancellationToken));
    }

    /// <summary>A token canceled before the call surfaces as cancellation, never as a null preview.</summary>
    [Fact]
    public void PreCanceledTokenThrows()
    {
        Write("texture.dds", Dds(0xf800));
        using var session = Open();
        var node = Node(session, "texture.dds");

        Assert.Throws<OperationCanceledException>(() => AssetImagePreviewSource.TryLoad(session, node, new CancellationToken(canceled: true)));
    }

    /// <summary>The pixel cap is 4096 x 4096 and a declined preview carries its reason, no frames, and whatever description it was given.</summary>
    [Fact]
    public void PixelCapAndDeclinedShape()
    {
        Assert.Equal(16_777_216L, AssetImagePreviewSource.MaximumFramePixels);
        Assert.Equal(4096L * 4096L, AssetImagePreviewSource.MaximumFramePixels);
        Assert.Equal("AssetPreview_TooLarge", AssetImagePreviewSource.TooLargeReasonKey);

        var bare = AssetImagePreview.Declined("AssetPreview_TooLarge");
        Assert.True(bare.IsDeclined);
        Assert.Equal("AssetPreview_TooLarge", bare.DeclineReasonKey);
        Assert.Empty(bare.Frames);
        Assert.Null(bare.Info);

        var described = AssetImagePreview.Declined("AssetPreview_TooLarge", AssetImageInfo.Single(8192, 8192));
        Assert.True(described.IsDeclined);
        Assert.Empty(described.Frames);
        Assert.Equal(AssetImageInfo.Single(8192, 8192), described.Info);

        Assert.Throws<ArgumentException>(() => AssetImagePreview.Declined(" "));
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

    /// <summary>A 768-byte 6-bit palette where entry <paramref name="index" /> is a known color.</summary>
    private static byte[] Palette768(int index, byte r, byte g, byte b)
    {
        var rgb = new byte[768];
        rgb[index * 3] = r;
        rgb[index * 3 + 1] = g;
        rgb[index * 3 + 2] = b;
        return rgb;
    }

    /// <summary>A complete 136-byte solid 4x4 BC1 DDS whose one block is <paramref name="color" /> in RGB565.</summary>
    private static byte[] Dds(ushort color)
    {
        var bytes = new byte[136];
        "DDS "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x21007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 4);
        "DXT1"u8.CopyTo(bytes.AsSpan(84));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(128), color);
        return bytes;
    }
}
