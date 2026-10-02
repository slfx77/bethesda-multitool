using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.RenderWare;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.RenderWare;

/// <summary>
///     The PSP texture raster decoder.
///     <para>
///         ⚠ The two traps this layout sets, both pinned below: the dimensions are LINEAR u16 rather
///         than the log2 pair a GE register would carry, and the row pitch has a 16-byte FLOOR — so
///         below 32 pixels at 4bpp every width shares a pitch and the byte total alone can recover
///         nothing. An earlier search for a log2 pair found nothing and briefly concluded the format
///         was unsolvable; the answer was inside the bytes it searched, in an encoding it did not try.
///     </para>
/// </summary>
public sealed class RwPspTextureTests
{
    private static byte[] Raster(
        int width, int height, RwPspPixelFormat format, string name,
        byte[]? clut = null, byte[]? pixels = null, int mipCount = 1)
    {
        var clutBytes = RwPspTexture.ClutEntries(format) * 4;
        var pixelBytes = (int)RwPspTexture.PixelBytes(width, height, format, mipCount);
        var body = new byte[RwPspTexture.ClutOffset + clutBytes + pixelBytes];

        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(RwPspTexture.DimensionsOffset), (uint)((height << 16) | width));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(RwPspTexture.FormatOffset), (uint)format);
        Encoding.ASCII.GetBytes(name).CopyTo(body, RwPspTexture.NameOffset);

        clut?.CopyTo(body, RwPspTexture.ClutOffset);
        pixels?.CopyTo(body, RwPspTexture.ClutOffset + clutBytes);
        return body;
    }

    /// <summary>A 16-entry CLUT whose entry i is (i*16, i*8, i*4, 255).</summary>
    private static byte[] Clut16()
    {
        var clut = new byte[16 * 4];
        for (var i = 0; i < 16; i++)
        {
            clut[i * 4 + 0] = (byte)(i * 16);
            clut[i * 4 + 1] = (byte)(i * 8);
            clut[i * 4 + 2] = (byte)(i * 4);
            clut[i * 4 + 3] = 255;
        }

        return clut;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(RwPspTexture texture, int x, int y)
    {
        var at = (y * texture.Width + x) * 4;
        return (texture.Rgba[at], texture.Rgba[at + 1], texture.Rgba[at + 2], texture.Rgba[at + 3]);
    }

    // ---------------------------------------------------------------- the two traps

    /// <summary>
    ///     ⚑ Dimensions are LINEAR u16 with width in the low half. A log2 reading would decode a
    ///     256x256 texture as 0x100 mips of nothing.
    /// </summary>
    [Fact]
    public void DimensionsAreLinearWithWidthInTheLowHalf()
    {
        var body = Raster(128, 64, RwPspPixelFormat.Indexed8, "wide", new byte[256 * 4]);

        var texture = RwPspTexture.TryParse(body);

        Assert.NotNull(texture);
        Assert.Equal(128, texture.Width);
        Assert.Equal(64, texture.Height);
        Assert.Equal("wide", texture.Name);
    }

    /// <summary>
    ///     ⚠ The pitch floor: at 4bpp a 16-pixel row needs 8 bytes but occupies 16. Getting this
    ///     wrong shears every row progressively, which looks like a skewed image rather than an error.
    /// </summary>
    [Theory]
    [InlineData(8, RwPspPixelFormat.Indexed4, 16)]
    [InlineData(16, RwPspPixelFormat.Indexed4, 16)]
    [InlineData(32, RwPspPixelFormat.Indexed4, 16)]
    [InlineData(64, RwPspPixelFormat.Indexed4, 32)]
    [InlineData(128, RwPspPixelFormat.Indexed4, 64)]
    [InlineData(8, RwPspPixelFormat.Indexed8, 16)]
    [InlineData(64, RwPspPixelFormat.Indexed8, 64)]
    [InlineData(64, RwPspPixelFormat.Rgba8888, 256)]
    // internal, not public: the parameter type is an internal enum, and xUnit v3 discovers
    // non-public test methods (measured on 3.2.2). That keeps the enum internal instead of
    // widening a production API, or passing ints and casting, to suit a test.
    internal void PitchIsSixteenByteAlignedWithASixteenByteFloor(int width, RwPspPixelFormat format, int expected)
    {
        Assert.Equal(expected, RwPspTexture.Pitch(width, format));
    }

    /// <summary>
    ///     Rows are read at the pitch, not at the row's own width. A 16-wide 4bpp texture stores 8
    ///     bytes of pixels in a 16-byte row, and the 8 bytes of padding must be skipped.
    /// </summary>
    [Fact]
    public void RowsAreReadAtThePitchNotTheRowWidth()
    {
        const int width = 16;
        const int height = 2;
        var pitch = RwPspTexture.Pitch(width, RwPspPixelFormat.Indexed4);
        var pixels = new byte[pitch * height];

        // Row 0 is index 1 throughout; row 1 is index 2. The padding is deliberately index 15,
        // which a reader that ignored the pitch would pick up.
        for (var i = 0; i < pitch; i++)
        {
            pixels[i] = i < width / 2 ? (byte)0x11 : (byte)0xFF;
            pixels[pitch + i] = i < width / 2 ? (byte)0x22 : (byte)0xFF;
        }

        var texture = RwPspTexture.TryParse(
            Raster(width, height, RwPspPixelFormat.Indexed4, "padded", Clut16(), pixels));

        Assert.NotNull(texture);
        Assert.Equal((16, 8, 4, 255), Pixel(texture, 0, 0));
        Assert.Equal((32, 16, 8, 255), Pixel(texture, 0, 1));
        Assert.Equal((32, 16, 8, 255), Pixel(texture, 15, 1));
    }

    // ---------------------------------------------------------------- formats

    /// <summary>4-bit indexed packs two texels per byte, low nibble first.</summary>
    [Fact]
    public void FourBitIndexedReadsTheLowNibbleFirst()
    {
        var pitch = RwPspTexture.Pitch(32, RwPspPixelFormat.Indexed4);
        var pixels = new byte[pitch];
        pixels[0] = 0x21; // texel 0 = index 1, texel 1 = index 2

        var texture = RwPspTexture.TryParse(
            Raster(32, 1, RwPspPixelFormat.Indexed4, "nibbles", Clut16(), pixels));

        Assert.NotNull(texture);
        Assert.Equal((16, 8, 4, 255), Pixel(texture, 0, 0));
        Assert.Equal((32, 16, 8, 255), Pixel(texture, 1, 0));
    }

    [Fact]
    public void EightBitIndexedReadsOneTexelPerByte()
    {
        var clut = new byte[256 * 4];
        clut[5 * 4 + 0] = 200;
        clut[5 * 4 + 3] = 255;

        var pixels = new byte[RwPspTexture.Pitch(16, RwPspPixelFormat.Indexed8)];
        pixels[3] = 5;

        var texture = RwPspTexture.TryParse(
            Raster(16, 1, RwPspPixelFormat.Indexed8, "bytes", clut, pixels));

        Assert.NotNull(texture);
        Assert.Equal((200, 0, 0, 255), Pixel(texture, 3, 0));
    }

    /// <summary>
    ///     16-bit channels are expanded by replicating their high bits, so a full-scale value
    ///     reaches 255 rather than 248. A plain shift leaves every white pixel slightly grey.
    /// </summary>
    [Fact]
    public void FiveBitChannelsExpandToFullRange()
    {
        var pixels = new byte[RwPspTexture.Pitch(8, RwPspPixelFormat.Rgb565)];
        BinaryPrimitives.WriteUInt16LittleEndian(pixels, 0xFFFF);

        var texture = RwPspTexture.TryParse(
            Raster(8, 1, RwPspPixelFormat.Rgb565, "white", pixels: pixels));

        Assert.NotNull(texture);
        Assert.Equal((255, 255, 255, 255), Pixel(texture, 0, 0));
    }

    [Fact]
    public void FiveFiveFiveOneCarriesItsSingleAlphaBit()
    {
        var pixels = new byte[RwPspTexture.Pitch(8, RwPspPixelFormat.Rgba5551)];
        BinaryPrimitives.WriteUInt16LittleEndian(pixels, 0x001F); // opaque bit clear
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(2), 0x801F); // set

        var texture = RwPspTexture.TryParse(
            Raster(8, 1, RwPspPixelFormat.Rgba5551, "alpha", pixels: pixels));

        Assert.NotNull(texture);
        Assert.Equal(0, Pixel(texture, 0, 0).A);
        Assert.Equal(255, Pixel(texture, 1, 0).A);
    }

    // ---------------------------------------------------------------- validation

    /// <summary>
    ///     A body whose length no mip count reproduces is refused. Without this a mis-located chunk
    ///     decodes into noise that looks like a texture.
    /// </summary>
    [Fact]
    public void ABodyNoMipCountExplainsIsRefused()
    {
        var body = Raster(64, 64, RwPspPixelFormat.Indexed8, "x", new byte[256 * 4]);

        Assert.Null(RwPspTexture.TryParse(body.Concat(new byte[7]).ToArray()));
    }

    [Fact]
    public void AMipChainIsRecognised()
    {
        var texture = RwPspTexture.TryParse(
            Raster(64, 64, RwPspPixelFormat.Indexed8, "mipped", new byte[256 * 4], mipCount: 7));

        Assert.NotNull(texture);
        Assert.Equal(7, texture.MipCount);
        Assert.Equal(64, texture.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xAC)]
    public void ATruncatedBodyIsRefused(int length)
    {
        Assert.Null(RwPspTexture.TryParse(new byte[length]));
    }

    [Fact]
    public void AnUnknownPixelFormatIsRefused()
    {
        var body = Raster(16, 16, RwPspPixelFormat.Indexed8, "x", new byte[256 * 4]);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(RwPspTexture.FormatOffset), 99);

        Assert.Null(RwPspTexture.TryParse(body));
    }
}

