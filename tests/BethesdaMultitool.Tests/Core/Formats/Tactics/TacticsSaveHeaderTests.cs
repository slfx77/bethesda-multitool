using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Vectors for the <c>&lt;saveh&gt;</c> v2 header that opens a Fallout Tactics save AND the
///     mission snapshot archived inside it, shaped after <c>Snake.sav</c> measured 2026-09-07.
///     <para>
///         ⚑ The first test writes the whole header out BY HAND — the tag, the flag byte and every
///         length prefix — so nothing here is produced by the reader under test. Its five strings
///         are the fixture's own (<c>''</c>, <c>New Save Game</c>, <c>Snake</c>, <c>Brahmin Wood</c>,
///         <c>Jan 1 2197.  06:29</c>), which is why the arithmetic lands on the numbers the retail
///         file shows: the images start at byte 127 and the header is 319 bytes with empty slots
///         (the retail EMBEDDED header, whose one string is a 43-character path, is 309).
///     </para>
/// </summary>
public sealed class TacticsSaveHeaderTests
{
    private static readonly string[] FixtureStrings =
        ["", "New Save Game", "Snake", "Brahmin Wood", "Jan 1 2197.  06:29"];

    private static readonly float[] FixtureFloats = [30f, 30f, 36f, 0f, 0f, 0f];

    [Fact]
    public void Read_TakesAFlagFiveWideStringsEightSlotsAndSixFloats_FromBytesWrittenByHand()
    {
        // ⚑ Written out byte by byte: '<saveh>' NUL '2' NUL, the flag, then five wide strings whose
        // prefixes are (0x80000000 | character count) — 0x80000000 for the empty one, 0x8000000D
        // for "New Save Game" — each followed by UTF-16LE units with no terminator.
        byte[] head =
        [
            (byte)'<', (byte)'s', (byte)'a', (byte)'v', (byte)'e', (byte)'h', (byte)'>', 0, (byte)'2', 0,
            1,
            0x00, 0x00, 0x00, 0x80,
            0x0D, 0x00, 0x00, 0x80,
            (byte)'N', 0, (byte)'e', 0, (byte)'w', 0, (byte)' ', 0,
            (byte)'S', 0, (byte)'a', 0, (byte)'v', 0, (byte)'e', 0, (byte)' ', 0,
            (byte)'G', 0, (byte)'a', 0, (byte)'m', 0, (byte)'e', 0,
            0x05, 0x00, 0x00, 0x80,
            (byte)'S', 0, (byte)'n', 0, (byte)'a', 0, (byte)'k', 0, (byte)'e', 0,
            0x0C, 0x00, 0x00, 0x80,
            (byte)'B', 0, (byte)'r', 0, (byte)'a', 0, (byte)'h', 0, (byte)'m', 0, (byte)'i', 0, (byte)'n', 0,
            (byte)' ', 0, (byte)'W', 0, (byte)'o', 0, (byte)'o', 0, (byte)'d', 0,
            0x12, 0x00, 0x00, 0x80,
            (byte)'J', 0, (byte)'a', 0, (byte)'n', 0, (byte)' ', 0, (byte)'1', 0, (byte)' ', 0,
            (byte)'2', 0, (byte)'1', 0, (byte)'9', 0, (byte)'7', 0, (byte)'.', 0, (byte)' ', 0,
            (byte)' ', 0, (byte)'0', 0, (byte)'6', 0, (byte)':', 0, (byte)'2', 0, (byte)'9', 0
        ];

        // 10 (tag) + 1 (flag) + 4 + 30 + 14 + 28 + 40 (the five strings) = 127 — exactly where the
        // retail file's first <zar> begins.
        Assert.Equal(127, head.Length);

        byte[] emptySlot =
        [
            (byte)'<', (byte)'z', (byte)'a', (byte)'r', (byte)'>', 0, (byte)'4', 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0,
            0, 0, 0, 0
        ];
        Assert.Equal(21, emptySlot.Length);

        var bytes = new List<byte>(head);
        for (var i = 0; i < 8; i++)
        {
            bytes.AddRange(emptySlot);
        }

        foreach (var value in FixtureFloats)
        {
            bytes.AddRange(BitConverter.GetBytes(value));
        }

        // 127 + 8 * 21 + 6 * 4 = 319.
        Assert.Equal(319, bytes.Count);

        var cursor = new TacticsCursor(bytes.ToArray(), "vector");
        var header = TacticsSaveHeader.Read(cursor);

        Assert.Equal(1, header.Flag);
        Assert.Equal(FixtureStrings, header.Strings);
        Assert.Equal(string.Empty, header.SpeechTextPath);
        Assert.Equal("New Save Game", header.Title);
        Assert.Equal("Snake", header.SaveName);
        Assert.Equal("Brahmin Wood", header.MissionName);
        Assert.Equal("Jan 1 2197.  06:29", header.GameTime);
        Assert.Equal(8, header.Images.Count);
        Assert.All(header.Images, image =>
        {
            Assert.False(image.HasImage);
            Assert.False(image.HasPalette);
            Assert.Equal(21, image.RecordLength);
        });
        Assert.Equal(FixtureFloats, header.Floats);
        Assert.Equal(319, header.Length);
        Assert.True(cursor.AtEnd);
    }

