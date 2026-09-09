using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Fallout;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Fallout;

/// <summary>
///     Synthetic vectors for Fallout 1 maps, shaped after the 72 retail <c>.MAP</c> files measured
///     2026-09-06 (version 19 on all of them; elevation flags 0x0/0x8/0xC only).
///     <para>
///         The fact worth pinning hardest is the 236-byte header. Scanning for the first plausible
///         tile block finds 56, because the 44 unused header dwords are zeros and zero passes a
///         range check — so a test that only asserts "the tiles look like tile ids" would pass on
///         the wrong offset.
///     </para>
/// </summary>
public sealed class FalloutMapFileTests
{
    private static byte[] Map(uint flags = 0xC, string name = "CARAVAN.MAP", uint script = FalloutMapFile.NoScript,
        ushort roof = 1, ushort floor = 1, int trailing = 0)
    {
        var count = FalloutMapFile.ElevationCount(flags);
        var b = new byte[FalloutMapFile.HeaderLength + count * FalloutMapFile.ElevationLength + trailing];
        BinaryPrimitives.WriteUInt32BigEndian(b, FalloutMapFile.Version19);
        Encoding.ASCII.GetBytes(name).CopyTo(b, 4);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 21_302);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(28), 2);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(36), script);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(40), flags);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(44), 1);

        for (var e = 0; e < count; e++)
        {
            var at = FalloutMapFile.HeaderLength + e * FalloutMapFile.ElevationLength;
            for (var i = 0; i < FalloutMapFile.GridWidth * FalloutMapFile.GridHeight; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at + i * 4), (ushort)(roof + e));
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at + i * 4 + 2), (ushort)(floor + e));
            }
        }

        return b;
    }

    [Fact]
    public void Parse_ReadsTheHeaderBigEndian()
    {
        var map = FalloutMapFile.Parse(Map(script: 659), "CARAVAN.MAP");

        Assert.Equal(FalloutMapFile.Version19, map.Version);
        Assert.Equal("CARAVAN.MAP", map.MapName); // the header name equals the file name on 72/72
        Assert.Equal(21_302u, map.PlayerPosition);
        Assert.Equal(2u, map.PlayerOrientation);
        Assert.Equal(659u, map.ScriptId);
        Assert.True(map.HasScript);
    }

    [Fact]
    public void Parse_ReadsTheNameToItsTerminatorNotToTheEndOfTheField()
    {
        // ⚠ The name is NUL-TERMINATED inside a fixed 16-byte field, not NUL-PADDED across it: the
        // bytes after the terminator are authoring leftovers. Retail CAVES.MAP carries exactly this
        // and a trailing-NUL trim yields "CAVES.MAP AP" — which is how the real reader was caught.
        var b = Map(name: "CAVES.MAP");
        " AP"u8.CopyTo(b.AsSpan(4 + 10));

        Assert.Equal("CAVES.MAP", FalloutMapFile.Parse(b, "CAVES.MAP").MapName);
    }

    [Fact]
    public void Parse_TreatsMinusOneAsNoScript()
    {
        Assert.False(FalloutMapFile.Parse(Map(), "M.MAP").HasScript);
    }

    [Theory]
    [InlineData(0x0u, 3)]
    [InlineData(0x8u, 2)]
    [InlineData(0xCu, 1)]
    public void ElevationCount_ReadsASetBitAsAbsent(uint flags, int expected)
    {
        // ⚠ The polarity is the trap: a SET bit means the elevation is MISSING. Reading it the
        // other way gives 0 elevations for the 41 retail maps that use 0xC and 3 for the 14 that
        // use 0x0 — exactly inverted, and the file still "parses".
        Assert.Equal(expected, FalloutMapFile.ElevationCount(flags));
        Assert.Equal(expected, FalloutMapFile.Parse(Map(flags), "M.MAP").Elevations.Count);
    }

    [Fact]
    public void Parse_NumbersElevationsByTheirFlagBitNotByPosition()
    {
        // ⚠ Fallout 2 ships two maps with flags 0x2 — elevation 0 ABSENT, 1 and 2 present — so the
        // first grid stored in the file is elevation 1. Numbering the stored grids 0,1,2 by position
        // mislabels every one of them, and nothing else in the file would contradict it.
        var map = FalloutMapFile.Parse(Map(0x2, roof: 40, floor: 50), "M.MAP");

        Assert.Equal(2, map.Elevations.Count);
        Assert.Equal([1, 2], map.Elevations.Select(e => e.Index));

        // The grids themselves are still consecutive from the header, so the first one read is the
        // first one stored — only its NUMBER changes.
        Assert.Equal(new FalloutMapTile(40, 50), map.Elevations[0].Tiles[0]);
        Assert.Equal(new FalloutMapTile(41, 51), map.Elevations[1].Tiles[0]);
    }

    [Fact]
    public void Parse_AcceptsFalloutTwosVersionTwenty()
    {
        var b = Map();
        BinaryPrimitives.WriteUInt32BigEndian(b, FalloutMapFile.Version20);

        Assert.True(FalloutMapFile.IsMapFile(b));
        Assert.Equal(FalloutMapFile.Version20, FalloutMapFile.Parse(b, "FO2.MAP").Version);
    }

    [Fact]
    public void Parse_ReadsEveryElevationsFullGrid()
    {
        var map = FalloutMapFile.Parse(Map(0x0, roof: 10, floor: 20), "M.MAP");

        Assert.Equal(3, map.Elevations.Count);
        Assert.All(map.Elevations,
            e => Assert.Equal(FalloutMapFile.GridWidth * FalloutMapFile.GridHeight, e.Tiles.Count));

        // Each synthetic elevation is stamped with its own value, so a reader that read the same
        // block three times, or strided wrongly, shows up here.
        Assert.Equal(new FalloutMapTile(10, 20), map.Elevations[0].Tiles[0]);
        Assert.Equal(new FalloutMapTile(11, 21), map.Elevations[1].Tiles[0]);
        Assert.Equal(new FalloutMapTile(12, 22), map.Elevations[2].Tiles[^1]);
    }

    [Fact]
    public void Parse_StartsTheGridAtTwoThirtySixNotFiftySix()
    {
        // ⚠⚠ THE trap this format sets. The 44 unused header dwords are zeros, and zero passes a
        // "valid tile id" range test, so a scan for the first plausible grid lands on 56 and is
        // wrong by 180 bytes. Here the header is filled with a value that IS a plausible tile id,
        // so only a reader using the real offset gets the stamped grid back.
        var b = Map(roof: 7, floor: 9);
        for (var o = 56; o < FalloutMapFile.HeaderLength; o += 2)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), 3);
        }

        var map = FalloutMapFile.Parse(b, "M.MAP");

        Assert.Equal(new FalloutMapTile(7, 9), map.Elevations[0].Tiles[0]);
        Assert.DoesNotContain(map.Elevations[0].Tiles, t => t.Roof == 3);
    }

    [Fact]
    public void Parse_HandsBackTheObjectSectionAfterDecodingScripts()
    {
        const int scriptListBytes = 5 * sizeof(uint);
        var bytes = Map(trailing: scriptListBytes + 64);
        var objectOffset = FalloutMapFile.HeaderLength + FalloutMapFile.ElevationLength + scriptListBytes;
        bytes.AsSpan(objectOffset).Fill(0xA5);
        var map = FalloutMapFile.Parse(bytes, "M.MAP");

        // Five empty script lists are decoded; without a prototype resolver the object section
        // must remain byte-for-byte available to a later reader.
        Assert.True(map.ScriptsDecoded);
        Assert.Empty(map.Scripts);
        Assert.False(map.ObjectsDecoded);
        Assert.Equal(bytes[objectOffset..], map.Undecoded.ToArray());
    }

    [Fact]
    public void Parse_RejectsAWrongVersionAndATruncatedGrid()
    {
        // 19 and 20 are the two shipped versions; anything else is refused rather than guessed at.
        var wrongVersion = Map();
        BinaryPrimitives.WriteUInt32BigEndian(wrongVersion, 21);
        Assert.Throws<InvalidDataException>(() => FalloutMapFile.Parse(wrongVersion, "FO3.MAP"));

        var truncated = Map()[..(FalloutMapFile.HeaderLength + 100)];
        var error = Assert.Throws<InvalidDataException>(() => FalloutMapFile.Parse(truncated, "SHORT.MAP"));
        Assert.Contains("1 elevations", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsMapFile_RecognisesTheVersionWord()
    {
        Assert.True(FalloutMapFile.IsMapFile(Map()));
        Assert.False(FalloutMapFile.IsMapFile(new byte[FalloutMapFile.HeaderLength]));
        Assert.False(FalloutMapFile.IsMapFile([0, 0, 0, 19]));
    }
}