/// <summary>
///     The texture decoder against every raster in the shipped Oblivion PSP builds. Opt-in:
///     <c>RUN_BUCKET_B=1</c>.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RwPspTextureRetailTests
{
    private static List<byte[]> RasterBodies(string pack)
    {
        var bytes = File.ReadAllBytes(pack);
        var archive = OblivionPspArchive.Parse(pack);
        var bodies = new List<byte[]>();

        void Walk(int offset, int end, int depth)
        {
            if (depth > 12) return;
            foreach (var chunk in RwChunk.Siblings(bytes, offset, end))
            {
                if (chunk.Type == RwChunk.TextureNative)
                {
                    if (RwChunk.TryRead(bytes, chunk.PayloadOffset, chunk.End, out var inner) &&
                        inner.Type == RwChunk.Struct)
                    {
                        bodies.Add(bytes[inner.PayloadOffset..inner.End]);
                    }
                }
                else if (RwChunk.IsContainer(chunk.Type))
                {
                    Walk(chunk.PayloadOffset, chunk.End, depth + 1);
                }
            }
        }

        foreach (var entry in archive.Entries)
        {
            if (entry.Size <= 0 || entry.Offset + entry.Size > bytes.Length) continue;
            var start = (int)entry.Offset;
            foreach (var resource in OblivionPspResourceReader.ReadResources(
                         bytes.AsSpan(start, (int)entry.Size)))
            {
                if (resource.IsRenderWareStream)
                {
                    Walk(start + resource.PayloadOffset, start + (int)entry.Size, 0);
                }
            }
        }

        return bodies;
    }

    private static string[] RequirePacks()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var builds = RealAssetPaths.Travels.OblivionPspBuilds();
        Assert.SkipWhen(builds.Count == 0, RealAssetPaths.SkipMessage("Oblivion PSP (cancelled betas)"));
        var packs = builds.SelectMany(build => Directory.EnumerateFiles(build, "GR.ARC", SearchOption.AllDirectories))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.SkipWhen(packs.Length == 0, "No GR.ARC packs are staged.");
        return packs;
    }

    /// <summary>
    ///     ⚑ Every raster in every build decodes, with dimensions that are powers of two and a
    ///     pixel buffer of exactly the size they imply.
    /// </summary>
    [Fact]
    public void EveryRetailRasterDecodes()
    {
        var packs = RequirePacks();

        var total = 0;
        var decoded = 0;
        var named = 0;
        var formats = new Dictionary<RwPspPixelFormat, int>();

        foreach (var pack in packs)
        {
            foreach (var body in RasterBodies(pack))
            {
                total++;
                var texture = RwPspTexture.TryParse(body);
                if (texture is null) continue;

                decoded++;
                formats[texture.Format] = formats.GetValueOrDefault(texture.Format) + 1;
                if (texture.Name.Length > 0) named++;

                Assert.Equal(texture.Width * texture.Height * 4, texture.Rgba.Length);
                Assert.True((texture.Width & (texture.Width - 1)) == 0, $"{texture.Name}: width {texture.Width}");
                Assert.True((texture.Height & (texture.Height - 1)) == 0, $"{texture.Name}: height {texture.Height}");
            }
        }

        // 19,477 across the six dated betas (measured 2026-09-09; the floor was 20,000 while the
        // corpus also held the community repack).
        Assert.True(total > 19_000, $"Only {total} rasters were reached.");
        Assert.Equal(total, decoded);
        Assert.True(named > total / 2, $"Only {named} of {total} rasters carried a name.");

        // The indexed formats dominate; a census inverted from this means the format word moved.
        Assert.True(
            formats.GetValueOrDefault(RwPspPixelFormat.Indexed4) >
            formats.GetValueOrDefault(RwPspPixelFormat.Rgba8888),
            "4-bit indexed should dominate the corpus.");
    }

    /// <summary>
    ///     Decoded pixels are not uniform. A layout that was subtly wrong — a bad pitch, a wrong
    ///     CLUT offset — still produces a full buffer, so this checks the images carry variety
    ///     rather than one repeated colour.
    /// </summary>
    [Fact]
    public void DecodedRastersCarryRealImageVariety()
    {
        var packs = RequirePacks();

        var examined = 0;
        var varied = 0;
        foreach (var body in RasterBodies(packs[^1]))
        {
            var texture = RwPspTexture.TryParse(body);
            if (texture is null || texture.Rgba.Length < 256) continue;

            examined++;
            var distinct = new HashSet<uint>();
            for (var i = 0; i + 3 < texture.Rgba.Length; i += 4)
            {
                distinct.Add(BitConverter.ToUInt32(texture.Rgba, i));
                if (distinct.Count > 4) break;
            }

            if (distinct.Count > 4) varied++;
        }

        Assert.True(examined > 100, $"Only {examined} rasters were examined.");
        Assert.True(
            varied > examined / 2,
            $"Only {varied} of {examined} rasters have more than four colours; the decode is likely wrong.");
    }
}