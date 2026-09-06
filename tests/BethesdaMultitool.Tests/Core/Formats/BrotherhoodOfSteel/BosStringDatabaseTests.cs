using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for Fallout: Brotherhood of Steel's <c>.SDB</c> string database, shaped after the
///     shipped <c>DATA/C1/BAR/BAR.SDB</c> measured 2026-09-06: magic 1499, 85 strings, 8-byte hash
///     slots, UTF-16LE text.
/// </summary>
public sealed class BosStringDatabaseTests
{
    /// <summary>
    ///     Builds a database: strings from +12, then a hash table of 8-byte slots, padded with
    ///     <paramref name="emptySlots" /> zeroed entries. <paramref name="declared" /> overrides the
    ///     header's string count so a disagreeing count can be tested.
    /// </summary>
    private static byte[] Database((uint Hash, string Value)[] entries, int emptySlots = 2, uint? declared = null)
    {
        var body = new MemoryStream();
        body.Write(new byte[BosStringDatabase.HeaderLength]);
        var offsets = new int[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            offsets[i] = (int)body.Length;
            body.Write(Encoding.Unicode.GetBytes(entries[i].Value));
            body.Write([0, 0]);
        }

        var tableOffset = (int)body.Length;
        var slots = entries.Length + emptySlots;
        var table = new byte[slots * BosStringDatabase.SlotLength];
        for (var i = 0; i < entries.Length; i++)
        {
            // Leave a gap so slot indices are not simply 0..n-1, as the real table does.
            var slot = i * 2 < slots ? i * 2 : i;
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(slot * BosStringDatabase.SlotLength), entries[i].Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan((slot * BosStringDatabase.SlotLength) + 4), (uint)offsets[i]);
        }

        body.Write(table);
        var bytes = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, BosStringDatabase.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)tableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), declared ?? (uint)entries.Length);
        return bytes;
    }

    [Fact]
    public void Parse_ReadsTheHeaderAndEveryPopulatedSlot()
    {
        var db = BosStringDatabase.Parse(
            Database([(0x8E972E05, "Sleeping Man"), (0x57632888, "Freezer Chest")]), "BAR.SDB");

        Assert.Equal(2, db.Entries.Count);
        Assert.Equal("Sleeping Man", db.Entries[0].Value);
        Assert.Equal(0x57632888u, db.Entries[1].Hash);
        Assert.Equal(0, db.UnresolvedSlots);
    }

    [Fact]
    public void Parse_ReadsUtf16NotAscii()
    {
        // ⚠ The strings are UTF-16LE. Read as ASCII, every one comes back with NUL between the
        // letters — which still "looks like" a string and would pass a laxer check.
        var db = BosStringDatabase.Parse(Database([(1, "Wasteland Man")]), "T.SDB");

        Assert.Equal("Wasteland Man", db.Entries[0].Value);
    }

    [Fact]
    public void Parse_SkipsZeroedSlotsWithoutCountingThem()
    {
        // The real table is sparse: 86 non-empty slots in a much larger table.
        var db = BosStringDatabase.Parse(Database([(7, "one"), (9, "two")], emptySlots: 40), "Sparse.SDB");

        Assert.Equal(2, db.Entries.Count);
        Assert.All(db.Entries, e => Assert.NotEqual(0u, e.Hash));
    }

    [Fact]
    public void Parse_StopsAtTheTerminatorRatherThanReadingTrailingBytesAsSlots()
    {
        // ⚠ 34 of the 56 shipped databases put a terminator slot after the table and carry unrelated
        // data beyond it. Walking to EOF regardless looked right on 51 of them and invented exactly
        // ONE extra entry in the other five — enough to break the count, not enough to look wrong.
        // Here the trailing bytes would resolve to a real string if the walk did not stop.
        var db = Database([(1, "kept")], emptySlots: 0);
        var trailing = new byte[BosStringDatabase.SlotLength * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(trailing, BosStringDatabase.TerminatorHash);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing.AsSpan(4), BosStringDatabase.TerminatorOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing.AsSpan(8), 0x1234);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing.AsSpan(12), BosStringDatabase.HeaderLength);

        var parsed = BosStringDatabase.Parse([.. db, .. trailing], "Terminated.SDB");

        Assert.Single(parsed.Entries);
        Assert.Equal("kept", parsed.Entries[0].Value);
        Assert.Equal(0, parsed.UnresolvedSlots);
    }

    [Fact]
    public void Parse_RejectsAFileWhoseResolvedCountContradictsTheHeader()
    {
        // ⚑ The count IS the proof that the 8-byte stride is right — a 16-byte stride resolves only
        // 40 of the shipped file's 85. So a mismatch must fail rather than be tolerated.
        var bytes = Database([(1, "a"), (2, "b")], declared: 5);

        var error = Assert.Throws<InvalidDataException>(() => BosStringDatabase.Parse(bytes, "BAD.SDB"));
        Assert.Contains("header declares 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAWrongMagicAndABadTableOffset()
    {
        var wrongMagic = Database([(1, "a")]);
        BinaryPrimitives.WriteUInt32LittleEndian(wrongMagic, 1500);
        Assert.Throws<InvalidDataException>(() => BosStringDatabase.Parse(wrongMagic, "BAD.SDB"));

        var badTable = Database([(1, "a")]);
        BinaryPrimitives.WriteUInt32LittleEndian(badTable.AsSpan(4), 99_999);
        Assert.Throws<InvalidDataException>(() => BosStringDatabase.Parse(badTable, "BAD.SDB"));
    }

    [Fact]
    public void Find_ResolvesByHash()
    {
        var db = BosStringDatabase.Parse(Database([(0xDEAD, "Bar Patron"), (0xBEEF, "Prostitute")]), "T.SDB");

        Assert.Equal("Prostitute", db.Find(0xBEEF));
        Assert.Null(db.Find(0x1234));
    }

    [Fact]
    public void IsStringDatabase_ChecksTheMagic()
    {
        Assert.True(BosStringDatabase.IsStringDatabase(Database([(1, "x")])));
        Assert.False(BosStringDatabase.IsStringDatabase("nope"u8.ToArray()));
    }
}
