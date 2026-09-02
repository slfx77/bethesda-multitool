using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Imaging;

/// <summary>
///     Tests for <see cref="Palette" />: 6-bit VGA promotion exactness, Arena/Daggerfall COL
///     header validation, and the opt-in transparent index.
/// </summary>
public class PaletteTests
{
    /// <summary>Builds a 768-byte RGB block with specific entries set, the rest zero.</summary>
    private static byte[] BuildRgb768(params (int Index, byte R, byte G, byte B)[] entries)
    {
        var rgb = new byte[Palette.RgbByteCount];
        foreach (var (index, r, g, b) in entries)
        {
            rgb[index * 3] = r;
            rgb[index * 3 + 1] = g;
            rgb[index * 3 + 2] = b;
        }

        return rgb;
    }

    // Hand-computed from rgb8 = (v << 2) | (v >> 4):
    //   0x00 -> 0x00, 0x01 -> 0x04, 0x0F -> 0x3C, 0x10 -> 0x41,
    //   0x15 -> 0x55, 0x20 -> 0x82, 0x2A -> 0xAA, 0x30 -> 0xC3, 0x3F -> 0xFF.
    [Theory]
    [InlineData(0x00, 0x00)]
    [InlineData(0x01, 0x04)]
    [InlineData(0x0F, 0x3C)]
    [InlineData(0x10, 0x41)]
    [InlineData(0x15, 0x55)]
    [InlineData(0x20, 0x82)]
    [InlineData(0x2A, 0xAA)]
    [InlineData(0x30, 0xC3)]
    [InlineData(0x3F, 0xFF)]
    public void FromVga6Bit_PromotesComponent_HandComputedTable(byte vga, byte expected)
    {
        var palette = Palette.FromVga6Bit(BuildRgb768((0, vga, vga, vga)));

        var (r, g, b, a) = palette.GetEntry(0);
        Assert.Equal(expected, r);
        Assert.Equal(expected, g);
        Assert.Equal(expected, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public void FromVga6Bit_PromotesAll64Values_MatchesSpecFormula()
    {
        // Entry v holds (v, v, v) for every 6-bit value 0..63.
        var rgb = new byte[Palette.RgbByteCount];
        for (var v = 0; v < 64; v++)
        {
            rgb[v * 3] = (byte)v;
            rgb[v * 3 + 1] = (byte)v;
            rgb[v * 3 + 2] = (byte)v;
        }

        var palette = Palette.FromVga6Bit(rgb);

        for (var v = 0; v < 64; v++)
        {
            // Spec formula, recomputed here: rgb8 = (v << 2) | (v >> 4).
            var expected = (byte)((v << 2) | (v >> 4));
            var (r, g, b, a) = palette.GetEntry(v);
            Assert.Equal(expected, r);
            Assert.Equal(expected, g);
            Assert.Equal(expected, b);
            Assert.Equal(255, a);
        }
    }

    [Fact]
    public void FromRgb8_CopiesTripletsUnchanged()
    {
        var palette = Palette.FromRgb8(BuildRgb768((0, 10, 200, 255), (255, 1, 2, 3)));

        Assert.Equal(((byte)10, (byte)200, (byte)255, (byte)255), palette.GetEntry(0));
        Assert.Equal(((byte)1, (byte)2, (byte)3, (byte)255), palette.GetEntry(255));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), palette.GetEntry(128));
    }

    [Theory]
    [InlineData(767)]
    [InlineData(769)]
    [InlineData(0)]
    public void FromVga6Bit_And_FromRgb8_RejectWrongLength(int length)
    {
        Assert.Throws<ArgumentException>(() => Palette.FromVga6Bit(new byte[length]));
        Assert.Throws<ArgumentException>(() => Palette.FromRgb8(new byte[length]));
    }

    /// <summary>Builds a 776-byte Arena COL file: u32 LE length, u32 LE version, 768 RGB bytes.</summary>
    private static byte[] BuildArenaCol(uint declaredLength = 776, uint version = 0xB123)
    {
        var file = new byte[776];
        BinaryTestWriter.WriteUInt32LE(file, 0, declaredLength);
        BinaryTestWriter.WriteUInt32LE(file, 4, version);
        return file;
    }

