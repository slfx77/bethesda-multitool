using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.VanBuren;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Vectors for the Van Buren prototype's <c>.stf</c> string table, shaped after the shipped
///     <c>English.stf</c> measured 2026-09-06: 3,281 strings tiling 52,508 → 254,846.
/// </summary>
public sealed class VanBurenStringTableTests
{
    private static byte[] Table(string[] strings, int trailing = 0, uint version = VanBurenStringTable.Version)
    {
        var directory = VanBurenStringTable.HeaderLength + strings.Length * VanBurenStringTable.RecordLength;
        var body = strings.Sum(s => s.Length);
        var b = new byte[directory + body + trailing];

        BinaryPrimitives.WriteUInt32LittleEndian(b, version);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)strings.Length);

        var cursor = directory;
        for (var i = 0; i < strings.Length; i++)
        {
            var at = VanBurenStringTable.HeaderLength + i * VanBurenStringTable.RecordLength;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)cursor);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), (uint)strings[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), (uint)b.Length); // the file length, repeated
            Encoding.ASCII.GetBytes(strings[i]).CopyTo(b.AsSpan(cursor));
            cursor += strings[i].Length;
        }

        return b;
    }

    [Fact]
    public void Parse_ReadsEveryStringInDirectoryOrder()
    {
        var table = VanBurenStringTable.Parse(Table(["Single Player", "Multiplayer", "New Game"]), "English.stf");

        Assert.Equal(3, table.Count);
        Assert.Equal(["Single Player", "Multiplayer", "New Game"], table.Strings);
    }

    [Fact]
    public void Parse_PutsTheFirstStringExactlyWhereTheDirectoryEnds()
    {
        // ⚑ 12 + 16 * count, which on the shipped table is 52,508 — and that IS the first offset.
        // The relation is what fixes the record size at 16 with nothing inferred.
        var table = VanBurenStringTable.Parse(Table(["A", "BB"]), "T.stf");

        Assert.Equal(["A", "BB"], table.Strings);
    }

    [Fact]
    public void Parse_RejectsAGapBetweenStrings()
    {
        var b = Table(["one", "two"]);
        var second = VanBurenStringTable.HeaderLength + VanBurenStringTable.RecordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(second), 9_999);

        Assert.Throws<InvalidDataException>(() => VanBurenStringTable.Parse(b, "BAD.stf"));
    }

    [Fact]
    public void Parse_RejectsTrailingBytesAfterTheLastString()
    {
        // The strings must end exactly at EOF — that is what makes the walk a proof rather than a
        // plausible reading.
        var error = Assert.Throws<InvalidDataException>(() => VanBurenStringTable.Parse(Table(["x"], 4), "BAD.stf"));
        Assert.Contains("strings end at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAWrongVersion()
    {
        Assert.Throws<InvalidDataException>(() => VanBurenStringTable.Parse(Table(["x"], version: 4), "BAD.stf"));
    }

    [Fact]
    public void Parse_ReadsAsciiNotUtf16()
    {
        // ⚠ Van Buren's text is ASCII; Brotherhood of Steel's string database is UTF-16. Reading
        // this one as UTF-16 would pair the letters up and yield CJK-looking nonsense.
        var table = VanBurenStringTable.Parse(Table(["Load Game"]), "T.stf");

        Assert.Equal("Load Game", table.Strings[0]);
    }

    [Fact]
    public void IsStringTable_AcceptsOnlyWhatTiles()
    {
        Assert.True(VanBurenStringTable.IsStringTable(Table(["x"])));
        Assert.False(VanBurenStringTable.IsStringTable(Table(["x"], 1)));
        Assert.False(VanBurenStringTable.IsStringTable("nope"u8.ToArray()));
    }
}