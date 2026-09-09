using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Vectors for <see cref="DaggerfallWoodsFile" />. The synthetic file reproduces the retail
///     layout in miniature: the 144-byte header, the full 500,000-entry offset table, the
///     500,000-byte heightmap, and a handful of 47-byte cell records that every offset points
///     into — enough to pin the offsets and the sub-grid read without a 26 MB fixture.
/// </summary>
public class DaggerfallWoodsFileTests
{
    private const int TableLength = DaggerfallWoodsFile.PixelCount * 4;
    private const int HeightMapOffset = DaggerfallWoodsFile.HeaderLength + TableLength;
    private const int CellsOffset = HeightMapOffset + DaggerfallWoodsFile.PixelCount;
    private const int CellRecordLength = 47;

    /// <summary>Builds a file with two cell records; pixel (x=1, y=0) points at the second.</summary>
    private static byte[] BuildWoods()
    {
        var file = new byte[CellsOffset + 2 * CellRecordLength];
        var span = file.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span, TableLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], DaggerfallWoodsFile.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], DaggerfallWoodsFile.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], HeightMapOffset); // data section 1
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], DaggerfallWoodsFile.CellDataSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], HeightMapOffset);

        // Every pixel's offset points at cell 0, except pixel (1, 0) which points at cell 1.
        for (var i = 0; i < DaggerfallWoodsFile.PixelCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                span[(DaggerfallWoodsFile.HeaderLength + i * 4)..],
                (uint)(CellsOffset + (i == 1 ? CellRecordLength : 0)));
        }

        // Heightmap: pixel (x, y) = (x + y) & 0xFF, so positions are observable.
        for (var y = 0; y < DaggerfallWoodsFile.Height; y++)
        {
            for (var x = 0; x < DaggerfallWoodsFile.Width; x++)
            {
                file[HeightMapOffset + y * DaggerfallWoodsFile.Width + x] = (byte)((x + y) & 0xFF);
            }
        }

        // Cell 0's grid is all 9s; cell 1's grid counts 0..24 row-major.
        for (var i = 0; i < 25; i++)
        {
            file[CellsOffset + DaggerfallWoodsFile.CellDataSkip + i] = 9;
            file[CellsOffset + CellRecordLength + DaggerfallWoodsFile.CellDataSkip + i] = (byte)i;
        }

        return file;
    }

    [Fact]
    public void Constants_MatchTheRetailLayout()
    {
        Assert.Equal(144, DaggerfallWoodsFile.HeaderLength);
        Assert.Equal(500_000, DaggerfallWoodsFile.PixelCount);
        Assert.Equal(22, DaggerfallWoodsFile.CellDataSkip);

        // Header + table lands exactly where the retail file's heightmap sits... minus the
        // 1,024-byte data section retail inserts between them; the offset field is authoritative.
        Assert.Equal(2_000_144, HeightMapOffset);
    }

    [Fact]
    public void Parse_ReadsTheHeightMapFromTheHeadersOffset()
    {
        var woods = DaggerfallWoodsFile.Parse(BuildWoods(), "WOODS.WLD");

        Assert.Equal(HeightMapOffset, woods.HeightMapOffset);
        Assert.Equal(DaggerfallWoodsFile.PixelCount, woods.HeightMap.Length);
        Assert.Equal(0, woods.GetHeight(0, 0));
        Assert.Equal(3, woods.GetHeight(1, 2));
        Assert.Equal((999 + 499) & 0xFF, woods.GetHeight(999, 499));
    }

    [Fact]
    public void GetHeight_ClampsToTheMapEdges()
    {
        var woods = DaggerfallWoodsFile.Parse(BuildWoods(), "WOODS.WLD");

        Assert.Equal(woods.GetHeight(0, 0), woods.GetHeight(-5, -5));
        Assert.Equal(woods.GetHeight(999, 499), woods.GetHeight(5000, 5000));
    }

    [Fact]
    public void GetCellGrid_FollowsTheOffsetTableAndSkipsTheRecordHeader()
    {
        var woods = DaggerfallWoodsFile.Parse(BuildWoods(), "WOODS.WLD");

        Assert.All(woods.GetCellGrid(0, 0), v => Assert.Equal(9, v));

        var second = woods.GetCellGrid(1, 0);
        Assert.Equal(25, second.Length);
        Assert.Equal(0, second[0]);
        Assert.Equal(24, second[24]);

        // Row-major: (gx=2, gy=1) is index 7.
        Assert.Equal(7, second[1 * DaggerfallWoodsFile.CellGridSize + 2]);
    }

    [Fact]
    public void Parse_WrongDimensions_Throws()
    {
        var file = BuildWoods();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 999);

        Assert.Throws<InvalidDataException>(() => DaggerfallWoodsFile.Parse(file, "WOODS.WLD"));
    }

    [Fact]
    public void Parse_HeightMapOffsetPastEndOfFile_Throws()
    {
        var file = BuildWoods();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), (uint)(file.Length - 10));

        Assert.Throws<InvalidDataException>(() => DaggerfallWoodsFile.Parse(file, "WOODS.WLD"));
    }

    [Fact]
    public void GetCellGrid_OffsetPastEndOfFile_Throws()
    {
        var file = BuildWoods();
        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(DaggerfallWoodsFile.HeaderLength + 2 * 4), (uint)(file.Length - 5));

        var woods = DaggerfallWoodsFile.Parse(file, "WOODS.WLD");

        Assert.Throws<InvalidDataException>(() => woods.GetCellGrid(2, 0));
    }

    [Fact]
    public void Parse_TooSmall_Throws()
    {
        Assert.Throws<InvalidDataException>(() => DaggerfallWoodsFile.Parse(new byte[1000], "WOODS.WLD"));
    }
}