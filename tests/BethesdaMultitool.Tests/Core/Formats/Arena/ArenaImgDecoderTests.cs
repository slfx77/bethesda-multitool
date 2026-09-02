using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Hand-built .IMG/.SET vectors for <see cref="ArenaImgDecoder" />. Every payload is
///     composed packet by packet with the expected pixels pinned independently; the LZHUF
///     vector reuses the compressed bytes already pinned by LzhufCodecTests so the framing
///     (u16 decompressed length inside dataLen) is what this file actually tests.
/// </summary>
public class ArenaImgDecoderTests
{
    [Fact]
    public void Decode_UncompressedWithHeader_PinsPixelsAndOffsets()
    {
        var bytes = Header(xOffset: 3, yOffset: 7, width: 2, height: 2, flags: 0x0000, dataLength: 4);
        bytes.AddRange(new byte[] { 10, 20, 30, 40 });

        var result = ArenaImgDecoder.Decode(bytes.ToArray(), "TEST.IMG");

        Assert.Single(result.Images);
        Assert.Null(result.EmbeddedPalette);
        var image = result.Image;
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(3, image.XOffset);
        Assert.Equal(7, image.YOffset);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, image.Indices);
    }

    [Fact]
    public void Decode_Type02Rle_Decompresses()
    {
        // Run packet: control 0x83 -> 0x83 - 0x7F = 4 repeats of 0xAB. (OpenTESArena's
        // IMGFile treats type 02 as unrecognized - only its CIF frames use it - but the
        // decoder here accepts the identical layout; see the production file's remarks.)
        var bytes = Header(0, 0, width: 2, height: 2, flags: 0x0002, dataLength: 2);
        bytes.AddRange(new byte[] { 0x83, 0xAB });

        var result = ArenaImgDecoder.Decode(bytes.ToArray(), "RLE.IMG");

        Assert.Equal(new byte[] { 0xAB, 0xAB, 0xAB, 0xAB }, result.Image.Indices);
    }

    [Fact]
    public void Decode_Type04Lzss_Decompresses()
    {
        // Flag byte 0x0F: four set bits (LSB-first) -> four literals, input then exhausted.
        var bytes = Header(0, 0, width: 2, height: 2, flags: 0x0004, dataLength: 5);
        bytes.AddRange(new byte[] { 0x0F, 0x10, 0x20, 0x30, 0x40 });

        var result = ArenaImgDecoder.Decode(bytes.ToArray(), "LZSS.IMG");

        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, result.Image.Indices);
    }

    [Fact]
    public void Decode_Type08Lzhuf_ReusesKnownVectorAndSkipsLengthField()
    {
        // Compressed bytes are the "ABCABCABC" vector pinned by LzhufCodecTests. dataLength
        // counts the leading u16 decompressed-length field (9), which the decoder must skip.
        var bytes = Header(xOffset: 1, yOffset: 2, width: 3, height: 3, flags: 0x0008, dataLength: 8);
        bytes.AddRange(new byte[] { 9, 0 }); // u16 LE decompressed length
        bytes.AddRange(new byte[] { 0xE6, 0xF3, 0xB9, 0xF1, 0xE0, 0x20 });

        var result = ArenaImgDecoder.Decode(bytes.ToArray(), "LZHUF.IMG");

        Assert.Equal("ABCABCABC"u8.ToArray(), result.Image.Indices);
        Assert.Equal(1, result.Image.XOffset);
        Assert.Equal(2, result.Image.YOffset);
    }

    [Fact]
    public void Decode_EmbeddedPaletteFlag_PromotesClampsAndMakesIndex0Transparent()
    {
        var bytes = Header(0, 0, width: 1, height: 1, flags: 0x0100, dataLength: 1);
        bytes.Add(0x05); // the single pixel
        var palette = new byte[768];
        palette[0] = 1; //  entry 0: (1, 2, 3) -> promoted (4, 8, 12), alpha 0
        palette[1] = 2;
        palette[2] = 3;
        palette[3] = 0x3F; // entry 1: (0x3F, 0, 0x20) -> (255, 0, 0x82)
        palette[5] = 0x20;
        palette[6] = 0x40; // entry 2: red 0x40 clamps to 0x3F -> 255 (unclamped promotion would give 4)
        bytes.AddRange(palette);

        var result = ArenaImgDecoder.Decode(bytes.ToArray(), "PAL.IMG");

        Assert.Equal(new byte[] { 0x05 }, result.Image.Indices);
        Assert.NotNull(result.EmbeddedPalette);

        var (r0, g0, b0, a0) = result.EmbeddedPalette.GetEntry(0);
        Assert.Equal(4, r0);
        Assert.Equal(8, g0);
        Assert.Equal(12, b0);
        Assert.Equal(0, a0); // index 0 transparent, per the reference's readPalette

        var (r1, g1, b1, a1) = result.EmbeddedPalette.GetEntry(1);
        Assert.Equal(255, r1);
        Assert.Equal(0, g1);
        Assert.Equal(0x82, b1); // (0x20 << 2) | (0x20 >> 4)
        Assert.Equal(255, a1);

        var (r2, _, _, _) = result.EmbeddedPalette.GetEntry(2);
        Assert.Equal(255, r2); // proves the 0x3F clamp ran before promotion

        var (r10, g10, b10, a10) = result.EmbeddedPalette.GetEntry(10);
        Assert.Equal(0, r10);
        Assert.Equal(0, g10);
        Assert.Equal(0, b10);
        Assert.Equal(255, a10);
    }

    [Fact]
    public void Decode_4096ByteFile_IsRaw64x64WallTexture()
    {
        // Exactly 4096 bytes and not in the raw-name table: raw 64x64, header parse skipped
        // even though the leading bytes could look like one.
        var bytes = new byte[4096];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        var result = ArenaImgDecoder.Decode(bytes, "WALLTEST.IMG");

        var image = result.Image;
        Assert.Equal(64, image.Width);
        Assert.Equal(64, image.Height);
        Assert.Equal(0, image.XOffset);
        Assert.Equal(0, image.YOffset);
        Assert.Equal(0x00, image.Indices[0]);
        Assert.Equal(0xFF, image.Indices[255]);
        Assert.Equal(0xFF, image.Indices[4095]);
    }

    [Fact]
    public void Decode_HeaderlessTableEntry_CaseInsensitiveAndIgnoresTrailingBytes()
    {
        // VILLAGE.IMG is 8x8 in the hardcoded table; the lookup must be case-insensitive
        // and only the first width * height bytes are pixels.
        var bytes = new byte[70];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i + 1);
        }

        var result = ArenaImgDecoder.Decode(bytes, "village.img");

        var image = result.Image;
        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);
        Assert.Equal(bytes.AsSpan(0, 64).ToArray(), image.Indices);
    }

    [Fact]
    public void Decode_Dzttav_PlacesPayloadInRightHalfOf64x64Canvas()
    {
        // DZTTAV.IMG is a raw 32x34 image the game expects as 64x64: each source row lands
        // at destination X + 32, everything else stays index 0.
        var source = new byte[32 * 34];
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = (byte)((i % 250) + 1); // never 0, so placement is distinguishable from fill
        }

        var result = ArenaImgDecoder.Decode(source, "DZTTAV.IMG");

        var image = result.Image;
        Assert.Equal(64, image.Width);
        Assert.Equal(64, image.Height);
        Assert.Equal(source[0], image.Indices[32]); //             row 0: src (0,0) -> dst (32,0)
        Assert.Equal(source[31], image.Indices[63]); //            row 0: src (31,0) -> dst (63,0)
        Assert.Equal(source[32], image.Indices[64 + 32]); //       row 1 starts at src index 32
        Assert.Equal(source[(33 * 32) + 31], image.Indices[(33 * 64) + 63]); // last payload pixel
        Assert.Equal(0, image.Indices[0]); //                      left half untouched
        Assert.Equal(0, image.Indices[34 * 64]); //                rows past the 34-row payload
        Assert.Equal(0, image.Indices[(34 * 64) + 32]);
    }

    [Fact]
    public void Decode_SetFile_SplitsInto64x64Tiles()
    {
        // 8192 bytes -> two 4096-byte tiles, in file order (extension match is
        // case-insensitive). Boundary bytes pin the exact chunk split.
        var bytes = new byte[8192];
        bytes.AsSpan(0, 4096).Fill(0x11);
        bytes.AsSpan(4096, 4096).Fill(0x22);
        bytes[4095] = 0x99;
        bytes[4096] = 0x77;

        var result = ArenaImgDecoder.Decode(bytes, "tiles.set");

        Assert.Equal(2, result.Images.Count);
        Assert.Null(result.EmbeddedPalette);
        Assert.All(result.Images, tile =>
        {
            Assert.Equal(64, tile.Width);
            Assert.Equal(64, tile.Height);
        });
        Assert.Equal(0x11, result.Images[0].Indices[0]);
        Assert.Equal(0x99, result.Images[0].Indices[4095]);
        Assert.Equal(0x77, result.Images[1].Indices[0]);
        Assert.Equal(0x22, result.Images[1].Indices[4095]);
    }

    [Fact]
    public void Decode_Tbs2Set_AppendsDummyByte()
    {
        // TBS2.SET ships one byte short of 0x1000; the decoder appends a zero byte so the
        // final tile completes.
        var bytes = new byte[4095];
        Array.Fill(bytes, (byte)0x33);

        var result = ArenaImgDecoder.Decode(bytes, "TBS2.SET");

        Assert.Single(result.Images);
        Assert.Equal(0x33, result.Images[0].Indices[4094]);
        Assert.Equal(0x00, result.Images[0].Indices[4095]);
    }

    [Fact]
    public void Decode_HeaderTooSmall_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ArenaImgDecoder.Decode(new byte[5], "FOO.IMG"));
    }

    [Fact]
    public void Decode_UnrecognizedCompressionType_Throws()
    {
        var bytes = Header(0, 0, width: 1, height: 1, flags: 0x0003, dataLength: 1);
        bytes.Add(0x00);

        Assert.Throws<InvalidDataException>(() => ArenaImgDecoder.Decode(bytes.ToArray(), "BAD.IMG"));
    }

    [Fact]
    public void Decode_TruncatedUncompressedPixels_Throws()
    {
        var bytes = Header(0, 0, width: 10, height: 10, flags: 0x0000, dataLength: 100);
        bytes.AddRange(new byte[] { 1, 2, 3, 4 }); // 100 pixels owed

        Assert.Throws<InvalidDataException>(() => ArenaImgDecoder.Decode(bytes.ToArray(), "SHORT.IMG"));
    }

    [Fact]
    public void Decode_TruncatedType04Payload_Throws()
    {
        var bytes = Header(0, 0, width: 2, height: 2, flags: 0x0004, dataLength: 50);
        bytes.AddRange(new byte[] { 0x0F, 0x10, 0x20, 0x30, 0x40 }); // 50 bytes declared, 5 present

        Assert.Throws<InvalidDataException>(() => ArenaImgDecoder.Decode(bytes.ToArray(), "SHORT4.IMG"));
    }

    [Fact]
    public void Decode_TruncatedEmbeddedPalette_Throws()
    {
        var bytes = Header(0, 0, width: 1, height: 1, flags: 0x0100, dataLength: 1);
        bytes.Add(0x05);
        bytes.AddRange(new byte[10]); // 768 palette bytes owed

        Assert.Throws<InvalidDataException>(() => ArenaImgDecoder.Decode(bytes.ToArray(), "SHORTPAL.IMG"));
    }

    [Fact]
    public void Decode_TruncatedHeaderlessTableEntry_Throws()
    {
        // VILLAGE.IMG needs 8 * 8 = 64 bytes.
        Assert.Throws<InvalidDataException>(() => ArenaImgDecoder.Decode(new byte[10], "VILLAGE.IMG"));
    }

    [Fact]
    public void Decode_SetSmallerThanOneTile_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ArenaImgDecoder.Decode(new byte[100], "TINY.SET"));
    }

    private static List<byte> Header(
        ushort xOffset, ushort yOffset, ushort width, ushort height, ushort flags, ushort dataLength)
    {
        var bytes = new List<byte>();
        AddUInt16(bytes, xOffset);
        AddUInt16(bytes, yOffset);
        AddUInt16(bytes, width);
        AddUInt16(bytes, height);
        AddUInt16(bytes, flags);
        AddUInt16(bytes, dataLength);
        return bytes;
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value & 0xFF));
        bytes.Add((byte)(value >> 8));
    }
}
