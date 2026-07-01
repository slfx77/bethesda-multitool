using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Wire-format tests for the <c>MODS</c> ("Alternate Textures") subrecord decoder — the most
///     byte-sensitive piece of the alternate-texture feature. Grounded in the confirmed real record:
///     FNV STAT "…AtomicWranglerTall" carries <c>MODS</c> mapping shape <c>BB04:13</c> → TXST
///     <c>0x0016A885</c> → index 0.
/// </summary>
public sealed class AlternateTextureParserTests
{
    private static byte[] BuildMods(bool bigEndian, params (string Shape, uint Txst, int Index)[] entries)
    {
        var buffer = new List<byte>();
        AppendUInt32(buffer, (uint)entries.Length, bigEndian);
        foreach (var (shape, txst, index) in entries)
        {
            var nameBytes = Encoding.ASCII.GetBytes(shape);
            AppendUInt32(buffer, (uint)nameBytes.Length, bigEndian);
            buffer.AddRange(nameBytes);
            AppendUInt32(buffer, txst, bigEndian);
            AppendUInt32(buffer, unchecked((uint)index), bigEndian);
        }

        return buffer.ToArray();
    }

    private static void AppendUInt32(List<byte> buffer, uint value, bool bigEndian)
    {
        Span<byte> tmp = stackalloc byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(tmp, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        }

        buffer.AddRange(tmp.ToArray());
    }

    [Theory]
    [InlineData(false)] // PC little-endian
    [InlineData(true)]  // Xbox 360 big-endian
    public void Parse_DecodesTheAtomicWranglerBillboardEntry(bool bigEndian)
    {
        var mods = BuildMods(bigEndian, ("BB04:13", 0x0016A885u, 0));

        var entries = AlternateTextureParser.Parse(mods, bigEndian);

        var entry = Assert.Single(entries);
        Assert.Equal("BB04:13", entry.ShapeName);
        Assert.Equal(0x0016A885u, entry.TextureSetFormId);
        Assert.Equal(0, entry.Index);
    }

    [Fact]
    public void Parse_ExactBytes_MatchesConfirmedLittleEndianLayout()
    {
        // The literal bytes captured from FalloutNV.esm (PC): count=1, nameLen=7, "BB04:13",
        // TXST 0x0016A885, index 0.
        byte[] mods =
        [
            0x01, 0x00, 0x00, 0x00,
            0x07, 0x00, 0x00, 0x00,
            0x42, 0x42, 0x30, 0x34, 0x3A, 0x31, 0x33, // "BB04:13"
            0x85, 0xA8, 0x16, 0x00,
            0x00, 0x00, 0x00, 0x00
        ];

        var entry = Assert.Single(AlternateTextureParser.Parse(mods, isBigEndian: false));
        Assert.Equal("BB04:13", entry.ShapeName);
        Assert.Equal(0x0016A885u, entry.TextureSetFormId);
        Assert.Equal(0, entry.Index);
    }

    [Fact]
    public void Parse_MultipleEntries_DecodesEachInOrder()
    {
        var mods = BuildMods(false,
            ("BB04:13", 0x0016A885u, 0),
            ("Sign:02", 0x00ABCDEFu, 3));

        var entries = AlternateTextureParser.Parse(mods, isBigEndian: false);

        Assert.Equal(2, entries.Count);
        Assert.Equal("BB04:13", entries[0].ShapeName);
        Assert.Equal(0x0016A885u, entries[0].TextureSetFormId);
        Assert.Equal("Sign:02", entries[1].ShapeName);
        Assert.Equal(0x00ABCDEFu, entries[1].TextureSetFormId);
        Assert.Equal(3, entries[1].Index);
    }

    [Theory]
    [InlineData(0)] // empty
    [InlineData(2)] // just a partial count field
    public void Parse_TooShort_ReturnsEmpty(int length)
    {
        Assert.Empty(AlternateTextureParser.Parse(new byte[length], isBigEndian: false));
    }

    [Fact]
    public void Parse_TruncatedEntry_StopsCleanlyWithoutThrowing()
    {
        // A well-formed 2-entry header but the second entry's bytes are cut off mid-payload.
        var full = BuildMods(false, ("BB04:13", 0x0016A885u, 0), ("Cut", 0x11u, 1));
        var truncated = full[..(full.Length - 5)];

        var entries = AlternateTextureParser.Parse(truncated, isBigEndian: false);

        // First entry survives; the truncated tail is dropped rather than throwing.
        var entry = Assert.Single(entries);
        Assert.Equal("BB04:13", entry.ShapeName);
    }

    [Fact]
    public void Parse_CorruptNameLength_DoesNotOverRead()
    {
        // count=1, but nameLen claims 0x7FFFFFFF — must be rejected, not read past the buffer.
        byte[] mods =
        [
            0x01, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0x7F,
            0x41, 0x42
        ];

        Assert.Empty(AlternateTextureParser.Parse(mods, isBigEndian: false));
    }
}