    /// <summary>
    ///     Arena COL components are FULL-RANGE 8-bit and must not be promoted as 6-bit VGA.
    ///     Measured on the retail install 2026-09-01: PAL.COL, CHARSHT.COL, DAYTIME.COL and
    ///     DREARY.COL all declare version 0xB123 and reach components of 255, 255, 212 and 87 —
    ///     every one above the 6-bit ceiling of 63. This test previously pinned the opposite
    ///     (a promoting loader); the promotion turned the grey 139,127,127 into cyan 44,255,255
    ///     and rendered creature sprites in rainbow speckle.
    /// </summary>
    [Fact]
    public void LoadArenaCol_KeepsFullRangePayloadUnshifted()
    {
        var file = BuildArenaCol();
        file[8] = 139;
        file[9] = 127;
        file[10] = 127;
        file[8 + (255 * 3)] = 255;
        file[8 + (255 * 3) + 1] = 128;
        file[8 + (255 * 3) + 2] = 64;

        var palette = Palette.LoadArenaCol(file);

        Assert.Equal(((byte)139, (byte)127, (byte)127, (byte)255), palette.GetEntry(0));
        Assert.Equal(((byte)255, (byte)128, (byte)64, (byte)255), palette.GetEntry(255));
    }

    [Fact]
    public void LoadArenaCol_RejectsWrongMagic()
    {
        Assert.Throws<InvalidDataException>(() => Palette.LoadArenaCol(BuildArenaCol(version: 0x1234)));
    }

    [Fact]
    public void ArenaAndDaggerfallCol_AreTheSameFormat()
    {
        // Same layout, same 0xB123 magic, same full-range payload — the two entry points differ
        // only in the game they name in error messages.
        var file = BuildArenaCol();
        file[8 + 3] = 200;
        file[8 + 4] = 100;
        file[8 + 5] = 50;

        Assert.Equal(
            Palette.LoadArenaCol(file).Rgba.ToArray(),
            Palette.LoadDaggerfallCol(file).Rgba.ToArray());
    }

    [Fact]
    public void LoadArenaCol_RejectsWrongFileLength()
    {
        Assert.Throws<InvalidDataException>(() => Palette.LoadArenaCol(new byte[775]));
        Assert.Throws<InvalidDataException>(() => Palette.LoadArenaCol(new byte[777]));
    }

    [Fact]
    public void LoadArenaCol_RejectsWrongDeclaredLength()
    {
        var file = BuildArenaCol(declaredLength: 770);

        Assert.Throws<InvalidDataException>(() => Palette.LoadArenaCol(file));
    }

    /// <summary>
    ///     Builds a 776-byte Daggerfall COL file: i32 LE size, u16 LE magic, u16 LE version,
    ///     768 RGB bytes.
    /// </summary>
    private static byte[] BuildDaggerfallCol(ushort magic = 0xB123)
    {
        var file = new byte[776];
        BinaryTestWriter.WriteUInt32LE(file, 0, 776);
        BinaryTestWriter.WriteUInt16LE(file, 4, magic);
        BinaryTestWriter.WriteUInt16LE(file, 6, 0);
        return file;
    }

    [Fact]
    public void LoadDaggerfallCol_KeepsFullRangePayloadUnshifted()
    {
        var file = BuildDaggerfallCol();
        // Entry 1 = (1, 2, 3): a 6-bit loader would promote this to (4, 8, 12).
        file[8 + 3] = 1;
        file[8 + 4] = 2;
        file[8 + 5] = 3;
        // Entry 0 = (255, 128, 64): 255 exceeds the 6-bit range entirely.
        file[8] = 255;
        file[9] = 128;
        file[10] = 64;

        var palette = Palette.LoadDaggerfallCol(file);

        Assert.Equal(((byte)255, (byte)128, (byte)64, (byte)255), palette.GetEntry(0));
        Assert.Equal(((byte)1, (byte)2, (byte)3, (byte)255), palette.GetEntry(1));
    }

    [Fact]
    public void LoadDaggerfallCol_RejectsWrongMagic()
    {
        var file = BuildDaggerfallCol(magic: 0xB124);

        Assert.Throws<InvalidDataException>(() => Palette.LoadDaggerfallCol(file));
    }

    [Fact]
    public void LoadDaggerfallCol_RejectsWrongFileLength()
    {
        Assert.Throws<InvalidDataException>(() => Palette.LoadDaggerfallCol(new byte[768]));
    }

    [Fact]
    public void WithTransparentIndex_ZeroesOnlyThatAlpha_AndLeavesOriginalUntouched()
    {
        var original = Palette.FromRgb8(BuildRgb768((0, 9, 8, 7)));

        var transparent = original.WithTransparentIndex(0);

        Assert.Equal(((byte)9, (byte)8, (byte)7, (byte)0), transparent.GetEntry(0));
        Assert.Equal((byte)255, transparent.GetEntry(1).A);
        Assert.Equal((byte)255, transparent.GetEntry(255).A);
        // The source palette is immutable — its entry 0 is still opaque.
        Assert.Equal((byte)255, original.GetEntry(0).A);
    }

    [Fact]
    public void WithTransparentIndex_RejectsOutOfRangeIndex()
    {
        var palette = Palette.FromRgb8(new byte[Palette.RgbByteCount]);

        Assert.Throws<ArgumentOutOfRangeException>(() => palette.WithTransparentIndex(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => palette.WithTransparentIndex(256));
    }
}