    [Fact]
    public void Read_LeavesTheCursorImmediatelyAfterTheSixthFloat()
    {
        // The header is read in the middle of a stream — the archive directory follows it — so the
        // cursor must stop on the byte after the last float and not one further.
        var bytes = TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.SaveHeader(0, FixtureStrings, FixtureFloats),
            [0xEE]);

        var cursor = new TacticsCursor(bytes, "vector");
        var header = TacticsSaveHeader.Read(cursor);

        Assert.Equal(0, header.Flag);
        Assert.Equal(319, cursor.Position);
        Assert.Equal(0xEE, cursor.U8());
        Assert.True(cursor.AtEnd);
    }

    [Fact]
    public void Read_RefusesAVersionOtherThanTheMeasuredTwo()
    {
        var parts = new List<byte[]> { TacticsSyntheticBytes.Tag("saveh", "3"), new byte[] { 1 } };
        parts.AddRange(FixtureStrings.Select(TacticsSyntheticBytes.Wide));
        var bytes = TacticsSyntheticBytes.Concat([.. parts]);

        var error =
            Assert.Throws<InvalidDataException>(() => TacticsSaveHeader.Read(new TacticsCursor(bytes, "vector")));

        Assert.Contains("'3'", error.Message, StringComparison.Ordinal);
        Assert.Contains("saveh", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RefusesAStringWhosePrefixLacksTheWideFlag()
    {
        // ⚠ Both encodings share the u32 prefix, so an ASCII string where a wide one is due is not
        // a length error — it is a SILENT desynchronisation unless the flag bit is checked.
        var bytes = TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.Tag("saveh", "2"),
            [1],
            TacticsSyntheticBytes.Wide(""),
            TacticsSyntheticBytes.Ascii("New Save Game"),
            TacticsSyntheticBytes.Wide("Snake"),
            TacticsSyntheticBytes.Wide("Brahmin Wood"),
            TacticsSyntheticBytes.Wide("Jan 1 2197.  06:29"));

        var error =
            Assert.Throws<InvalidDataException>(() => TacticsSaveHeader.Read(new TacticsCursor(bytes, "vector")));

        Assert.Contains("bit 31 clear", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RefusesAHeaderThatEndsInsideItsImageSlots()
    {
        var complete = TacticsSyntheticBytes.SaveHeader(1, FixtureStrings, FixtureFloats);
        var truncated = complete[..200];

        var error = Assert.Throws<InvalidDataException>(() =>
            TacticsSaveHeader.Read(new TacticsCursor(truncated, "vector")));

        Assert.Contains("remain", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("saveh", true)]
    [InlineData("campaign", false)]
    [InlineData("world", false)]
    public void IsSaveHeader_MatchesTheSavehFramingAndNothingElse(string tag, bool expected)
    {
        var bytes = TacticsSyntheticBytes.Concat(TacticsSyntheticBytes.Tag(tag, "2"), [1, 2, 3, 4]);

        Assert.Equal(expected, TacticsSaveHeader.IsSaveHeader(bytes));
    }
}