using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Synthetic vectors for <see cref="RedguardGxaFile" />, shaped after the 65 retail files
///     surveyed 2026-09-04. The container is mixed-endian — big-endian chunk lengths around
///     little-endian values — so the endianness cases carry real weight here.
/// </summary>
public sealed class RedguardGxaFileTests
{
    private static byte[] Chunk(string tag, byte[] payload)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        return [.. Encoding.ASCII.GetBytes(tag), .. length, .. payload];
    }

    private static byte[] Header(int imageCount)
    {
        var payload = new byte[RedguardGxaFile.ImageHeaderLength];
        Encoding.ASCII.GetBytes(RedguardGxaFile.Banner.PadRight(RedguardGxaFile.BannerLength))
            .CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(RedguardGxaFile.BannerLength), (ushort)imageCount);
        return payload;
    }

    /// <summary>A 6-bit VGA ramp: index i maps to (i/4, i/4, i/4) before promotion.</summary>
    private static byte[] SixBitPalette()
    {
        var rgb = new byte[Palette.RgbByteCount];
        for (var i = 0; i < Palette.EntryCount; i++)
        {
            var v = (byte)(i / 4);
            rgb[i * 3] = v;
            rgb[i * 3 + 1] = v;
            rgb[i * 3 + 2] = v;
        }

        return rgb;
    }

    private static byte[] Frame(int width, int height, byte fill, byte form = 0)
    {
        var header = new byte[RedguardGxaFile.FrameHeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)height);
        header[10] = form;

        var pixels = new byte[width * height];
        Array.Fill(pixels, fill);
        return [.. header, .. pixels];
    }

    private static byte[] Build(int declaredCount, params byte[][] frames)
    {
        var bitmap = frames.SelectMany(static f => f).ToArray();
        return
        [
            .. Chunk("BMHD", Header(declaredCount)),
            .. Chunk("BPAL", SixBitPalette()),
            .. Chunk("BBMP", bitmap),
            .. "END "u8.ToArray()
        ];
    }

    [Fact]
    public void Parse_SingleFrame_ReadsDimensionsAndPixels()
    {
        var file = RedguardGxaFile.Parse(Build(1, Frame(4, 3, 7)), "TEST.GXA");

        var frame = Assert.Single(file.Frames);
        Assert.Equal((4, 3), (frame.Width, frame.Height));
        Assert.Equal(12, frame.Indices.Length);
        Assert.All(frame.Indices, index => Assert.Equal(7, index));
        Assert.Equal(0, file.CompressedFrames);
    }

    [Fact]
    public void Parse_ManyFrames_WalksThemSequentiallyAtTheirOwnSizes()
    {
        // GXICONS-style: one file, many frames, and they are NOT all the same size — so the walk
        // has to step by each frame's own dimensions rather than a single stride.
        var file = RedguardGxaFile.Parse(
            Build(3, Frame(2, 2, 1), Frame(5, 1, 2), Frame(3, 4, 3)), "ICONS.GXA");

        Assert.Equal([(2, 2), (5, 1), (3, 4)], file.Frames.Select(f => (f.Width, f.Height)));
        Assert.Equal([1, 2, 3], file.Frames.Select(f => f.Indices[0]));
    }

    [Fact]
    public void Parse_PaletteIsPromotedFromSixBit()
    {
        var file = RedguardGxaFile.Parse(Build(1, Frame(1, 1, 255)), "TEST.GXA");

        // Retail GXA palettes top out at 63, so the reader must promote rather than pass through:
        // the ramp's last entry is 63, which promotes to 255 and not to 63.
        var rgba = file.Palette.Rgba;
        Assert.Equal(Palette.EntryCount * 4, rgba.Length);
        Assert.Equal(255, rgba[255 * 4]);
        Assert.Equal(0, rgba[0]);
    }

    [Fact]
    public void Parse_ChunkLengthsAreBigEndian()
    {
        // The one property most likely to be got wrong: values inside are little-endian while the
        // chunk lengths around them are big-endian. A little-endian length here reads as huge.
        var bytes = Build(1, Frame(4, 3, 7));
        var declared = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4));

        Assert.Equal((uint)RedguardGxaFile.ImageHeaderLength, declared);
        Assert.NotEqual((uint)RedguardGxaFile.ImageHeaderLength,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
    }

    [Fact]
    public void Parse_CompressedFrame_IsReportedRatherThanDecodedWrong()
    {
        // The six retail files that fail the raw walk flag it at the frame header's +10 byte.
        // Decoding those bytes as pixels would silently produce garbage for every later frame.
        var file = RedguardGxaFile.Parse(Build(2, Frame(2, 2, 1), Frame(2, 2, 9, 1)), "GUI.GXA");

        Assert.Single(file.Frames);
        Assert.Equal(1, file.CompressedFrames);
    }

    [Fact]
    public void Parse_FrameDeclaringMorePixelsThanRemain_StopsAndCounts()
    {
        var bytes = Build(1, Frame(4, 3, 7));

        // Inflate the declared width so the payload can no longer hold the frame.
        var frameStart = bytes.Length - 4 - (RedguardGxaFile.FrameHeaderLength + 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(frameStart + 2), 400);

        var file = RedguardGxaFile.Parse(bytes, "TRUNC.GXA");

        Assert.Empty(file.Frames);
        Assert.Equal(1, file.CompressedFrames);
    }

    [Fact]
    public void Parse_WrongChunkOrder_Throws()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Chunk("BMHD", Header(1)));
        bytes.AddRange(Chunk("BBMP", Frame(1, 1, 0)));

        var error = Assert.Throws<InvalidDataException>(() => RedguardGxaFile.Parse([.. bytes], "BAD.GXA"));

        Assert.Contains("BPAL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_BitmapLengthWrittenAsTerminatorOffset_IsAcceptedExactly()
    {
        // GXICONS.GXA and gui.gxa store BBMP's big-endian length as the absolute offset of "END "
        // (fileLength - 4) instead of a payload length. The reader takes exactly that and no other
        // overrun: one byte off in either direction must still fail as a plain bad chunk.
        var bytes = Build(1, Frame(3, 2, 5));
        var lengthAt = bytes.Length - 4 - (RedguardGxaFile.FrameHeaderLength + 6) - 4;
        var good = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(good.AsSpan(lengthAt), (uint)(bytes.Length - 4));

        var file = RedguardGxaFile.Parse(good, "GUI.GXA");
        Assert.Equal((3, 2), (Assert.Single(file.Frames).Width, file.Frames[0].Height));

        var offByOne = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(offByOne.AsSpan(lengthAt), (uint)(bytes.Length - 3));
        Assert.Throws<InvalidDataException>(() => RedguardGxaFile.Parse(offByOne, "BAD.GXA"));
    }

    [Fact]
    public void Probe_AcceptsAGxaAndRejectsOtherFormats()
    {
        Assert.True(RedguardGxaFile.TryProbe(Build(1, Frame(1, 1, 0))));
        Assert.False(RedguardGxaFile.TryProbe("OARC"u8.ToArray()));
        Assert.False(RedguardGxaFile.TryProbe([]));
        Assert.False(RedguardGxaFile.TryProbe(Encoding.ASCII.GetBytes(new string('x', 64))));
    }
}