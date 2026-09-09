using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for the Fallout: Brotherhood of Steel <c>_S.CLP</c> sound bank, shaped after the 55
///     shipped banks measured 2026-09-07 — a resident clump whose entries are <c>.vag</c> files,
///     each carrying its own sample rate.
/// </summary>
public sealed class BosSoundBankTests
{
    private static readonly Spec[] Mixed =
    [
        new("BF_Dirt_Large_1", 18000, 10),
        new("FS_Linoleum_1", 16000, 6),
        new("wstlanddog_attk1", 22050, 20)
    ];

    /// <summary>
    ///     Builds a bank the way the disc lays one out: a 256-byte header page, each sound a
    ///     <c>VAGp</c> header plus blocks padded to 256, then the hash table at <c>+8 × 256</c>
    ///     with every slot placed by the engine's probe chain and its CRC-32 at <c>+12</c>,
    ///     zero-filled to 2048.
    /// </summary>
    internal static byte[] Bank(params Spec[] specs)
    {
        var entries = new List<BosClumpFixture.Entry>();
        foreach (var spec in specs)
        {
            var body = spec.Blocks * BosSoundBank.BlockLength;
            var vag = new byte[BosSoundBank.VagHeaderLength + body];
            BinaryPrimitives.WriteUInt32BigEndian(vag, BosSoundBank.VagMagic);
            BinaryPrimitives.WriteUInt32BigEndian(vag.AsSpan(4), 4);
            BinaryPrimitives.WriteInt32BigEndian(vag.AsSpan(0xC), body);
            BinaryPrimitives.WriteInt32BigEndian(vag.AsSpan(0x10), spec.Rate);
            Encoding.ASCII.GetBytes(spec.Name).AsSpan(0, Math.Min(16, spec.Name.Length)).CopyTo(vag.AsSpan(0x20));
            for (var i = 1; i < spec.Blocks; i++)
            {
                var o = BosSoundBank.VagHeaderLength + i * BosSoundBank.BlockLength;
                vag[o] = 0x25; // shift 5, filter 2
                if (i == spec.Blocks - 2)
                {
                    vag[o + 1] = BosSoundBank.LoopEndFlag;
                }
                else if (i == spec.Blocks - 1)
                {
                    vag[o + 1] = BosSoundBank.EndOfStreamFlag;
                }
                for (var j = 2; j < BosSoundBank.BlockLength; j++)
                {
                    vag[o + j] = (byte)(0x11 * ((i + j) % 7));
                }
            }

            entries.Add(new BosClumpFixture.Entry(BosAssetHash.Compute("/Final_Assets/sound/" + spec.Name + ".vag"),
                vag));
        }

        return BosClumpFixture.Build(BosClumpFile.ResidentPageLength, entries, "c1/BAR/BAR_S.clp");
    }

    [Fact]
    public void Parse_ReadsEverySoundsOwnRateFromItsHeader()
    {
        // ⚑ The rate is per sound, in the VAGp header's big-endian +0x10, and the shipped banks mix
        // them freely — so a single constant could not have been right. Pinned literals, not a
        // round trip through the code under test.
        var bank = BosSoundBank.Parse(Bank(Mixed), "BAR_S.CLP");

        Assert.Equal(3, bank.DeclaredCount);
        Assert.Equal([18000, 16000, 22050], bank.Sounds.Select(s => s.SampleRate));
        Assert.Equal(["BF_Dirt_Large_1", "FS_Linoleum_1", "wstlanddog_attk1"], bank.Sounds.Select(s => s.Name));
        Assert.Equal([10, 6, 20], bank.Sounds.Select(s => s.BlockCount));
    }

    [Fact]
    public void Parse_LaysSoundsOutOnTwoHundredFiftySixBytePages()
    {
        var bank = BosSoundBank.Parse(Bank(Mixed), "BAR_S.CLP");

        // First header on page 1; body 0x30 past it; 48 + 160 bytes pads to one page.
        Assert.Equal(256, bank.Sounds[0].HeaderOffset);
        Assert.Equal(256 + 0x30, bank.Sounds[0].Offset);
        Assert.Equal(512, bank.Sounds[1].HeaderOffset);
        Assert.Equal(768, bank.Sounds[2].HeaderOffset);
    }

    [Theory]
    [InlineData(16000, 1365)]
    [InlineData(18000, 1536)]
    [InlineData(22050, 1881)]
    [InlineData(24000, 2048)]
    [InlineData(48000, 4096)]
    [InlineData(1000, 85)]
    public void PitchFor_IsTheEnginesShiftTwelveOverFortyEightThousand(int rate, int expectedPitch)
    {
        // 0x0019AA18: (rate << 12) / 48000, integer division — the SPU2 pitch register value.
        Assert.Equal(expectedPitch, BosSoundBank.PitchFor(rate));
    }

    [Fact]
    public void TryFind_ResolvesTheKeyTheEngineHashes()
    {
        var bank = BosSoundBank.Parse(Bank(Mixed), "BAR_S.CLP");

        Assert.True(bank.TryFind("/Final_Assets/sound/FS_Linoleum_1.vag", out var sound));
        Assert.Equal(16000, sound.SampleRate);
        Assert.False(bank.TryFind("/Final_Assets/sound/nothing.vag", out _));
    }

