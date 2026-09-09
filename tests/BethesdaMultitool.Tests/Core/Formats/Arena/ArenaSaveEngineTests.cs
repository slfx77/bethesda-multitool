using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Synthetic vectors for <see cref="ArenaSaveEngine" />: a 17,983-byte image with a
///     non-repeating head, three slots that satisfy the populated-record oracle, one slot whose
///     doubled block is all zero (which the oracle must NOT count) and one whose two blocks
///     differ. Offsets are literals from the measured layout, not computed from the constants.
/// </summary>
public sealed class ArenaSaveEngineTests
{
    private const int FileLength = 17_983;

    private static byte[] BuildFile()
    {
        var file = new byte[FileLength];

        // Head: a ramp whose stride-8 differences are never zero (i * 37 + 11; 8 * 37 = 296 = 40 mod 256).
        for (var i = 0; i < 3_664; i++)
        {
            file[i] = (byte)(i * 37 + 11);
        }

        // Slots 0, 2 and 5 populated: distinct doubled blocks at +40 and +48.
        PlantRecord(file, 3_664, [0x4C, 0x33, 0x4C, 0x66, 0x4C, 0x4C, 0x4C, 0x80]);
        PlantRecord(file, 5_772, [0x33, 0x19, 0x19, 0x5A, 0x4C, 0x4C, 0x66, 0x80]);
        PlantRecord(file, 8_934, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

        // Slot 3: the two blocks are equal but zero -> not populated.
        // (Left as the zero fill.)

        // Slot 1: two different non-zero blocks -> not populated.
        var slot1 = 4_718;
        for (var i = 0; i < 8; i++)
        {
            file[slot1 + 40 + i] = (byte)(0x10 + i);
            file[slot1 + 48 + i] = (byte)(0x20 + i);
        }

        // A marker in the tail so its extraction can be checked.
        file[FileLength - 1] = 0x7E;
        file[17_366] = 0x5A;

        return file;
    }

    private static void PlantRecord(byte[] file, int offset, byte[] block)
    {
        // Some body so the record is not just its oracle block.
        for (var i = 0; i < 40; i++)
        {
            file[offset + i] = (byte)(0xC0 + i);
        }

        block.CopyTo(file, offset + 40);
        block.CopyTo(file, offset + 48);
        file[offset + 1_053] = 0x11;
    }

    [Fact]
    public void Layout_IsHeadPlusThirteenRecordsPlusTail()
    {
        Assert.Equal(17_983, ArenaSaveEngine.FileLength);
        Assert.Equal(3_664, ArenaSaveEngine.HeadLength);
        Assert.Equal(1_054, ArenaSaveEngine.RecordStride);
        Assert.Equal(13, ArenaSaveEngine.RecordSlotCount);
        Assert.Equal(617, ArenaSaveEngine.TailLength);
        Assert.Equal(
            ArenaSaveEngine.FileLength,
            ArenaSaveEngine.HeadLength + ArenaSaveEngine.RecordSlotCount * ArenaSaveEngine.RecordStride +
            ArenaSaveEngine.TailLength);
        // 1,054 does not divide the file: 17 x 1,054 + 65.
        Assert.Equal(65, ArenaSaveEngine.FileLength % ArenaSaveEngine.RecordStride);
    }

    [Fact]
    public void IsSaveEngine_GatesOnLengthOnly()
    {
        Assert.True(ArenaSaveEngine.IsSaveEngine(new byte[FileLength]));
        Assert.False(ArenaSaveEngine.IsSaveEngine(new byte[FileLength - 1]));
        Assert.False(ArenaSaveEngine.IsSaveEngine(new byte[FileLength + 1]));
    }

    [Fact]
    public void Parse_RejectsWrongLength()
    {
        Assert.Throws<InvalidDataException>(() => ArenaSaveEngine.Parse(new byte[FileLength - 1], "short"));
    }

    [Fact]
    public void Parse_SplitsHeadRecordsAndTail()
    {
        var engine = ArenaSaveEngine.Parse(BuildFile(), "SAVEENGN.00");

        Assert.Equal(3_664, engine.Head.Length);
        Assert.Equal(11, engine.Head[0]);
        // (3,663 * 37 + 11) & 0xFF = 135,542 & 0xFF = 118.
        Assert.Equal(118, engine.Head[3_663]);

        Assert.Equal(13, engine.Records.Count);
        Assert.Equal(3_664, engine.Records[0].Offset);
        Assert.Equal(4_718, engine.Records[1].Offset);
        Assert.Equal(16_312, engine.Records[12].Offset);
        Assert.All(engine.Records, r => Assert.Equal(1_054, r.Bytes.Length));
        Assert.Equal(0xC0, engine.Records[0].Bytes[0]);
        Assert.Equal(0x11, engine.Records[0].Bytes[1_053]);
        Assert.Equal(0x10, engine.Records[1].Bytes[40]);

        Assert.Equal(617, engine.Tail.Length);
        Assert.Equal(0x5A, engine.Tail[0]);
        Assert.Equal(0x7E, engine.Tail[616]);
    }

    [Fact]
    public void PopulatedOracle_RequiresEqualNonZeroBlocks()
    {
        var engine = ArenaSaveEngine.Parse(BuildFile(), "SAVEENGN.00");

        int[] expected = [3_664, 5_772, 8_934];
        Assert.Equal(expected, engine.PopulatedRecordOffsets);
        Assert.True(engine.Records[0].IsPopulated);
        Assert.False(engine.Records[1].IsPopulated, "differing blocks must not count");
        Assert.True(engine.Records[2].IsPopulated);
        Assert.False(engine.Records[3].IsPopulated, "an all-zero doubled block must not count");
        Assert.True(engine.Records[5].IsPopulated);
        Assert.Equal(new byte[] { 0x33, 0x19, 0x19, 0x5A, 0x4C, 0x4C, 0x66, 0x80 },
            engine.Records[2].OracleBlock.ToArray());
    }

    [Fact]
    public void IsPopulatedRecord_OnShortInput_IsFalse()
    {
        Assert.False(ArenaSaveEngine.IsPopulatedRecord(new byte[55]));
    }

    [Fact]
    public void FindOracleHits_ScansEveryOffset_AndFindsOnlyThePlantedSlots()
    {
        var hits = ArenaSaveEngine.FindOracleHits(BuildFile());

        int[] expected = [3_664, 5_772, 8_934];
        Assert.Equal(expected, hits);
    }
}