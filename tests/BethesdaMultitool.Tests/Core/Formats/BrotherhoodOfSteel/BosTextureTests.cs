using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for the Fallout: Brotherhood of Steel <c>.tex</c> texture: a 0x80 header, a CLUT
///     transfer in CSM1 order, and PSMT8 texels uploaded as PSMCT32 in the GS block layout.
///     <para>
///         The swizzle oracle is the game's OWN block table (<c>0x0019F400</c>), transcribed from
///         the decompile and pinned below by literals computed from that formula outside this
///         code base — not the GS address tables the decoder uses. A decoder built on wrong GS
///         tables would read every one of the 256 texels somewhere else.
///     </para>
/// </summary>
public sealed class BosTextureTests
{
    private const int RegisterBitbltbuf = 0x50;
    private const int RegisterTrxpos = 0x51;
    private const int RegisterTrxreg = 0x52;
    private const int RegisterTrxdir = 0x53;

    /// <summary>
    ///     The first 16 entries of the game's block table, evaluated by hand from the two helpers
    ///     <c>0x0019F368</c> / <c>0x0019F398</c>: transfer byte p carries texel table[p] of the 16×16 tile.
    /// </summary>
    private static readonly byte[] GameTableHead = [0, 36, 8, 44, 1, 37, 9, 45, 2, 38, 10, 46, 3, 39, 11, 47];

