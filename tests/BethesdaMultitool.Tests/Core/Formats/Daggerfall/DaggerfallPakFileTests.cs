using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Vectors for <see cref="DaggerfallPakFile" />: a 500-entry u32 row-offset table, then rows
///     of (u16 count, u8 value) runs that must sum to exactly 1,001. Both retail overlays tile to
///     EOF under this rule (verified 2026-09-02).
/// </summary>
public class DaggerfallPakFileTests
{
    /// <summary>Builds a PAK whose every row is the given run list.</summary>
    private static byte[] BuildPak(IReadOnlyList<(int Count, byte Value)> runsPerRow)
    {
        var rowBytes = new List<byte>();
        foreach (var (count, value) in runsPerRow)
        {
            rowBytes.Add((byte)(count & 0xFF));
            rowBytes.Add((byte)((count >> 8) & 0xFF));
            rowBytes.Add(value);
        }

        var file = new List<byte>();
        var tableLength = DaggerfallPakFile.Height * 4;
        for (var row = 0; row < DaggerfallPakFile.Height; row++)
        {
            var offset = tableLength + row * rowBytes.Count;
            file.Add((byte)(offset & 0xFF));
            file.Add((byte)((offset >> 8) & 0xFF));
            file.Add((byte)((offset >> 16) & 0xFF));
            file.Add((byte)((offset >> 24) & 0xFF));
        }

        for (var row = 0; row < DaggerfallPakFile.Height; row++)
        {
            file.AddRange(rowBytes);
        }

        return [.. file];
    }

    [Fact]
    public void Constants_MatchTheOverlayGeometry()
    {
        // One wider than the 1,000-pixel heightmap: the last column repeats for edge lookups.
        Assert.Equal(1001, DaggerfallPakFile.Width);
        Assert.Equal(500, DaggerfallPakFile.Height);
    }

    [Fact]
    public void Parse_ExpandsRunsIntoTheRowBuffer()
    {
        var pak = DaggerfallPakFile.Parse(BuildPak([(1000, 7), (1, 9)]), "CLIMATE.PAK");

        Assert.Equal(DaggerfallPakFile.Width * DaggerfallPakFile.Height, pak.Values.Length);
        Assert.Equal(7, pak[0, 0]);
        Assert.Equal(7, pak[999, 0]);
        Assert.Equal(9, pak[1000, 0]);
        Assert.Equal(9, pak[1000, 499]);
    }

    [Fact]
    public void Parse_ManySmallRuns_SumToTheRowWidth()
    {
        var runs = new List<(int, byte)>();
        for (var i = 0; i < 1001; i++)
        {
            runs.Add((1, (byte)(i % 3)));
        }

        var pak = DaggerfallPakFile.Parse(BuildPak(runs), "POLITIC.PAK");

        Assert.Equal(0, pak[0, 0]);
        Assert.Equal(1, pak[1, 0]);
        Assert.Equal(2, pak[2, 0]);
        Assert.Equal(1000 % 3, pak[1000, 0]);
    }

    [Fact]
    public void Indexer_RejectsCoordinatesOutsideTheMap()
    {
        var pak = DaggerfallPakFile.Parse(BuildPak([(1001, 1)]), "CLIMATE.PAK");

        Assert.Throws<ArgumentOutOfRangeException>(() => pak[1001, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => pak[0, 500]);
        Assert.Throws<ArgumentOutOfRangeException>(() => pak[-1, 0]);
    }

    [Fact]
    public void Parse_RowOverrunningItsWidth_Throws()
    {
        // 1,002 values in a 1,001-wide row.
        Assert.Throws<InvalidDataException>(() =>
            DaggerfallPakFile.Parse(BuildPak([(1000, 1), (2, 2)]), "CLIMATE.PAK"));
    }

    [Fact]
    public void Parse_ZeroLengthRun_ThrowsInsteadOfSpinning()
    {
        Assert.Throws<InvalidDataException>(() =>
            DaggerfallPakFile.Parse(BuildPak([(0, 1), (1001, 1)]), "CLIMATE.PAK"));
    }

    [Fact]
    public void Parse_RowOffsetOutsideTheFile_Throws()
    {
        var file = BuildPak([(1001, 1)]);
        file[0] = 0xFF;
        file[1] = 0xFF;
        file[2] = 0xFF;
        file[3] = 0x7F;

        Assert.Throws<InvalidDataException>(() => DaggerfallPakFile.Parse(file, "CLIMATE.PAK"));
    }

    [Fact]
    public void Parse_TruncatedRow_Throws()
    {
        var file = BuildPak([(1001, 1)]);
        Array.Resize(ref file, file.Length - 2);

        Assert.Throws<InvalidDataException>(() => DaggerfallPakFile.Parse(file, "CLIMATE.PAK"));
    }

    [Fact]
    public void Parse_TooSmallForTheRowTable_Throws()
    {
        Assert.Throws<InvalidDataException>(() => DaggerfallPakFile.Parse(new byte[100], "CLIMATE.PAK"));
    }

    [Fact]
    public void IsPakFileName_AcceptsOnlyTheTwoOverlays()
    {
        Assert.True(DaggerfallPakFile.IsPakFileName("CLIMATE.PAK"));
        Assert.True(DaggerfallPakFile.IsPakFileName("politic.pak"));
        Assert.False(DaggerfallPakFile.IsPakFileName("OTHER.PAK"));
    }
}