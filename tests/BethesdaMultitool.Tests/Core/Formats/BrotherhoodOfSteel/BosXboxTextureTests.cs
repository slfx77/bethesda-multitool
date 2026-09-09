using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for <see cref="BosXboxTexture" />: the Xbox release's <c>D3DFMT_P8</c> texture
///     section — a 0x38 header, a 1,024-byte RGBA palette and LINEAR 8-bit indices at the actual
///     width.
///     <para>
///         The expected pixels are computed here from the fixture's own palette and index bytes as
///         written, never from the reader; a decoder that swizzled, that read the palette as BGRA,
///         or that padded rows to the next power of two would produce different bytes for the
///         non-power-of-two case below.
///     </para>
/// </summary>
public sealed class BosXboxTextureTests
{
    private const uint PalettizedFlags = 0x4014;

    private static byte[] Section(int width, int height, uint flags, byte[] palette, byte[] indices,
        int dataOffset = BosXboxTexture.StandardDataOffset)
    {
        var b = new byte[dataOffset + palette.Length + indices.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)dataOffset);
        palette.CopyTo(b.AsSpan(dataOffset));
        indices.CopyTo(b.AsSpan(dataOffset + palette.Length));
        return b;
    }

    /// <summary>A palette whose entry i is (i, 255 - i, i * 2 mod 256, i mod 255) in R, G, B, A order.</summary>
    private static byte[] RampPalette()
    {
        var palette = new byte[BosXboxTexture.PaletteLength];
        for (var i = 0; i < 256; i++)
        {
            palette[i * 4 + 0] = (byte)i;
            palette[i * 4 + 1] = (byte)(255 - i);
            palette[i * 4 + 2] = (byte)(i * 2);
            palette[i * 4 + 3] = (byte)(i % 255);
        }

        return palette;
    }

    [Fact]
    public void DecodesEveryTexelThroughTheStoredRgbaPalette()
    {
        const int width = 5;
        const int height = 3;
        var palette = RampPalette();
        var indices = new byte[width * height];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = (byte)(i * 17);
        }

        var texture = BosXboxTexture.Parse(Section(width, height, PalettizedFlags, palette, indices), "ramp");

        Assert.Equal(width, texture.Width);
        Assert.Equal(height, texture.Height);
        Assert.Equal(BosXboxTexture.StandardDataOffset, texture.DataOffset);
        Assert.Equal(indices, texture.Indices);
        Assert.False(texture.IsPowerOfTwo);

        for (var texel = 0; texel < indices.Length; texel++)
        {
            var index = indices[texel];
            // Independently derived from the palette bytes as written, in R,G,B,A order.
            Assert.Equal(index, texture.Rgba[texel * 4 + 0]);
            Assert.Equal((byte)(255 - index), texture.Rgba[texel * 4 + 1]);
            Assert.Equal((byte)(index * 2), texture.Rgba[texel * 4 + 2]);
            Assert.Equal((byte)(index % 255), texture.Rgba[texel * 4 + 3]);
        }
    }

    [Fact]
    public void RowsArePackedAtTheActualWidthNotThePowerOfTwoTheLoaderRoundsUpTo()
    {
        // ⚑ The loader copies memcpy(dst, data + 0x400 + width*row, width) and only THEN advances
        // the destination by the rounded-up width, so nothing on disc is padded. A reader that
        // padded rows to 8 here would read row 1 starting three texels late.
        const int width = 5;
        const int height = 2;
        var palette = RampPalette();
        byte[] indices = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        var texture = BosXboxTexture.Parse(Section(width, height, PalettizedFlags, palette, indices), "rows");

        Assert.Equal(6, texture.Indices[width]);
        Assert.Equal(6, texture.Rgba[width * 4]);
    }

    [Fact]
    public void RejectsASectionWhoseSizeDoesNotAccountForPaletteAndPixels()
    {
        // The size arithmetic is the discriminating half of the probe. ⚠ On retail it is the
        // DIMENSION gate that does the refusing (1,948 of the 1,996 flag-bearing non-textures) and
        // nothing there ever reaches this check — which is exactly why the case needs a synthetic
        // vector: the retail corpus cannot exercise it. What the retail corpus DOES show is that
        // no rival palette or header length satisfies the relation on any of its 21,905 sections.
        var section = Section(4, 4, PalettizedFlags, RampPalette(), new byte[16]);
        var truncated = section.AsSpan(0, section.Length - 1).ToArray();

        Assert.False(BosXboxTexture.IsTexture(truncated));
        Assert.False(BosXboxTexture.TryParse(truncated, "short", out _, out var error));
        Assert.Contains("width × height", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsASectionWithoutThePalettizedFlagTheLoaderTests()
    {
        // 0x00084EC0 acts only when byte(+5) & 0x40 is set; without it the loader relocates the
        // pointer and returns, so the section is not this format however well the size fits.
        var section = Section(4, 4, 0x0014, RampPalette(), new byte[16]);

        Assert.False(BosXboxTexture.IsTexture(section));
    }

    [Fact]
    public void AcceptsAPowerOfTwoTextureAndReportsItAsOne()
    {
        var texture = BosXboxTexture.Parse(
            Section(8, 8, 0xC014, RampPalette(), new byte[64]), "pow2");

        Assert.True(texture.IsPowerOfTwo);
        Assert.Equal(0xC014U, texture.Flags);
        Assert.Equal(8, texture.ToDecodedTexture().Width);
        Assert.Equal(8, texture.ToIndexedBitmap().Width);
    }

    [Theory]
    // Hand-computed from the loader's own arithmetic at 0x00085091-0x00085125:
    // mean = (r + g + b) * 0.33333334, then each component becomes (c - mean) * 1.3 + mean + 0.5,
    // TRUNCATED (cvttss2si) and clamped to 0..255. Alpha is written through untouched.
    // (10,20,30): mean 20 -> -13+20.5=7.5 -> 7 | 20.5 -> 20 | 13+20.5=33.5 -> 33
    [InlineData(10, 20, 30, 7, 20, 33)]
    // (120,130,140): mean 130 -> 117.5 | 130.5 | 143.5
    [InlineData(120, 130, 140, 117, 130, 143)]
    // (3,200,7): mean 70 -> -16.6 clamps LOW, 239.5 -> 239, -11.4 clamps LOW
    [InlineData(3, 200, 7, 0, 239, 0)]
    // (255,0,0): mean 85 -> 306.5 clamps HIGH, -25 clamps LOW twice
    [InlineData(255, 0, 0, 255, 0, 0)]
    // A grey has zero saturation to boost, so it must come back unchanged.
    [InlineData(90, 90, 90, 90, 90, 90)]
    public void TheLoadersSaturationBoostIsWhatTheConsoleActuallyDraws(
        byte r, byte g, byte b, byte expectedR, byte expectedG, byte expectedB)
    {
        // ⚑ The loader SKIPS this only when flags bit 0x08 is set (`test byte ptr [esi+4], 8`),
        // and that bit is clear on all 3,686 shipped textures — so this, not the stored byte, is
        // the colour on screen. The expectations are derived from the disassembled formula, not
        // from BosXboxTexture.
        var palette = new byte[BosXboxTexture.PaletteLength];
        palette[0] = r;
        palette[1] = g;
        palette[2] = b;
        palette[3] = BosXboxTexture.OpaqueAlpha;

        var texture = BosXboxTexture.Parse(
            Section(2, 1, PalettizedFlags, palette, new byte[2]), "boost");

        Assert.True(texture.IsSaturationBoosted);
        // Rgba is the STORED colour…
        Assert.Equal(new[] { r, g, b, BosXboxTexture.OpaqueAlpha }, texture.Rgba[..4]);

        // …and RgbaAsDrawn is the boosted one, for every texel.
        var drawn = texture.RgbaAsDrawn();
        Assert.Equal(8, drawn.Length);
        Assert.Equal(new[] { expectedR, expectedG, expectedB, BosXboxTexture.OpaqueAlpha }, drawn[..4]);
        Assert.Equal(new[] { expectedR, expectedG, expectedB, BosXboxTexture.OpaqueAlpha }, drawn[4..]);
    }

    [Fact]
    public void ASuppressedSaturationFlagLeavesThePaletteAlone()
    {
        // ⚠ Bit 0x08 suppresses the boost. NO shipped texture sets it, so this vector is the only
        // exercise that path gets — and it is what makes the "3,686 of 3,686 are boosted" count on
        // the retail side mean something rather than being true by construction.
        var palette = new byte[BosXboxTexture.PaletteLength];
        palette[0] = 10;
        palette[1] = 20;
        palette[2] = 30;
        palette[3] = 255;

        var texture = BosXboxTexture.Parse(
            Section(1, 1, PalettizedFlags | BosXboxTexture.SaturationSuppressedFlag, palette, new byte[1]),
            "suppressed");

        Assert.False(texture.IsSaturationBoosted);
        Assert.Equal(texture.Rgba, texture.RgbaAsDrawn());
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, texture.RgbaAsDrawn());
    }

    [Fact]
    public void APs2SectionIsNotAnXboxTexture()
    {
        // ⛔ The two families never overlap: a PS2 .tex is a GIF packet stream whose header has
        // 0x80 at +0x10, which lands nowhere near the size relation this needs.
        var ps2 = new byte[0x80 + 32];
        BinaryPrimitives.WriteUInt16LittleEndian(ps2, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(ps2.AsSpan(2), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(ps2.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(ps2.AsSpan(0x10), 0x80);

        Assert.False(BosXboxTexture.IsTexture(ps2));
    }
}