using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for the Fallout: Brotherhood of Steel <c>.CLP</c> container, shaped after the 227
///     shipped clumps measured 2026-09-07 — a 0x18-byte header, a hash table at
///     <c>pageCount × unit</c> whose CRC-32 sits at <c>+12</c>, and an unbroken page chain.
/// </summary>
public sealed class BosClumpFileTests
{
    private static readonly BosClumpFixture.Entry[] BarSections =
    [
        new(BosAssetHash.Compute("bar.hsh"), Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()),
        new(BosAssetHash.Compute("bar.tex"), new byte[2 * 4096 + 1]),
        new(BosAssetHash.Compute("bar.vat"), [1, 2, 3, 4, 5])
    ];

    /// <summary>
    ///     Twenty-two sections: 64 slots, of which the first entry's 61-step probe chain reaches 46,
    ///     leaving 18 the fixture can misplace into.
    ///     <para>
    ///         ⚠ This is NOT the smallest workable count, though an earlier note here claimed it
    ///         was. That count was taken over the off-chain slots still FREE once every entry has
    ///         been placed, but the fixture misplaces entry 0 into a table that is still EMPTY, so
    ///         all 18 are available to it. Re-measured over the counts 3..31: 3-5 sections take 8
    ///         slots and 6-10 take 16, and at both sizes the chain covers EVERY slot, so no
    ///         placement can be off it; 11 sections take 32 slots with 29 on the chain and 3 off
    ///         it, and 11 is the minimum. Both ends are pinned below by
    ///         <see cref="OnProbeChain_CannotMisplaceASlotBelowElevenSections" /> and
    ///         <see cref="OnProbeChain_ElevenSectionsIsTheSmallestTableThatHoldsAMisplacedSlot" />.
    ///     </para>
    /// </summary>
    private static readonly BosClumpFixture.Entry[] ManySections = GeneratedSections(22);

