using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Xngine;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     The cut-1c header-only path of <see cref="BsiFile" /> (plan D9 and section 8, slice 4):
///     <see cref="BsiFile.ReadHeaders" /> walks the chunk grammar the full parse walks and returns the
///     same geometry, frame counts, names and offsets, plus where every chunk lies, without touching a
///     DATA byte (control: a DATA payload the full parse rejects) and while seeing a changed width byte
///     (control). <see cref="BsiFileTests" /> is untouched; the chunk builder here is its own.
/// </summary>
public sealed class BsiFileHeaderTests
{
    /// <summary>A chunk: 4-byte tag, BIG-endian u32 length, payload.</summary>
    private static byte[] Chunk(string tag, params byte[] payload)
    {
        var header = new byte[8];
        Encoding.ASCII.GetBytes(tag.PadRight(4)).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)payload.Length);
        return [.. header, .. payload];
    }

    private static byte[] Header(int width, int height, int frames, int compression, int xOffset = 0, int yOffset = 0)
    {
        var header = new byte[26];
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(0), (short)xOffset);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(2), (short)yOffset);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(4), (short)width);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(6), (short)height);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(14), (short)frames);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(24), (short)compression);
        return header;
    }

    private static byte[] ColorMap(byte marker = 0)
    {
        var map = new byte[768];
        for (var i = 0; i < 256; i++)
        {
            map[i * 3] = (byte)(i / 4);
            map[i * 3 + 1] = (byte)(i / 4);
            map[i * 3 + 2] = (byte)(i / 4);
        }

        map[0] = marker;
        return map;
    }

    /// <summary>The retail chunk order: BSIF, IFHD, NAME, BHDR, HICL, HTBL, CMAP, DATA, END.</summary>
    private static byte[] Terrain()
    {
        return
        [
            .. Chunk("BSIF"),
            .. Chunk("IFHD", new byte[44]),
            .. Chunk("NAME", "TERRAIN\0"u8.ToArray()),
            .. Chunk("BHDR", Header(2, 2, 2, 0, 260, 262)),
            .. Chunk("HICL", new byte[BsiFile.HighColorLength]),
            .. Chunk("HTBL", new byte[8192]),
            .. Chunk("CMAP", ColorMap()),
            .. Chunk("DATA", 1, 2, 3, 4, 200, 201, 202, 203),
            .. Chunk("END ")
        ];
    }

    [Fact]
    public void ReadHeaders_ReturnsWhatTheFullParseDecodes_AndWhereEveryChunkLies()
    {
        var bytes = Terrain();

        var headers = BsiFile.ReadHeaders(bytes, "TERR02.BSI");
        var full = BsiFile.Parse(bytes, "TERR02.BSI");

        Assert.Equal("TERR02.BSI", headers.Name);
        Assert.Equal(bytes.Length, headers.Length);
        var image = Assert.Single(headers.Images);
        var decoded = Assert.Single(full.Images);

        Assert.Equal(0, image.Index);
        Assert.Equal(decoded.Name, image.Name);
        Assert.Equal((decoded.Width, decoded.Height, decoded.FrameCount), (image.Width, image.Height, image.FrameCount));
        Assert.Equal((decoded.XOffset, decoded.YOffset, decoded.Compression), (image.XOffset, image.YOffset, image.Compression));
        Assert.Equal(8, image.DeclaredPixelBytes);

        // Offsets by arithmetic on the builder: BSIF 8, IFHD 8 + 44, NAME 8 + 8, then BHDR.
        const int bhdrChunk = 8 + 52 + 16;
        Assert.Equal(bhdrChunk + 8, image.HeaderOffset);
        const int hiclChunk = bhdrChunk + 8 + 26;
        Assert.Equal(new ByteArea("HICL", hiclChunk + 8, hiclChunk + 8 + 256), image.HighColor);
        const int htblChunk = hiclChunk + 8 + 256;
        Assert.Equal(new ByteArea("HTBL", htblChunk + 8, htblChunk + 8 + 8192), image.HighColorTable);
        const int cmapChunk = htblChunk + 8 + 8192;
        Assert.Equal(new ByteArea("CMAP", cmapChunk + 8, cmapChunk + 8 + 768), image.ColorMap);
        const int dataChunk = cmapChunk + 8 + 768;
        Assert.Equal(new ByteArea("DATA", dataChunk + 8, dataChunk + 16), image.Data);
        Assert.Equal(new ByteArea("image:0", 8, dataChunk + 16), image.Chunks);
        Assert.Equal(dataChunk + 16, headers.EndOffset);
        Assert.Equal(bytes.Length, headers.EndOffset + 8);

        // The DATA range slices the bytes the full parse decoded.
        Assert.Equal(decoded.Frames[0].Indices, bytes[image.Data.Start..(image.Data.Start + 4)]);
        Assert.Equal(decoded.Frames[1].Indices, bytes[(image.Data.Start + 4)..image.Data.End]);
    }

    [Fact]
    public void ReadHeaders_SeparatesSeveralImages_AndCarriesPalettesForwardAsParseDoes()
    {
        byte[] bytes =
        [
            .. Chunk("BHDR", Header(2, 1, 1, 0)),
            .. Chunk("CMAP", ColorMap(1)),
            .. Chunk("DATA", 10, 11),
            .. Chunk("BHDR", Header(1, 2, 1, 0)),
            .. Chunk("CMAP", ColorMap(2)),
            .. Chunk("DATA", 12, 13),
            .. Chunk("BHDR", Header(1, 1, 1, 0)),
            .. Chunk("DATA", 14),
            .. Chunk("END ")
        ];

        var headers = BsiFile.ReadHeaders(bytes, "SPMENU.BSI");
        var full = BsiFile.Parse(bytes, "SPMENU.BSI");

        Assert.Equal(3, headers.Images.Count);
        Assert.Equal(3, full.Images.Count);
        Assert.Equal([0, 1, 2], headers.Images.Select(i => i.Index));
        Assert.Equal([(2, 1), (1, 2), (1, 1)], headers.Images.Select(i => (i.Width, i.Height)));
        Assert.Equal(["image:0", "image:1", "image:2"], headers.Images.Select(i => i.Chunks.Name));

        // Each image's chunk range starts where the previous one ended and ends with its DATA chunk.
        Assert.Equal(0, headers.Images[0].Chunks.Start);
        Assert.Equal(headers.Images[0].Chunks.End, headers.Images[1].Chunks.Start);
        Assert.Equal(headers.Images[1].Chunks.End, headers.Images[2].Chunks.Start);
        Assert.Equal(headers.Images[2].Chunks.End, headers.EndOffset);
        Assert.Equal(headers.Images[1].Data.End, headers.Images[1].Chunks.End);

        // Its own CMAP for the first two; the third inherits the second's, exactly as Parse hands it
        // the second's decoded palette.
        Assert.NotEqual(headers.Images[0].ColorMap, headers.Images[1].ColorMap);
        Assert.Equal(headers.Images[1].ColorMap, headers.Images[2].ColorMap);
        Assert.Same(full.Images[1].ColorMap, full.Images[2].ColorMap);
        Assert.Equal(2, bytes[headers.Images[2].ColorMap!.Value.Start]);
        Assert.Null(headers.Images[0].HighColor);
        Assert.Null(headers.Images[0].HighColorTable);
    }

    [Fact]
    public void ReadHeaders_SeesAChangedWidthByte_TheFullParseOfTheOriginalDoesNot()
    {
        var bytes = Terrain();
        var decoded = Assert.Single(BsiFile.Parse(bytes, "TERR02.BSI").Images);

        var altered = (byte[])bytes.Clone();
        var headerOffset = Assert.Single(BsiFile.ReadHeaders(bytes, "TERR02.BSI").Images).HeaderOffset;
        altered[headerOffset + 4] = 4;

        var image = Assert.Single(BsiFile.ReadHeaders(altered, "TERR02.BSI").Images);
        Assert.Equal(4, image.Width);
        Assert.NotEqual(decoded.Width, image.Width);
    }

    [Fact]
    public void ReadHeaders_DecodesNoData_SoAPayloadTheFullParseRejectsStillReads()
    {
        // 4x4x1 declared, two bytes shipped: the decode rejects it, the header read reports it.
        byte[] bytes =
        [
            .. Chunk("BHDR", Header(4, 4, 1, 0)), .. Chunk("CMAP", ColorMap()), .. Chunk("DATA", 1, 2), .. Chunk("END ")
        ];

        Assert.Throws<InvalidDataException>(() => BsiFile.Parse(bytes, "X.BSI"));

        var image = Assert.Single(BsiFile.ReadHeaders(bytes, "X.BSI").Images);
        Assert.Equal(16, image.DeclaredPixelBytes);
        Assert.Equal(2, image.Data.Length);

        // A compressed image is likewise handed back by its declared geometry and payload range only.
        byte[] compressed = [.. Chunk("BHDR", Header(4, 2, 1, 6)), .. Chunk("DATA", new byte[3]), .. Chunk("END ")];
        Assert.Throws<InvalidDataException>(() => BsiFile.Parse(compressed, "Y.BSI"));
        var line = Assert.Single(BsiFile.ReadHeaders(compressed, "Y.BSI").Images);
        Assert.Equal((6, 3), (line.Compression, line.Data.Length));
    }

    [Fact]
    public void ReadHeaders_RejectsWhatTheFullParseRejects_AndAcceptsTheEmptyFile()
    {
        var batch = Assert.Throws<InvalidDataException>(() =>
            BsiFile.ReadHeaders("@echo off\r\ncopy a b\r\n"u8.ToArray(), "SMP_BAK.BAT"));
        Assert.Contains("does not open with a BSI chunk", batch.Message, StringComparison.Ordinal);

        byte[] truncated = [.. Chunk("BHDR", Header(2, 2, 1, 0)), .. Chunk("DATA", 1, 2, 3)];
        truncated[^5] = 0xFF;
        Assert.Throws<InvalidDataException>(() => BsiFile.ReadHeaders(truncated, "X.BSI"));

        byte[] dataFirst = [.. Chunk("DATA", 1, 2), .. Chunk("END ")];
        Assert.Throws<InvalidDataException>(() => BsiFile.ReadHeaders(dataFirst, "X.BSI"));

        byte[] implausible = [.. Chunk("BHDR", Header(0, 2, 1, 0)), .. Chunk("END ")];
        Assert.Throws<InvalidDataException>(() => BsiFile.ReadHeaders(implausible, "X.BSI"));

        Assert.Throws<InvalidDataException>(() => BsiFile.ReadHeaders([], "X.BSI"));

        var empty = BsiFile.ReadHeaders(Chunk("END "), "BIP.BSI");
        Assert.Empty(empty.Images);
        Assert.Equal(0, empty.EndOffset);

        // A CMAP of the wrong length is reported absent, as Parse decodes it to null.
        byte[] shortMap = [.. Chunk("BHDR", Header(1, 1, 1, 0)), .. Chunk("CMAP", new byte[100]), .. Chunk("DATA", 5), .. Chunk("END ")];
        Assert.Null(Assert.Single(BsiFile.ReadHeaders(shortMap, "Z.BSI").Images).ColorMap);
        Assert.Null(Assert.Single(BsiFile.Parse(shortMap, "Z.BSI").Images).ColorMap);
    }
}
