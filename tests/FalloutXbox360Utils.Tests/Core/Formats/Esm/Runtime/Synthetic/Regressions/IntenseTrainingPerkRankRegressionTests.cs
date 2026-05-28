using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;
using static FalloutXbox360Utils.Tests.Helpers.SyntheticStructFactory;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Synthetic.Regressions;

/// <summary>
///     Regression guard for the IntenseTraining PERK rank-parsing bug. A prior
///     reader implementation read the BGSPerkEntry's <c>rank</c> field at the
///     wrong offset within the entry struct, producing garbage values like
///     <c>130</c> (uninit heap fill) instead of the actual rank.
///     <para>
///         The captured contract: a BGSPerkEntry with <c>rank=0</c> at the
///         correct offset (+4 inside the 12-byte entry struct, after the
///         4-byte vtable) must be read as <c>Rank=0</c>, not <c>130</c> or
///         any other heap garbage.
///     </para>
///     <para>
///         Synthetic input: a single-entry PERK list with rank=0, priority=0,
///         no data pointer. Captures the failure mode of the original bug
///         (reading the wrong offset would surface adjacent garbage) without
///         needing real DMP bytes.
///     </para>
/// </summary>
public sealed class IntenseTrainingPerkRankRegressionTests
{
    private const byte PerkFormType = 0x56;
    private const int PerkStructSize = 96;

    // PDB offsets for BGSPerk class (per pdb_layouts.json key 0x56).
    private const int PerkDataOffset = 72;          // 5-byte PerkData
    private const int PerkEntriesListOffset = 88;   // BSSimpleList head (8 bytes)

    // BGSPerkEntry inner offsets — read in RuntimeMagicReader.ReadPerkEntry.
    private const int PerkEntryStructSize = 12;
    private const int PerkEntryRankOffset = 4;
    private const int PerkEntryPriorityOffset = 5;
    private const int PerkEntryDataPtrOffset = 8;

    private const uint PerkVa = 0x40100000;
    private const uint EntryVa = 0x40200000;

    [Fact]
    public void ReadRuntimePerk_ReadsEntryRankFromOffsetPlus4_NotGarbage()
    {
        // The IntenseTraining captured behaviour: at least one entry with rank=0.
        // Synthetic equivalent: build a PERK with a single-entry list where the
        // entry has rank=0 at offset +4 within its struct. If the reader
        // regresses to reading the wrong offset, it'll surface either 0
        // (vtable bytes are zero in the synthetic fixture, coincidentally
        // masking the bug) or some other value. We assert exact rank=0 AND
        // assert no entry has rank=130 (the historical garbage value), which
        // catches any drift to a reasonable-looking-but-wrong offset.
        const uint perkFormId = 0x000A1234; // synthetic FormID
        var perkBuffer = BuildPerk(perkFormId, entriesHeadItemVa: EntryVa);
        var entryBuffer = BuildPerkEntry(rank: 0, priority: 0, dataPtr: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(perkBuffer, PerkVa)
            .WithPointerTarget(EntryVa, entryBuffer);
        var reader = new RuntimeMagicReader(fixture.BuildContext());

        var perk = reader.ReadRuntimePerk(
            fixture.MakeEntry(perkFormId, PerkFormType, PerkVa));

        Assert.NotNull(perk);
        Assert.NotEmpty(perk.Entries);
        Assert.Equal((byte)0, perk.Entries[0].Rank);
        Assert.DoesNotContain(perk.Entries, e => e.Rank == 130);
    }

    [Fact]
    public void ReadRuntimePerk_ReadsExactRankValueAtCorrectOffset()
    {
        // Stronger contract: write a non-zero sentinel rank, assert the reader
        // pulls THAT EXACT value. Catches drift to an offset that happens to
        // contain zero in the synthetic fixture (which would mask the bug if
        // we only asserted rank=0).
        const uint perkFormId = 0x000A1235;
        const byte expectedRank = 7;
        const byte expectedPriority = 42;

        var perkBuffer = BuildPerk(perkFormId, entriesHeadItemVa: EntryVa);
        var entryBuffer = BuildPerkEntry(rank: expectedRank, priority: expectedPriority, dataPtr: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(perkBuffer, PerkVa)
            .WithPointerTarget(EntryVa, entryBuffer);
        var reader = new RuntimeMagicReader(fixture.BuildContext());

        var perk = reader.ReadRuntimePerk(
            fixture.MakeEntry(perkFormId, PerkFormType, PerkVa));

        Assert.NotNull(perk);
        Assert.Single(perk.Entries);
        Assert.Equal(expectedRank, perk.Entries[0].Rank);
        Assert.Equal(expectedPriority, perk.Entries[0].Priority);
    }

    [Fact]
    public void ReadRuntimePerk_EmptyEntriesList_ReturnsEmptyEntries()
    {
        // Null head + null next = empty list. Reader returns a PERK record with
        // empty Entries (not null), and doesn't fabricate garbage from buffer
        // tail bytes.
        const uint perkFormId = 0x000A1236;
        var perkBuffer = BuildPerk(perkFormId, entriesHeadItemVa: 0);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(perkBuffer, PerkVa);
        var reader = new RuntimeMagicReader(fixture.BuildContext());

        var perk = reader.ReadRuntimePerk(
            fixture.MakeEntry(perkFormId, PerkFormType, PerkVa));

        Assert.NotNull(perk);
        Assert.Empty(perk.Entries);
    }

    private static byte[] BuildPerk(uint formId, uint entriesHeadItemVa)
    {
        var buf = new byte[PerkStructSize];
        WriteFormHeader(buf, 0, PerkFormType, formId);

        // PerkData (5 bytes at +72): trait, minLevel, ranks, playable, +1 pad — left zero.

        // PerkEntries BSSimpleList head at +88: m_item + m_pkNext.
        WriteUInt32BE(buf, PerkEntriesListOffset, entriesHeadItemVa);
        WriteUInt32BE(buf, PerkEntriesListOffset + 4, 0); // m_pkNext = 0 (no chain)
        return buf;
    }

    /// <summary>
    ///     Builds a 12-byte BGSPerkEntry: vtable(+0..+3, ignored), rank(+4),
    ///     priority(+5), pad(+6..+7), dataPtr(+8..+11).
    /// </summary>
    private static byte[] BuildPerkEntry(byte rank, byte priority, uint dataPtr)
    {
        var buf = new byte[PerkEntryStructSize];
        // vtable @ +0 left zero (reader doesn't read it).
        buf[PerkEntryRankOffset] = rank;
        buf[PerkEntryPriorityOffset] = priority;
        // pad @ +6, +7 left zero.
        WriteUInt32BE(buf, PerkEntryDataPtrOffset, dataPtr);
        return buf;
    }
}