    [Fact]
    public void Crc32_IsTheZlibCrc32TheLoaderBuildsAtRunTime()
    {
        // 0x0013F1F8 builds the reflected 0xEDB88320 table and 0x0013F280 runs init −1 / final ~.
        // The check value for "123456789" is the published one; the 160-zero value is zlib.crc32.
        Assert.Equal(0xCBF43926u, BosClumpFile.Crc32("123456789"u8));
        Assert.Equal(0xAA075363u, BosClumpFile.Crc32(new byte[160]));
        Assert.Equal(0u, BosClumpFile.Crc32(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(4096, 0x28FFF24Bu)]
    [InlineData(256, 0xF8CCC74Eu)]
    public void Fixture_WritesTheZlibCrcOfItsTableAtPlusTwelve(int unit, uint expected)
    {
        // ⚑ The fixture builds its table with the code under test, so this pins its +12 to
        // Python's zlib.crc32 over the same 160 bytes (bar.hsh at slot 2 page 1, bar.tex at slot 0
        // page 2, bar.vat rehashed to slot 6 at page 5 / 35). A wrong slot count, placement or
        // CRC routine would make the fixture and the parser agree with each other and not with this.
        var b = BosClumpFixture.Build(unit, BarSections);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)));
        Assert.Equal(6, BosClumpFile.Parse(b, "BAR_T.CLP").Sections.Single(s => s.Tag == 0x8DE8E41Au).Slot);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(256)]
    public void Parse_SettlesThePageUnitByTheTableCrc(int unit)
    {
        // ⚑ The same slot layout at either unit: only the CRC at +12 says which, and it is
        // computed over the TABLE, so the two units cannot both match.
        var clump = BosClumpFile.Parse(BosClumpFixture.Build(unit, BarSections), "BAR_T.CLP");

        Assert.Equal(unit, clump.PageLength);
        Assert.Equal(unit == 4096, clump.IsStreamed);
        Assert.Equal(3, clump.DeclaredCount);
        Assert.Equal(8, clump.SlotCount);

        // bar.hsh: 100 bytes = 1 page; bar.tex: 2 pages + 1 byte = 3 pages at 4096 (33 at 256); bar.vat: 1 page.
        var texPages = (2 * 4096 + 1 + unit - 1) / unit;
        Assert.Equal([1, 2, 2 + texPages], clump.Sections.Select(s => s.StartPage));
        Assert.Equal([unit, 2 * unit, (2 + texPages) * unit], clump.Sections.Select(s => s.Offset));
        Assert.Equal([100, 2 * 4096 + 1, 5], clump.Sections.Select(s => s.Size));
        Assert.Equal(3 + texPages, clump.PageCount);
        Assert.Equal(clump.PageCount * unit, clump.TableOffset);
        Assert.Equal(0, clump.Misplaced);
    }

    [Fact]
    public void Parse_RefusesTheRefutedReadingOfPlusTwelveAsAPathHash()
    {
        // ⛔ "+12 is a hash of the clump's own path" fitted nothing on 227/227; the fixture writes
        // exactly that and the parse must say so.
        var error = Assert.Throws<InvalidDataException>(() =>
            BosClumpFile.Parse(BosClumpFixture.Build(4096, BarSections, crcOfPathInstead: true), "BAD.CLP"));
        Assert.Contains("CRC-32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesATableWhoseBytesChangedUnderTheCrc()
    {
        var b = BosClumpFixture.Build(256, BarSections);
        var good = BosClumpFile.Parse(b, "OK.CLP");
        b[good.TableOffset + 13] ^= 0x01; // one size byte of slot 0

        var error = Assert.Throws<InvalidDataException>(() => BosClumpFile.Parse(b, "BAD.CLP"));
        Assert.Contains("CRC-32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OrdersSectionsByStartPageNotBySlot()
    {
        // ⚑ Field 2 is the START PAGE. An earlier reading called it a "kind" and fitted 50 of 50
        // retail files while still being wrong. Slots are hash-placed (bar.tex lands on slot 0
        // because 0x8DE8D8C8 & 7 == 0) while file order is hsh, tex, vat.
        var clump = BosClumpFile.Parse(BosClumpFixture.Build(4096, BarSections), "BAR_T.CLP");

        Assert.Equal([0, 1, 2], clump.Sections.Select(s => s.Index));
        Assert.True(clump.Sections.Zip(clump.Sections.Skip(1)).All(p => p.First.StartPage < p.Second.StartPage));
        Assert.Equal(0, clump.Sections.Single(s => s.Tag == 0x8DE8D8C8u).Slot);
        Assert.Equal(1, clump.Sections.Single(s => s.Tag == 0x8DE8D8C8u).Index);
    }

    [Fact]
    public void TryFind_ResolvesTheKeyTheEngineHashes()
    {
        var b = BosClumpFixture.Build(4096, BarSections);
        var clump = BosClumpFile.Parse(b, "BAR_T.CLP");

        Assert.True(clump.TryFind("bar.tex", out var tex));
        Assert.Equal(0x8DE8D8C8u, tex.Tag);
        Assert.Equal(2 * 4096, tex.Offset);
        Assert.True(clump.TryFind(0x8DE8AC12u, out var hsh));
        Assert.Equal(1, hsh.StartPage);
        Assert.Equal([0, 1, 2, 3, 4], BosClumpFile.Read(b, hsh)[..5].ToArray());
        Assert.False(clump.TryFind("nothing.tex", out _));
    }

    [Fact]
    public void Parse_RequiresTheChainToEndExactlyAtThePageCount()
    {
        // One spare page at the end is enough to refuse — the sections must consume the file.
        var error = Assert.Throws<InvalidDataException>(() =>
            BosClumpFile.Parse(BosClumpFixture.Build(4096, BarSections, pageBias: 1), "BAD.CLP"));
        Assert.Contains("end at page", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RequiresTheDeclaredCountToMatchThePopulatedSlots()
    {
        // +16 is outside the CRC, so the count gate fires on its own — as long as the slot count
        // it implies stays 8 (4 entries → 1 << bitlen(6) = 8, like 3). A count that changes the
        // slot count moves the table's extent and the CRC gate fires first instead.
        var error = Assert.Throws<InvalidDataException>(() =>
            BosClumpFile.Parse(BosClumpFixture.Build(256, BarSections, declaredCount: 4), "BAD.CLP"));
        Assert.Contains("declares 4", error.Message, StringComparison.Ordinal);

        var moved = Assert.Throws<InvalidDataException>(() =>
            BosClumpFile.Parse(BosClumpFixture.Build(256, BarSections, declaredCount: 2), "BAD.CLP"));
        Assert.Contains("CRC-32", moved.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RequiresThePopulatedSlotsToCarryZeroRuntimeFields()
    {
        // Slot +8 is the runtime cache pointer and is zero in every one of the 21,092 populated
        // slots on disc; the fixture recomputes the CRC over the mutated table so only this gate fires.
        var b = BosClumpFixture.Build(256, BarSections, mutateTable: table =>
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(8), 0xDEADBEEF)); // slot 0 = bar.tex

        var error = Assert.Throws<InvalidDataException>(() => BosClumpFile.Parse(b, "BAD.CLP"));
        Assert.Contains("well-formed", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Sections keyed <c>bar0.tex</c>, <c>bar1.tex</c>, … with one page of payload each.</summary>
    private static BosClumpFixture.Entry[] GeneratedSections(int count)
    {
        return
        [
            .. Enumerable.Range(0, count).Select(i =>
                new BosClumpFixture.Entry(BosAssetHash.Compute($"bar{i}.tex"), new byte[i + 1]))
        ];
    }

    [Fact]
    public void Parse_ReportsAMisplacedSlotWithoutRefusingTheFile()
    {
        var clump = BosClumpFile.Parse(BosClumpFixture.Build(256, ManySections, misplaceFirst: true), "ODD.CLP");

        Assert.Equal(1, clump.Misplaced);
        Assert.Equal(22, clump.Sections.Count);
        Assert.Equal(64, clump.SlotCount);

        // The other 21 are on their chains, and the file is still fully readable.
        Assert.Equal(0, BosClumpFile.Parse(BosClumpFixture.Build(256, ManySections), "OK.CLP").Misplaced);
        Assert.Equal(Enumerable.Range(0, 22), clump.Sections.Select(s => s.Index));
    }

    [Theory]
    [InlineData(3, 8)]
    [InlineData(5, 8)]
    [InlineData(8, 16)]
    [InlineData(10, 16)]
    public void OnProbeChain_CannotMisplaceASlotBelowElevenSections(int sections, int slots)
    {
        // ⚠ The engine rehashes at most 60 times, and on an 8- or a 16-slot table that 61-step
        // chain visits EVERY slot, so a "misplaced" slot cannot exist and the fixture — not the
        // parser — refuses. Recorded because the earlier three-section version of the test above
        // threw out of the fixture and was read as a parser result.
        Assert.Equal(slots, BosAssetHash.SlotCountFor(sections));

        var error = Assert.Throws<InvalidOperationException>(() =>
            BosClumpFixture.Build(256, GeneratedSections(sections), misplaceFirst: true));
        Assert.Contains("off the probe chain", error.Message, StringComparison.Ordinal);

        // The three real BAR keys are an 8-slot table too, and behave the same way.
        Assert.Throws<InvalidOperationException>(() => BosClumpFixture.Build(256, BarSections, misplaceFirst: true));
    }

    [Fact]
    public void OnProbeChain_ElevenSectionsIsTheSmallestTableThatHoldsAMisplacedSlot()
    {
        // The other side of the boundary: 11 sections take 32 slots, the first key's chain reaches
        // 29 of them, and the fixture misplaces into one of the 3 it never touches. Anything
        // smaller is refused by the theory above, so 11 is the measured minimum — the 22 the
        // misplaced-slot test uses is workable but not minimal.
        Assert.Equal(32, BosAssetHash.SlotCountFor(11));

        var clump = BosClumpFile.Parse(BosClumpFixture.Build(256, GeneratedSections(11), misplaceFirst: true),
            "MIN.CLP");

        Assert.Equal(1, clump.Misplaced);
        Assert.Equal(32, clump.SlotCount);
        Assert.Equal(11, clump.Sections.Count);
        Assert.Equal(0, BosClumpFile.Parse(BosClumpFixture.Build(256, GeneratedSections(11)), "OK.CLP").Misplaced);
    }

    [Fact]
    public void Parse_AcceptsTheExtraPaddingSectorElevenSoundBanksCarry()
    {
        // 11 of the 55 _S banks end one 2048-byte block later than the table needs; padding is
        // not part of the layout and must not be counted against it.
        var clump = BosClumpFile.Parse(BosClumpFixture.Build(256, BarSections, extraSectors: 1), "X_S.CLP");
        Assert.Equal(256, clump.PageLength);
    }

    [Fact]
    public void ReadStrings_StopsWhereTheAuthoredPathsEnd()
    {
        // ⚠⚠ There is NO empty-string terminator. BAR_T.CLP follows its real paths with the
        // non-printable run 9B 8C 9D 8E 9F and then a UTF-16 "KERNEL32.dll", which a naive ASCII
        // walk reports as eleven single-letter strings K, E, R, N, E, L, 3, 2, ., d, l, l.
        var b = BosClumpFixture.Build(4096, BarSections, "c1/BAR/BAR.clp");
        var after = BosClumpFile.StringTableOffset + "c1/BAR/BAR.clp".Length + 1;
        new byte[] { 0x9B, 0x8C, 0x9D, 0x8E, 0x9F }.CopyTo(b.AsSpan(after));
        Encoding.Unicode.GetBytes("KERNEL32.dll").CopyTo(b.AsSpan(after + 5));

        var clump = BosClumpFile.Parse(b, "BAR_T.CLP");

        Assert.Equal(["c1/BAR/BAR.clp"], clump.Strings);
    }

    [Fact]
    public void IsClump_RequiresTheMagicAndACrcMatchingTable()
    {
        Assert.True(BosClumpFile.IsClump(BosClumpFixture.Build(4096, BarSections)));
        Assert.True(BosClumpFile.IsClump(BosClumpFixture.Build(256, BarSections)));

        // ⛔ The old "length == pages × 4096 + 2048" shape with a zero table: no CRC, no clump.
        var oldShape = new byte[4096 + 2048];
        BinaryPrimitives.WriteUInt32LittleEndian(oldShape, BosClumpFile.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(oldShape.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(oldShape.AsSpan(16), 1);
        Assert.False(BosClumpFile.IsClump(oldShape));

        Assert.False(BosClumpFile.IsClump(new byte[BosClumpFile.HeaderLength]));
        Assert.False(BosClumpFile.IsClump(BosClumpFixture.Build(4096, BarSections, crcOfPathInstead: true)));
    }
}