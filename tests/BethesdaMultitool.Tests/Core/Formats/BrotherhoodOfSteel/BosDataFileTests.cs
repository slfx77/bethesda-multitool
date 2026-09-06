using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for Fallout: Brotherhood of Steel's <c>.DDF</c> record store, shaped after the 55
///     shipped files measured 2026-09-06 — 43,006 records, every directory tiling exactly.
/// </summary>
public sealed class BosDataFileTests
{
    private static byte[] DataFile(int[] sizes, uint? countOverride = null, int firstOffsetBias = 0, uint thirdField = 0)
    {
        var count = sizes.Length;
        var directoryEnd = BosDataFile.HeaderLength + (count * BosDataFile.RecordLength);
        var b = new byte[directoryEnd + sizes.Sum()];

        BinaryPrimitives.WriteUInt32LittleEndian(b, countOverride ?? (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), BosDataFile.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), BosDataFile.SecondSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), BosDataFile.ThirdSignature);

        var cursor = directoryEnd + firstOffsetBias;
        for (var i = 0; i < count; i++)
        {
            var at = BosDataFile.HeaderLength + (i * BosDataFile.RecordLength);
            var hash = 0xA000_0000u + (uint)i;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), hash);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), (uint)cursor);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), thirdField);

            // Retail payloads open with a class code and then ECHO their own directory key at +4;
            // the fixture carries both so it exercises the same checks the shipped files pass.
            if (cursor >= 0 && cursor + 8 <= b.Length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(cursor), (uint)(i % 13));
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(cursor + 4), hash);
            }

            cursor += sizes[i];
        }

        return b;
    }

    [Fact]
    public void Parse_DerivesEachRecordSizeFromTheNextOffset()
    {
        // Records carry no length — the size is the gap to the next entry, and the last runs to EOF.
        var file = BosDataFile.Parse(DataFile([16, 32, 8]), "MILL_1.DDF");

        Assert.Equal(3, file.Records.Count);
        Assert.Equal([16, 32, 8], file.Records.Select(r => r.Size));
        Assert.Equal(BosDataFile.HeaderLength + (3 * BosDataFile.RecordLength), file.Records[0].Offset);
    }

    [Fact]
    public void Parse_RequiresTheFirstRecordToStartWhereTheDirectoryEnds()
    {
        // ⚑ 772 + 12 * count == the first offset, on 55/55 retail files. That equality is what
        // fixes the 772-byte header and the 12-byte record together.
        var error = Assert.Throws<InvalidDataException>(
            () => BosDataFile.Parse(DataFile([16], firstOffsetBias: 4), "BAD.DDF"));
        Assert.Contains("rather than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsANonZeroThirdField()
    {
        // Zero on all 43,006 retail entries; anything else means this is not the measured layout.
        Assert.Throws<InvalidDataException>(() => BosDataFile.Parse(DataFile([16], thirdField: 1), "BAD.DDF"));
    }

    [Fact]
    public void Parse_RejectsWrongHeaderConstants()
    {
        var b = DataFile([16]);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1056);

        Assert.Throws<InvalidDataException>(() => BosDataFile.Parse(b, "BAD.DDF"));
    }

    [Fact]
    public void Parse_RejectsACountThatDoesNotFit()
    {
        Assert.Throws<InvalidDataException>(() => BosDataFile.Parse(DataFile([16], countOverride: 99_999), "BAD.DDF"));
    }

    [Fact]
    public void Read_ReturnsTheRecordBytes()
    {
        var b = DataFile([4, 4]);
        var file = BosDataFile.Parse(b, "T.DDF");
        var start = file.Records[1].Offset;
        b[start] = 0xAB;

        Assert.Equal(0xAB, BosDataFile.Read(b, file.Records[1])[0]);
        Assert.Equal(4, BosDataFile.Read(b, file.Records[0]).Length);
    }

    [Fact]
    public void Parse_RequiresThePayloadToEchoItsDirectoryKey()
    {
        // ⚑ The payload repeats its own hash at +4 on all 43,006 retail records. That echo is the
        // independent check on the walk: read the directory at the wrong stride and the payloads
        // land elsewhere, so every echo fails at once.
        var b = DataFile([16]);
        var payload = BosDataFile.HeaderLength + BosDataFile.RecordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(payload + 4), 0xDEAD_BEEF);

        var error = Assert.Throws<InvalidDataException>(() => BosDataFile.Parse(b, "BAD.DDF"));
        Assert.Contains("echo", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExposesTheClassCodeAndTheDeclaredLength()
    {
        var b = DataFile([32, 32]);
        var payload = BosDataFile.HeaderLength + (2 * BosDataFile.RecordLength);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(payload + 16), 32);

        var file = BosDataFile.Parse(b, "T.DDF");

        Assert.Equal(0u, file.Records[0].Type);
        Assert.Equal(1u, file.Records[1].Type);
        Assert.Equal(32u, file.Records[0].DeclaredLength);
    }

    [Theory]
    [InlineData(0u, "Actor")]
    [InlineData(2u, "Weapon")]
    [InlineData(9u, "AI behaviour")]
    [InlineData(13u, "Trap")]
    public void DescribeType_NamesTheClassesTheNamesRevealed(uint type, string expected)
    {
        // Read off the SDB names, not guessed: type 9 holds "RangedBasic"/"DuckAndCover", type 0
        // holds the game's three player characters.
        Assert.Equal(expected, BosDataFile.DescribeType(type));
    }

    [Fact]
    public void DescribeType_ReturnsNullForTheOneCodeRetailNeverUses()
    {
        // 13 codes occur across all 43,006 records; 7 is the gap.
        Assert.Null(BosDataFile.DescribeType(7));
    }

    [Fact]
    public void IsDataFile_RequiresTheArithmeticNotJustTheConstants()
    {
        // ⚠ There is no magic string. The three constants alone are weak evidence, so the probe
        // demands the directory walk as well.
        Assert.True(BosDataFile.IsDataFile(DataFile([8, 8])));
        Assert.False(BosDataFile.IsDataFile(DataFile([8], firstOffsetBias: 8)));
        Assert.False(BosDataFile.IsDataFile(new byte[BosDataFile.HeaderLength]));
    }
}
