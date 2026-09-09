using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for the Fallout: Brotherhood of Steel <c>.vat</c> voice bank: a section of the
///     streamed <c>_T.CLP</c> keyed <c>&lt;stem&gt;.vat</c>, split by a name/offset directory in the
///     resident <c>.CLP</c>, played at the IOP driver's fixed stream pitch.
/// </summary>
public sealed class BosVoiceBankTests
{
    private const int VatSize = 3 * 2048;

    private static readonly (string, int)[] Lines =
    [
        ("Ruby_02.vag", 0),
        ("Ruby_03.vag", 2048),
        ("Ruby_02.anm", 4096)
    ];

    /// <summary>A streamed (4096-page) clump with one <c>.vat</c> section of three sectors at page 1.</summary>
    private static byte[] Streamed(string firstString = "bar.vat")
    {
        // Two lines of ADPCM: a zero block then patterned blocks.
        var vat = new byte[VatSize];
        for (var at = 16; at < VatSize; at += 16)
        {
            vat[at] = 0x25;
            for (var j = 2; j < 16; j++)
            {
                vat[at + j] = (byte)(0x11 * ((at / 16 + j) % 7));
            }
        }

        return BosClumpFixture.Build(
            BosClumpFile.StreamedPageLength,
            [new BosClumpFixture.Entry(BosAssetHash.Compute("bar.vat"), vat)],
            firstString);
    }

    /// <summary>A resident (256-page) clump holding one section: a directory of the given lines plus its terminator.</summary>
    private static byte[] Resident((string Name, int Offset)[] lines, int terminator = VatSize)
    {
        var dir = new byte[(lines.Length + 1) * BosVoiceBank.RecordLength];
        for (var i = 0; i < lines.Length; i++)
        {
            Encoding.ASCII.GetBytes(lines[i].Name).CopyTo(dir.AsSpan(i * BosVoiceBank.RecordLength));
            BinaryPrimitives.WriteInt32LittleEndian(dir.AsSpan(i * BosVoiceBank.RecordLength + 0x40), lines[i].Offset);
        }

        BinaryPrimitives.WriteInt32LittleEndian(dir.AsSpan(lines.Length * BosVoiceBank.RecordLength + 0x40),
            terminator);

        // The directory's slot key is not reproduced by any tried spelling; a fixed literal stands in.
        return BosClumpFixture.Build(
            BosClumpFile.ResidentPageLength,
            [new BosClumpFixture.Entry(0x67030D49u, dir)]);
    }

    [Fact]
    public void TryOpen_SplitsTheSectionByTheDirectoryAndTheEnginesLengthRule()
    {
        var streamed = Streamed();
        Assert.True(BosVoiceBank.TryOpen(streamed, Resident(Lines), "BAR", out var bank, out var error), error);

        Assert.Equal(1, bank.Section.StartPage);
        Assert.Equal(["Ruby_02.vag", "Ruby_03.vag", "Ruby_02.anm"], bank.Lines.Select(l => l.Name));
        Assert.Equal([2048, 2048, 2048], bank.Lines.Select(l => l.Length));
        Assert.Equal([BosVoiceLineKind.Voice, BosVoiceLineKind.Voice, BosVoiceLineKind.Animation],
            bank.Lines.Select(l => l.Kind));
    }

    [Fact]
    public void TryOpen_RefusesADirectoryWhoseTerminatorIsNotTheSectionSize()
    {
        // The terminator's offset must be the END of the .vat — that is the tiling gate, 26/26.
        Assert.False(BosVoiceBank.TryOpen(Streamed(), Resident(Lines, VatSize - 2048), "BAR", out _, out var error));
        Assert.Contains("line directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryOpen_ReportsALevelWithNoVoiceBank()
    {
        // 28 of the 54 shipped levels carry only .tex and .hsh; the key is simply absent.
        Assert.False(BosVoiceBank.TryOpen(Streamed("mill_2.tex"), Resident(Lines), "MILL_2", out _, out var error));
        Assert.Contains("mill_2.vat", error, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionKey_IsTheFirstHeaderStringsStemPlusVat()
    {
        // BAR_T's first string is 'bar.vat'; WARE_1's is 'warehouse_1.hsh' — the stem rules, not the extension.
        var clump = BosClumpFile.Parse(Streamed("warehouse_1.hsh"), "WARE_1_T.CLP");
        Assert.Equal("warehouse_1.vat", BosVoiceBank.SectionKey(clump));
    }

    [Fact]
    public void SampleRate_IsTheIopDriversStreamPitch()
    {
        // HELLO.IRX .data+0x20: streamPitch = {4096, 2046, 2046}; voice lines run on streams 1 and 2.
        Assert.Equal(2046, BosVoiceBank.StreamPitch);
        Assert.Equal(23976.5625, BosVoiceBank.SampleRate);
    }

    [Fact]
    public void Decode_ProducesTwentyEightSamplesPerBlock()
    {
        var streamed = Streamed();
        Assert.True(BosVoiceBank.TryOpen(streamed, Resident(Lines), "BAR", out var bank, out var error), error);

        var pcm = bank.Decode(streamed, bank.Lines[1]);
        Assert.Equal(2048 / 16 * BosSoundBank.SamplesPerBlock, pcm.Length);
        Assert.Contains(pcm, s => s != 0);
    }

    [Fact]
    public void TryReadDirectory_AllowsAliasesButNotADescendingOffset()
    {
        // BAR ships nine entries that share a line (equal offsets); nothing ships a descending one.
        var alias = new[] { ("A.vag", 0), ("B.vag", 0), ("C.vag", 2048) };
        Assert.True(BosVoiceBank.TryReadDirectory(Resident(alias).AsSpan(256, 4 * BosVoiceBank.RecordLength), VatSize,
            out var lines));
        Assert.Equal([0, 2048, VatSize - 2048], lines.Select(l => l.Length));

        var descending = new[] { ("A.vag", 2048), ("B.vag", 0) };
        Assert.False(BosVoiceBank.TryReadDirectory(Resident(descending).AsSpan(256, 3 * BosVoiceBank.RecordLength),
            VatSize, out _));
    }
}