    [Fact]
    public void Parse_RejectsAStreamedClump()
    {
        // ⚠ Texture clumps carry the SAME CLMP magic. Their CRC settles on 4096-byte pages; a
        // bank's on 256. Without that a _T clump would parse as a bank of noise.
        var clump = BosClumpFixture.Build(
            BosClumpFile.StreamedPageLength,
            [new BosClumpFixture.Entry(BosAssetHash.Compute("bar.hsh"), new byte[64])],
            "c1/BAR/BAR_T.clp");

        var error = Assert.Throws<InvalidDataException>(() => BosSoundBank.Parse(clump, "BAR_T.CLP"));
        Assert.Contains("streamed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsACountThatDisagreesWithTheTable()
    {
        // +16 is the entry count and the loader compares it with the populated slots. It sits
        // OUTSIDE the CRC, so the count gate can fire on its own — but only while the count it
        // declares implies the same slot count (3 and 4 both give 1 << bitlen(n + n/2) == 8).
        var b = Bank(Mixed);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 4);

        var error = Assert.Throws<InvalidDataException>(() => BosSoundBank.Parse(b, "BAR_S.CLP"));
        Assert.Contains("declares 4", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsACountThatMovesTheTableByItsCrcInstead()
    {
        // ⚠ A count of 2 asks for 4 slots, so the table it describes is 80 bytes rather than 160
        // and the CRC at +12 no longer reproduces. This test used to expect the count message for
        // that mutation and could never have passed: the CRC gate runs first, by construction.
        var b = Bank(Mixed);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 2);

        var error = Assert.Throws<InvalidDataException>(() => BosSoundBank.Parse(b, "BAR_S.CLP"));
        Assert.Contains("CRC-32", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("declares", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAnEntryWithoutAVagHeader()
    {
        var b = Bank(Mixed);
        b[512] = 0; // break the second sound's 'V'

        var error = Assert.Throws<InvalidDataException>(() => BosSoundBank.Parse(b, "BAR_S.CLP"));
        Assert.Contains("no VAGp magic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAHeaderLengthThatDoesNotFillItsEntry()
    {
        var b = Bank(Mixed);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(256 + 0xC), 16);

        Assert.Throws<InvalidDataException>(() => BosSoundBank.Parse(b, "BAR_S.CLP"));
    }

    [Fact]
    public void Decode_ProducesTwentyEightSamplesPerAudibleBlock()
    {
        // ⚠ Ten blocks are STORED and nine carry audio: the last is the SPU2 end marker, which is
        // not sound. 9 x 28 = 252, pinned as a literal rather than read back off the record.
        var b = Bank(new Spec("one", 16000, 10));
        var bank = BosSoundBank.Parse(b, "T.CLP");
        var pcm = BosSoundBank.Decode(b, bank.Sounds[0]);

        Assert.Equal(10, bank.Sounds[0].BlockCount);
        Assert.True(bank.Sounds[0].HasEndBlock);
        Assert.Equal(252, pcm.Length);
        Assert.Contains(pcm, s => s != 0);
    }

    [Fact]
    public void Decode_StopsAtTheEndBlockInsteadOfEmittingItsDummyPayload()
    {
        // ⚑ THE SHIPPED END BLOCK IS `00 07 77 … 77` on 3,423 of the disc's 3,430 sounds, and the
        // dummy nibble 7 at shift 0 with no filter decodes to a CONSTANT +28,672 — 87% of full
        // scale, a click on the tail of every export. This test can fail: clearing the end flag
        // (the counterfactual below) makes the same bytes produce exactly that 28-sample run.
        var b = Bank(new Spec("one", 16000, 10));
        var last = 256 + BosSoundBank.VagHeaderLength + 9 * BosSoundBank.BlockLength;
        b[last] = 0x00;
        b[last + 1] = BosSoundBank.EndOfStreamFlag;
        for (var j = 2; j < BosSoundBank.BlockLength; j++)
        {
            b[last + j] = 0x77;
        }

        var bank = BosSoundBank.Parse(b, "T.CLP");
        var pcm = BosSoundBank.Decode(b, bank.Sounds[0]);

        Assert.Equal(252, pcm.Length);
        Assert.DoesNotContain((short)28_672, pcm);

        // Counterfactual: the same bytes with the marker's flag cleared ARE decoded, and the tail
        // is the 28-sample DC run the stop exists to suppress.
        b[last + 1] = 0;
        var withoutMarker = BosSoundBank.Parse(b, "T.CLP");
        var raw = BosSoundBank.Decode(b, withoutMarker.Sounds[0]);

        Assert.False(withoutMarker.Sounds[0].HasEndBlock);
        Assert.Equal(280, raw.Length);
        Assert.All(raw.AsSpan(252, 28).ToArray(), s => Assert.Equal((short)28_672, s));
    }

    [Fact]
    public void Decode_StaysInsideSixteenBitRange()
    {
        // The filter feedback can run away if the accumulator is not clamped; a shipped bank decoded
        // without clamping overflows within a few hundred blocks.
        var b = Bank(new Spec("long", 22050, 200));
        var bank = BosSoundBank.Parse(b, "T.CLP");
        var pcm = BosSoundBank.Decode(b, bank.Sounds[0]);

        Assert.All(pcm, s => Assert.InRange(s, short.MinValue, short.MaxValue));
    }

    [Fact]
    public void Clump_PlacesEveryEntryOnItsProbeChainAtTheResidentUnit()
    {
        var clump = BosClumpFile.Parse(Bank(Mixed), "BAR_S.CLP");

        Assert.Equal(0, clump.Misplaced);
        Assert.Equal(8, clump.SlotCount);
        Assert.Equal(BosClumpFile.ResidentPageLength, clump.PageLength);
        Assert.False(clump.IsStreamed);
        Assert.Equal("c1/BAR/BAR_S.clp", Assert.Single(clump.Strings));
    }

    /// <summary>One synthetic sound: name, header rate, and block count.</summary>
    internal readonly record struct Spec(string Name, int Rate, int Blocks);
}
