using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for <see cref="BosNfoFile" />, the build tool's plain-text dump of a
///     <see cref="BosStringDatabase" />. The fixture below reproduces the shipped layout exactly,
///     including the mixed line endings a retail dump really has (LF for the header, CRLF at the
///     end) — that is not decoration, it is the trap that puts a stray <c>\r</c> on the last
///     string.
/// </summary>
public sealed class BosNfoFileTests
{
    private const string Fixture =
        "SDB Information for c1/BAR/BAR.sdb\n" +
        "\n" +
        "===========================================================================\n" +
        "Header Information:\n" +
        "===========================================================================\n" +
        "hashtableoffset:\t2744\n" +
        "          magic:\t1499\n" +
        "     numstrings:\t4\n" +
        "  hashtablesize:\t128\n" +
        "\n" +
        "===========================================================================\n" +
        "String Entries:\n" +
        "===========================================================================\n" +
        "00:\n" +
        "01:\thash: 0x8EAE3281\toffset: 0x606 1542\tgibchunks_human_heavy\n" +
        "02:\n" +
        "05:\thash: 0x8E972E05\toffset: 0x030 0048\tamb_dinner_sleep1\n" +
        "08:\thash: 0x57632888\toffset: 0x5EA 1514\tFreezer Chest\r\n" +
        "27:\thash: 0x6AB79C1B\toffset: 0x8A0 2208\tspit\r\n";

    [Fact]
    public void ReadsTheHeaderBlockTheDatabaseTakesItsFieldNamesFrom()
    {
        var nfo = BosNfoFile.Parse(Fixture, "BAR.nfo");

        Assert.Equal("c1/BAR/BAR.sdb", nfo.Subject);
        Assert.Equal(2744, nfo.HashTableOffset);
        Assert.Equal(BosStringDatabase.Magic, nfo.Magic);
        Assert.Equal(4, nfo.DeclaredCount);
        Assert.Equal(128, nfo.HashTableSize);
        Assert.Equal(4, nfo.Entries.Count);
    }

    [Fact]
    public void KeepsTheSlotNumbersTheDumpPrintsRatherThanRenumbering()
    {
        var nfo = BosNfoFile.Parse(Fixture, "BAR.nfo");

        Assert.Equal([1, 5, 8, 27], [.. nfo.Entries.Select(e => e.Slot)]);
        Assert.Equal(0x606, nfo.Entries[0].Offset);
    }

    [Fact]
    public void SeparatesAProvenNameFromADisplayStringFiledUnderIt()
    {
        // ⚑ This is the whole point of the file. Three of the four strings hash to the key printed
        // beside them; "Freezer Chest" does not — it is the UI text of the record whose internal
        // name owns 0x57632888.
        var nfo = BosNfoFile.Parse(Fixture, "BAR.nfo");

        Assert.Equal(3, nfo.NameCount);
        Assert.True(nfo.Entries[0].IsName);
        Assert.True(nfo.Entries[1].IsName);
        Assert.False(nfo.Entries[2].IsName);
        Assert.True(nfo.Entries[3].IsName);
    }

    [Fact]
    public void TrimsTheCarriageReturnRatherThanLeavingItInTheString()
    {
        // ⚠ A retail dump switches from LF to CRLF part-way through, so a naive split on '\n'
        // leaves "spit\r" — which then hashes to something else and looks like a hash failure.
        var nfo = BosNfoFile.Parse(Fixture, "BAR.nfo");

        Assert.Equal("spit", nfo.Entries[3].Value);
        Assert.Equal("Freezer Chest", nfo.Entries[2].Value);
        Assert.Equal(0x6AB79C1BU, BosNameHash.Compute(nfo.Entries[3].Value));
    }

    [Fact]
    public void RefusesADumpWhoseLineCountDisagreesWithNumstrings()
    {
        // The count is the self-check; it holds on 55 of 55 shipped dumps, so a disagreement means
        // the walk skipped or invented a line.
        var broken = Fixture.Replace("numstrings:\t4", "numstrings:\t5", StringComparison.Ordinal);

        Assert.False(BosNfoFile.TryParse(broken, "BAR.nfo", out _, out var error));
        Assert.Contains("declares 5", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTextThatIsNotADump()
    {
        Assert.False(BosNfoFile.IsNfo("hashtableoffset:\t2744\n"));
        Assert.False(BosNfoFile.TryParse("hashtableoffset:\t2744\n", "x", out _, out var error));
        Assert.Contains("SDB Information for", error, StringComparison.Ordinal);
    }
}