    private static byte[] AdPacket(params (int Register, ulong Data)[] registers)
    {
        var b = new byte[16 + registers.Length * 16];
        BinaryPrimitives.WriteUInt64LittleEndian(b,
            (ulong)registers.Length | (1UL << 15) | (1UL << 60)); // NLOOP, EOP, NREG=1, FLG=0
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), 0xE); // A+D
        for (var i = 0; i < registers.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16 + i * 16), registers[i].Data);
            b[16 + i * 16 + 8] = (byte)registers[i].Register;
        }

        return b;
    }

    private static byte[] ImagePacket(byte[] data)
    {
        var b = new byte[16 + data.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(b,
            (ulong)(data.Length / 16) | (1UL << 15) | (2UL << 58)); // FLG=2 IMAGE
        data.CopyTo(b.AsSpan(16));
        return b;
    }

    private static byte[] Transfer(int dbw, int x, int y, int w, int h, byte[] data)
    {
        var bitbltbuf = (ulong)dbw << 48; // DBP 0, DBW, DPSM 0 = PSMCT32
        var trxpos = ((ulong)x << 32) | ((ulong)y << 48);
        var trxreg = (uint)w | ((ulong)(uint)h << 32);
        return
        [
            .. AdPacket((RegisterBitbltbuf, bitbltbuf), (RegisterTrxpos, trxpos), (RegisterTrxreg, trxreg),
                (RegisterTrxdir, 0)),
            .. ImagePacket(data)
        ];
    }

    private static byte[] Texture(int width, int height, uint flags, params byte[][] packets)
    {
        var payload = packets.SelectMany(p => p).ToArray();
        var b = new byte[BosTexture.HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)(payload.Length / 16));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0xc), 0x7664C2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x10), 0x80);
        payload.CopyTo(b.AsSpan(BosTexture.HeaderLength));
        return b;
    }

    /// <summary>The CLUT as the disc stores it: logical entry i at CSM1 slot i with bits 3 and 4 swapped.</summary>
    private static byte[] ClutBytes(uint[] logical)
    {
        var b = new byte[1024];
        for (var i = 0; i < 256; i++)
        {
            var slot = (i & ~0x18) | ((i & 8) << 1) | ((i & 16) >> 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(slot * 4), logical[i]);
        }

        return b;
    }

    private static uint[] TestClut()
    {
        var clut = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var alpha = (i & 1) == 0 ? 0x80u : 0x40u;
            clut[i] = (uint)i | ((uint)(255 - i) << 8) | ((uint)(i ^ 0x5A) << 16) | (alpha << 24);
        }

        return clut;
    }

    /// <summary>A 16×16 tile swizzled by the GAME'S table: transfer byte p carries texel table[p].</summary>
    private static byte[] SwizzleTile(byte[] indices)
    {
        var table = BosGsLayout.BuildGameBlockTable();
        var block = new byte[256];
        for (var p = 0; p < 256; p++)
        {
            block[p] = indices[table[p]];
        }

        return block;
    }

    [Fact]
    public void GameBlockTable_MatchesTheDecompiledFormulaAndIsAPermutation()
    {
        var table = BosGsLayout.BuildGameBlockTable();

        Assert.Equal(GameTableHead, table.Take(16));
        Assert.Equal(256, table.Distinct().Count());
    }

    [Fact]
    public void Csm1ClutIndex_SwapsEntriesEightToFifteenWithSixteenToTwentyThree()
    {
        Assert.Equal(0, BosGsLayout.Csm1ClutIndex(0));
        Assert.Equal(16, BosGsLayout.Csm1ClutIndex(8));
        Assert.Equal(8, BosGsLayout.Csm1ClutIndex(16));
        Assert.Equal(24, BosGsLayout.Csm1ClutIndex(24));
        Assert.Equal(48, BosGsLayout.Csm1ClutIndex(40));
        Assert.Equal(255, BosGsLayout.Csm1ClutIndex(255));
    }

    [Fact]
    public void Parse_UnswizzlesATileThroughTheGsLayoutToTheGamesOwnBlockTable()
    {
        // Every texel a distinct index (7 is coprime with 256), so a wrong address maps to a wrong colour.
        var indices = Enumerable.Range(0, 256).Select(t => (byte)(t * 7 + 3)).ToArray();
        var clut = TestClut();
        var bytes = Texture(16, 16, 0x14,
            Transfer(1, 0, 0, 16, 16, ClutBytes(clut)),
            Transfer(1, 0, 0, 8, 8, SwizzleTile(indices)));

        var texture = BosTexture.Parse(bytes, "tile.tex");

        Assert.Equal(16, texture.Width);
        Assert.Equal(16, texture.Height);
        Assert.False(texture.IsTrueColour);
        Assert.True(texture.IsFullyCovered);
        Assert.Equal(2, texture.Uploads.Count);
        Assert.Equal(0x13, texture.PixelStorageFormat);
        Assert.Equal(indices, texture.Indices);
        for (var t = 0; t < 256; t++)
        {
            var colour = clut[indices[t]];
            Assert.Equal((byte)colour, texture.Rgba[t * 4]);
            Assert.Equal((byte)(colour >> 8), texture.Rgba[t * 4 + 1]);
            Assert.Equal((byte)(colour >> 16), texture.Rgba[t * 4 + 2]);
            Assert.Equal((indices[t] & 1) == 0 ? 255 : 128, texture.Rgba[t * 4 + 3]);
        }
    }

    [Fact]
    public void Parse_PlacesHandComputedTransferBytesOnTheirTexels()
    {
        // Evaluated by hand from 0x0019F368/0x0019F398 for a = 0..1, b = 0: transfer byte 1 is
        // texel 36 (row 2, column 4), byte 2 is texel 8 (row 0, column 8), byte 4 is texel 1
        // (row 0, column 1). Neither the game table nor the GS address tables are consulted here.
        var block = new byte[256];
        block[1] = 0xAA;
        block[2] = 0xBB;
        block[4] = 0xCC;
        var texture = BosTexture.Parse(
            Texture(16, 16, 0x14, Transfer(1, 0, 0, 16, 16, ClutBytes(TestClut())), Transfer(1, 0, 0, 8, 8, block)),
            "hand.tex");

        Assert.Equal(0xAA, texture.Indices![2 * 16 + 4]);
        Assert.Equal(0xBB, texture.Indices[8]);
        Assert.Equal(0xCC, texture.Indices[1]);
        Assert.Equal(0, texture.Indices[36 + 1]);
        Assert.Equal(253, texture.Indices.Count(i => i == 0));
    }

    [Fact]
    public void Parse_ReadsTheClutInCsm1Order()
    {
        // Logical entry 8 is stored at slot 16: a decoder reading the CLUT linearly would hand
        // texel index 8 the colour of entry 16.
        var clut = TestClut();
        Assert.Equal(8u, clut[8] & 0xFF);
        Assert.Equal(16u, clut[16] & 0xFF);

        var indices = new byte[256];
        Array.Fill(indices, (byte)8);
        var texture = BosTexture.Parse(
            Texture(16, 16, 0x14, Transfer(1, 0, 0, 16, 16, ClutBytes(clut)),
                Transfer(1, 0, 0, 8, 8, SwizzleTile(indices))),
            "clut.tex");

        Assert.Equal(clut, texture.Clut);
        Assert.Equal(8, texture.Rgba[0]);
        Assert.Equal(255 - 8, texture.Rgba[1]);
    }

    [Fact]
    public void Parse_TreatsAnAlphaOfZeroOrOneOnEverySampledTexelAsACoverageFlag()
    {
        // 2,137 shipped CLUT textures sample nothing but GS alpha 0 or 1 (1/128 is no visible
        // surface in any blend mode). Entry 5 is alpha 0 and entry 9 alpha 1 here; the graded
        // entries 0x40/0x80 exist in the CLUT but no texel samples them, so the flag still holds.
        var clut = TestClut();
        clut[5] = 0x00112233u;
        clut[9] = 0x01445566u;
        var indices = new byte[256];
        Array.Fill(indices, (byte)9);
        indices[3] = 5;
        var texture = BosTexture.Parse(
            Texture(16, 16, 0x14, Transfer(1, 0, 0, 16, 16, ClutBytes(clut)),
                Transfer(1, 0, 0, 8, 8, SwizzleTile(indices))),
            "flag.tex");

        Assert.True(texture.AlphaIsFlag);
        Assert.Equal(255, texture.Rgba[3]);
        Assert.Equal(0, texture.Rgba[3 * 4 + 3]);
        Assert.Equal(0x66, texture.Rgba[0]);

        // One sampled texel at alpha 2 and the texture is graded: 0x80 → 255, 2 → 4, 1 → 2.
        clut[7] = 0x02000000u;
        indices[4] = 7;
        indices[5] = 0;
        var graded = BosTexture.Parse(
            Texture(16, 16, 0x14, Transfer(1, 0, 0, 16, 16, ClutBytes(clut)),
                Transfer(1, 0, 0, 8, 8, SwizzleTile(indices))),
            "graded.tex");
        Assert.False(graded.AlphaIsFlag);
        Assert.Equal(2, graded.Rgba[3]);
        Assert.Equal(4, graded.Rgba[4 * 4 + 3]);
        Assert.Equal(255, graded.Rgba[5 * 4 + 3]);
    }

    [Fact]
    public void Parse_RendersTexelsNoTransferWroteAsTransparent()
    {
        // 462 shipped textures upload less than w/2 × h/2. A 32×16 texture whose only pixel
        // transfer is the left 8×8 PSMCT32 quarter covers its left 16 columns only.
        var indices = new byte[256];
        Array.Fill(indices, (byte)2);
        var clut = TestClut();
        var texture = BosTexture.Parse(
            Texture(32, 16, 0x14, Transfer(1, 0, 0, 16, 16, ClutBytes(clut)),
                Transfer(1, 0, 0, 8, 8, SwizzleTile(indices))),
            "half.tex");

        Assert.Equal(256, texture.CoveredTexels);
        Assert.False(texture.IsFullyCovered);
        Assert.Equal(255, texture.Rgba[(0 * 32 + 15) * 4 + 3]);
        Assert.Equal(0, texture.Rgba[(0 * 32 + 16) * 4 + 3]);
        Assert.Equal(0, texture.Rgba[(15 * 32 + 31) * 4 + 3]);
    }

    [Fact]
    public void Parse_DecodesATrueColourTextureWithoutAClut()
    {
        // The three flag-0x20 textures: one PSMCT32 transfer that IS the image.
        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < 256; i++)
        {
            pixels[i * 4] = (byte)i;
            pixels[i * 4 + 1] = (byte)(i * 3);
            pixels[i * 4 + 2] = (byte)(255 - i);
            pixels[i * 4 + 3] = 0x80;
        }

        // PSMCT32 written and read through the same layout is the identity, so the transfer
        // order of a 16×16 image at dbw 1 is what the texels come back as.
        var texture = BosTexture.Parse(Texture(16, 16, 0x420, Transfer(1, 0, 0, 16, 16, pixels)), "true.tex");

        Assert.True(texture.IsTrueColour);
        Assert.Null(texture.Indices);
        Assert.True(texture.IsFullyCovered);
        for (var i = 0; i < 256; i++)
        {
            Assert.Equal(pixels[i * 4], texture.Rgba[i * 4]);
            Assert.Equal(pixels[i * 4 + 2], texture.Rgba[i * 4 + 2]);
            Assert.Equal(255, texture.Rgba[i * 4 + 3]);
        }
    }

    [Fact]
    public void Parse_RefusesAClutlessTextureWithoutTheTrueColourFlag()
    {
        // An 8×8 first transfer is not a CLUT; with flag 0x20 clear the file fits neither reading.
        var error = Assert.Throws<InvalidDataException>(() =>
            BosTexture.Parse(Texture(16, 16, 0x14, Transfer(1, 0, 0, 8, 8, new byte[256])), "odd.tex"));
        Assert.Contains("flag 0x20", error.Message, StringComparison.Ordinal);

        // And a CLUT that is followed by nothing is refused too.
        var lonely = Assert.Throws<InvalidDataException>(() =>
            BosTexture.Parse(Texture(16, 16, 0x14, Transfer(1, 0, 0, 16, 16, ClutBytes(TestClut()))), "clut.tex"));
        Assert.Contains("no pixel transfer", lonely.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RequiresThePacketWalkToConsumeTheSectionExactly()
    {
        var indices = new byte[256];
        var good = Texture(16, 16, 0x14, Transfer(1, 0, 0, 16, 16, ClutBytes(TestClut())),
            Transfer(1, 0, 0, 8, 8, SwizzleTile(indices)));
        var trailing = new byte[good.Length + 16];
        good.CopyTo(trailing, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(trailing.AsSpan(6), (ushort)((trailing.Length - 0x80) / 16));

        var error = Assert.Throws<InvalidDataException>(() => BosTexture.Parse(trailing, "tail.tex"));
        Assert.Contains("neither A+D nor IMAGE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsTexture_RequiresTheHeaderRelations()
    {
        var indices = new byte[256];
        var good = Texture(16, 16, 0x14, Transfer(1, 0, 0, 16, 16, ClutBytes(TestClut())),
            Transfer(1, 0, 0, 8, 8, SwizzleTile(indices)));
        Assert.True(BosTexture.IsTexture(good));

        var wrongQwords = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(wrongQwords.AsSpan(6), 1);
        Assert.False(BosTexture.IsTexture(wrongQwords));

        var wrongGifOffset = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongGifOffset.AsSpan(0x10), 0x90);
        Assert.False(BosTexture.IsTexture(wrongGifOffset));

        var relocated = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(relocated.AsSpan(0x1c), 0xFFFFFFFF);
        Assert.False(BosTexture.IsTexture(relocated));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0x40, 128)]
    [InlineData(0x7F, 253)]
    [InlineData(0x80, 255)]
    [InlineData(0xFF, 255)]
    public void ScaleAlpha_TreatsGsZeroEightyAsOpaque(int gs, int expected)
    {
        Assert.Equal(expected, BosTexture.ScaleAlpha((byte)gs));
    }

    [Fact]
    public void FindTextureSections_ReportsOnlyTexShapedSections()
    {
        var tex = Texture(16, 16, 0x14, Transfer(1, 0, 0, 16, 16, ClutBytes(TestClut())),
            Transfer(1, 0, 0, 8, 8, SwizzleTile(new byte[256])));
        var entries = new List<BosClumpFixture.Entry>
        {
            new(BosAssetHash.Compute("halo1.tex"), tex),
            new(BosAssetHash.Compute("exptable.exp"), new byte[300])
        };
        var b = BosClumpFixture.Build(256, entries, "inventry.clp");
        var clump = BosClumpFile.Parse(b, "INVENTRY.CLP");

        var found = BosTexture.FindTextureSections(b, clump).ToList();
        Assert.Equal(BosAssetHash.Compute("halo1.tex"), Assert.Single(found).Tag);
        Assert.Equal("halo1", BosKnownAssetNames.NameOrTag(found[0].Tag));
        Assert.Equal("tag_00000001", BosKnownAssetNames.NameOrTag(1));
    }
}