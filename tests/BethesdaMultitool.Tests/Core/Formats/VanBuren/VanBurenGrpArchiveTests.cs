using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.VanBuren;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Vectors for the cancelled Fallout 3 "Van Buren" prototype's <c>.grp</c> container, shaped
///     after the Dec 9 2003 build measured 2026-09-06: 24 archives, 7,044 entries, every one tiling
///     exactly.
///     <para>
///         There is no magic string — the two constant header dwords plus the exact tiling ARE the
///         probe. The six 12-byte empty archives the build ships are what pin the header length with
///         no inference: an archive with nothing in it is exactly its header.
///     </para>
/// </summary>
public sealed class VanBurenGrpArchiveTests
{
    /// <summary>Builds an archive whose payloads tile, unless <paramref name="gap" /> perturbs one.</summary>
    private static byte[] Archive(int gap = 0, byte[][]? payloads = null)
    {
        // null means "the default two"; an EMPTY array must stay empty, or the 12-byte case below
        // would silently test something else.
        payloads ??= [[.. "B3D "u8, 1, 2], [.. "RIFF"u8, 9]];
        var directory = VanBurenGrpArchive.HeaderLength + payloads.Length * VanBurenGrpArchive.EntryLength;
        var body = payloads.Sum(p => p.Length);
        var b = new byte[directory + body + gap];

        BinaryPrimitives.WriteUInt32LittleEndian(b, VanBurenGrpArchive.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), VanBurenGrpArchive.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)payloads.Length);

        var cursor = directory;
        for (var i = 0; i < payloads.Length; i++)
        {
            var at = VanBurenGrpArchive.HeaderLength + i * VanBurenGrpArchive.EntryLength;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)cursor);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), (uint)payloads[i].Length);
            payloads[i].CopyTo(b.AsSpan(cursor));
            cursor += payloads[i].Length;
        }

        return b;
    }

    [Fact]
    public void Parse_ReadsTheDirectoryAndTheTagsItsPayloadsCarry()
    {
        var archive = VanBurenGrpArchive.Parse(Archive(), "Items.grp");

        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal("B3D", archive.Entries[0].Tag);
        Assert.Equal("RIFF", archive.Entries[1].Tag);

        // The first payload begins exactly where the directory ends.
        Assert.Equal(VanBurenGrpArchive.HeaderLength + 2 * VanBurenGrpArchive.EntryLength, archive.Entries[0].Offset);
    }

    [Fact]
    public void Parse_AcceptsAnEmptyArchiveOfExactlyTwelveBytes()
    {
        // ⚑ Six of the build's 24 archives are exactly this — Templates, _CHM, _Dev, _ESF, _MSC and
        // _RLZ — and they are what fix the header length at 12 with nothing inferred.
        var empty = Archive(payloads: []);
        Assert.Equal(VanBurenGrpArchive.HeaderLength, empty.Length);

        var archive = VanBurenGrpArchive.Parse(empty, "_CHM.grp");
        Assert.Empty(archive.Entries);
    }

    [Fact]
    public void Parse_RejectsAGapBetweenPayloads()
    {
        // ⚠ The container has NO magic string, so the tiling is the only thing separating a real
        // archive from anything else that opens with 2 and 1. A trailing gap must fail.
        var error = Assert.Throws<InvalidDataException>(() => VanBurenGrpArchive.Parse(Archive(4), "BAD.grp"));
        Assert.Contains("entries end at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAnEntryThatDoesNotStartWhereTheLastOneEnded()
    {
        var b = Archive();
        var firstSize = VanBurenGrpArchive.HeaderLength + VanBurenGrpArchive.EntryLength + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(firstSize), 999);

        Assert.Throws<InvalidDataException>(() => VanBurenGrpArchive.Parse(b, "BAD.grp"));
    }

    [Fact]
    public void Parse_RejectsHeaderDwordsThatAreNotTheConstants()
    {
        var b = Archive();
        BinaryPrimitives.WriteUInt32LittleEndian(b, 3);

        Assert.Throws<InvalidDataException>(() => VanBurenGrpArchive.Parse(b, "BAD.grp"));
    }

    [Fact]
    public void Entry_NamesItselfByIndexAndTagBecauseTheContainerStoresNoNames()
    {
        // ⚠ Entries have an offset and a size and nothing else, so any name is ours. Index-plus-tag
        // keeps a listing readable and an extraction unambiguous.
        var archive = VanBurenGrpArchive.Parse(Archive(), "Items.grp");

        Assert.Equal("00000.B3D", archive.Entries[0].Name);
        Assert.Equal("00001.RIFF", archive.Entries[1].Name);
    }

    [Fact]
    public void Entry_FallsBackToBinWhenTheTagIsNotText()
    {
        var archive = VanBurenGrpArchive.Parse(Archive(payloads: [[0, 0, 2, 0, 7]]), "Engine.grp");

        Assert.Equal("00000.BIN", archive.Entries[0].Name);
    }

    [Fact]
    public void IsGrpArchive_AcceptsOnlyWhatTiles()
    {
        Assert.True(VanBurenGrpArchive.IsGrpArchive(Archive()));
        Assert.False(VanBurenGrpArchive.IsGrpArchive(Archive(1)));
        Assert.False(VanBurenGrpArchive.IsGrpArchive("not an archive"u8.ToArray()));
    }

    [Fact]
    public void Read_ReturnsThePayloadBytes()
    {
        var b = Archive(payloads: [[.. "B3D "u8, 42], [.. "GUI "u8]]);
        var archive = VanBurenGrpArchive.Parse(b, "Interface.grp");

        Assert.Equal([.. "B3D "u8, 42], VanBurenGrpArchive.Read(b, archive.Entries[0]));
        Assert.Equal("GUI "u8.ToArray(), VanBurenGrpArchive.Read(b, archive.Entries[1]));
    }
}