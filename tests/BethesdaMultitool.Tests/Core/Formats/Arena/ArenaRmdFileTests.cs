using System.Collections.Generic;
using System.Linq;
using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Vectors for <see cref="ArenaRmdFile" />, the fixed 64x64 wilderness chunk. Both storage
///     modes are covered: the four raw city-quarter chunks (#001-#004, which declare length 0)
///     and the word-RLE ones every other chunk uses.
/// </summary>
public class ArenaRmdFileTests
{
    [Fact]
    public void Constants_MatchTheFixedChunkGeometry()
    {
        Assert.Equal(64, ArenaRmdFile.Width);
        Assert.Equal(64, ArenaRmdFile.Depth);
        Assert.Equal(64 * 64, ArenaRmdFile.VoxelsPerLayer);
        Assert.Equal(64 * 64 * 2, ArenaRmdFile.BytesPerLayer);
        Assert.Equal(64 * 64 * 2 * 3, ArenaRmdFile.UncompressedFileLength);
        Assert.Equal(24576, ArenaRmdFile.UncompressedFileLength);
    }

    [Fact]
    public void Parse_UncompressedChunk_SplitsIntoThreeLayers()
    {
        var bytes = new byte[ArenaRmdFile.UncompressedFileLength];

        // The leading word doubles as the first floor voxel, so it must stay 0 (empty) for the
        // file to read as uncompressed — that is exactly how the four raw chunks are stored.
        // The floor marker therefore goes in voxel 1, not voxel 0.
        bytes[2] = 0x11;
        bytes[3] = 0x00;
        bytes[ArenaRmdFile.BytesPerLayer] = 0x22;
        bytes[ArenaRmdFile.BytesPerLayer + 1] = 0x00;
        bytes[(ArenaRmdFile.BytesPerLayer * 2) + 0] = 0x33;
        bytes[(ArenaRmdFile.BytesPerLayer * 2) + 1] = 0x00;

        var chunk = ArenaRmdFile.Parse(bytes, "001.RMD");

        Assert.False(chunk.WasCompressed);
        Assert.Equal(0, chunk.Floor[0]);
        Assert.Equal(0x11, chunk.Floor[1]);
        Assert.Equal(0x22, chunk.Map1[0]);
        Assert.Equal(0x33, chunk.Map2[0]);
        Assert.Equal(ArenaRmdFile.VoxelsPerLayer, chunk.Floor.Length);
    }

    [Fact]
    public void Parse_UncompressedChunkOfTheWrongSize_Throws()
    {
        // Declared length 0 means "stored raw", which pins the file size exactly.
        var ex = Assert.Throws<InvalidDataException>(
            () => ArenaRmdFile.Parse(new byte[ArenaRmdFile.UncompressedFileLength - 2], "BAD.RMD"));

        Assert.Contains("24576", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CompressedChunk_ExpandsThroughWordRle()
    {
        const int totalWords = ArenaRmdFile.VoxelsPerLayer * 3;
        var bytes = new List<byte>
        {
            totalWords & 0xFF,
            (totalWords >> 8) & 0xFF
        };

        // One packet per layer: repeat a distinct word across the whole layer.
        foreach (var value in new[] { 0x0011, 0x0022, 0x0033 })
        {
            var repeat = -ArenaRmdFile.VoxelsPerLayer;
            bytes.Add((byte)(repeat & 0xFF));
            bytes.Add((byte)((repeat >> 8) & 0xFF));
            bytes.Add((byte)(value & 0xFF));
            bytes.Add((byte)((value >> 8) & 0xFF));
        }

        var chunk = ArenaRmdFile.Parse([.. bytes], "010.RMD");

        Assert.True(chunk.WasCompressed);
        Assert.All(chunk.Floor, v => Assert.Equal(0x11, v));
        Assert.All(chunk.Map1, v => Assert.Equal(0x22, v));
        Assert.All(chunk.Map2, v => Assert.Equal(0x33, v));
    }

    [Fact]
    public void Parse_CompressedChunkThatIsTooShort_Throws()
    {
        // Declares four words, which is far less than three full layers.
        byte[] bytes = [0x04, 0x00, 0xFC, 0xFF, 0x11, 0x00];

        Assert.Throws<InvalidDataException>(() => ArenaRmdFile.Parse(bytes, "SHORT.RMD"));
    }

    [Fact]
    public void Parse_Empty_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ArenaRmdFile.Parse([0x00], "T.RMD"));
    }

    [Fact]
    public void Parse_LayersAreLittleEndianAndRowMajor()
    {
        var bytes = new byte[ArenaRmdFile.UncompressedFileLength];

        // Voxel (x=1, z=2) of the floor layer = index 1 + (2 * 64) = 129.
        bytes[129 * 2] = 0xCD;
        bytes[(129 * 2) + 1] = 0xAB;

        var chunk = ArenaRmdFile.Parse(bytes, "T.RMD");

        Assert.Equal(0xABCD, ArenaMifLevel.VoxelAt(chunk.Floor, ArenaRmdFile.Width, 1, 2));
    }
}
