using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Vectors for the two Fallout Tactics string encodings and the bounds-checked cursor that
///     every save reader walks through, shaped after <c>Snake.sav</c> measured 2026-09-07.
/// </summary>
public sealed class TacticsCursorTests
{
    [Fact]
    public void WideString_ReadsAUtf16StringWhosePrefixHasBit31Set()
    {
        // ⚑ The bytes are written out BY HAND from the independently known string "New Save Game"
        // (13 characters → 0x8000000D), not produced by an encoder — the fixture's own bytes at
        // offset 15 are exactly these.
        byte[] bytes =
        [
            0x0D, 0x00, 0x00, 0x80,
            (byte)'N', 0, (byte)'e', 0, (byte)'w', 0, (byte)' ', 0,
            (byte)'S', 0, (byte)'a', 0, (byte)'v', 0, (byte)'e', 0, (byte)' ', 0,
            (byte)'G', 0, (byte)'a', 0, (byte)'m', 0, (byte)'e', 0,
            0xEE // a byte that must NOT be consumed
        ];

        var cursor = new TacticsCursor(bytes, "vector");

        Assert.Equal("New Save Game", cursor.WideString());
        Assert.Equal(4 + 13 * 2, cursor.Position);
        Assert.Equal(0xEE, cursor.U8());
    }

    [Fact]
    public void WideString_ReadsTheEmptyStringFromTheBareFlag()
    {
        // ⛔ "u32 1 then a LONE 0x80 byte" was the first reading of the save header. The 0x80 is
        // the high byte of 00 00 00 80 — an EMPTY wide string — as the second header in the same
        // file proved (its body is 00 | 2B 00 00 80 | 'locale/...', which the u32 reading garbles).
        byte[] bytes = [0x01, 0x00, 0x00, 0x00, 0x80, 0x0D, 0x00, 0x00, 0x80, (byte)'N', 0];

        var cursor = new TacticsCursor(bytes, "vector");

        Assert.Equal(1, cursor.U8());
        Assert.Equal(string.Empty, cursor.WideString());
        Assert.Equal(5, cursor.Position);
    }

    [Fact]
    public void String_DispatchesOnTheFlagSoAsciiAndWideCoexist()
    {
        // Both encodings occur in ONE file: the save's own fields are wide, the <esh> bags inside
        // it are ASCII ('SMG' as 03 00 00 00 'S' 'M' 'G').
        byte[] bytes =
        [
            0x03, 0x00, 0x00, 0x00, (byte)'S', (byte)'M', (byte)'G',
            0x02, 0x00, 0x00, 0x80, (byte)'O', 0, (byte)'K', 0
        ];

        var cursor = new TacticsCursor(bytes, "vector");

        Assert.Equal("SMG", cursor.String());
        Assert.Equal("OK", cursor.String());
        Assert.True(cursor.AtEnd);
    }

    [Fact]
    public void WideString_RefusesAnAsciiPrefixAndViceVersa()
    {
        // The flag assert is the misalignment detector: it fired twice while the campaign layout
        // was being derived, each time on a wrong reading. It must keep firing.
        byte[] ascii = [0x03, 0x00, 0x00, 0x00, (byte)'S', (byte)'M', (byte)'G'];
        byte[] wide = [0x01, 0x00, 0x00, 0x80, (byte)'A', 0];

        var error = Assert.Throws<InvalidDataException>(() => new TacticsCursor(ascii, "v").WideString());
        Assert.Contains("bit 31 clear", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => new TacticsCursor(wide, "v").AsciiString());
    }

    [Fact]
    public void ReadsRefuseToRunPastTheBuffer()
    {
        byte[] bytes = [0x10, 0x00, 0x00, 0x80, (byte)'A', 0, (byte)'B', 0];

        var error = Assert.Throws<InvalidDataException>(() => new TacticsCursor(bytes, "short.sav").WideString());
        Assert.Contains("short.sav @0", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => new TacticsCursor(bytes, "v", 6).U32());
    }

    [Fact]
    public void Tag_RequiresTheNamedTagAndReportsTheOneFound()
    {
        var bytes = "<world>\070\0...."u8.ToArray();

        var error = Assert.Throws<InvalidDataException>(() => new TacticsCursor(bytes, "v").Tag("saveh"));
        Assert.Contains("found <world>", error.Message, StringComparison.Ordinal);

        var cursor = new TacticsCursor(bytes, "v");
        var chunk = cursor.Tag("world");
        Assert.Equal("70", chunk.Version);
        Assert.Equal(11, cursor.Position);
    }

    [Fact]
    public void Properties_WalksAnEmbeddedEshBlockAndStopsAfterItsLastProperty()
    {
        // The same self-describing walk TacticsPropertyBag applies to a whole file, but in place —
        // a campaign holds 57 of these back to back, so the walk must stop where the block does.
        var b = new List<byte>();
        b.AddRange("<esh>\01\0"u8);
        b.AddRange(BitConverter.GetBytes(2u));
        b.AddRange(BitConverter.GetBytes(4u));
        b.AddRange("Name"u8);
        b.AddRange(BitConverter.GetBytes(4u)); // type
        b.AddRange(BitConverter.GetBytes(9u)); // size: u32 length + 5 chars
        b.AddRange(BitConverter.GetBytes(5u));
        b.AddRange("Vault"u8);
        b.AddRange(BitConverter.GetBytes(6u));
        b.AddRange("Radius"u8);
        b.AddRange(BitConverter.GetBytes(3u));
        b.AddRange(BitConverter.GetBytes(4u));
        b.AddRange(BitConverter.GetBytes(37u));
        b.Add(0xEE);

        var cursor = new TacticsCursor(b.ToArray(), "v");
        var properties = cursor.Properties();

        Assert.Equal(2, properties.Count);
        Assert.Equal("Name", properties[0].Name);
        // ⚠ Text payloads carry their OWN u32 length; PropertyText strips it where AsText would not.
        Assert.Equal("Vault", TacticsCursor.PropertyText(properties[0]));
        Assert.Equal("Radius", properties[1].Name);
        Assert.Equal(37u, BitConverter.ToUInt32(properties[1].Value.Span));
        Assert.Equal(0xEE, cursor.U8());
    }

    [Fact]
    public void Zeros_RefusesANonZeroByte()
    {
        var cursor = new TacticsCursor(new byte[] { 0, 0, 0, 1 }, "v");

        Assert.Throws<InvalidDataException>(() => cursor.Zeros(4, "padding"));
    }

    [Fact]
    public void RequireEnd_ThrowsUnlessTheWalkLandedOnTheLastByte()
    {
        var cursor = new TacticsCursor(new byte[] { 1, 2, 3, 4, 5 }, "v");
        cursor.U32();

        var error = Assert.Throws<InvalidDataException>(() => cursor.RequireEnd("record"));
        Assert.Contains("ends at 4 of 5", error.Message, StringComparison.Ordinal);

        cursor.U8();
        cursor.RequireEnd("record");
    }
}