using FalloutXbox360Utils.Core.Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Utils;

public sealed class BinaryPatternSearcherTests
{
    [Fact]
    public void FindTextMatches_ReturnsAllOffsetsForRepeatedPattern()
    {
        var data = "alpha beta alpha gamma alpha"u8.ToArray();

        var matches = BinaryPatternSearcher.FindTextMatches(data, "alpha");

        Assert.Equal([0L, 11L, 23L], matches);
    }

    [Fact]
    public void FindTextMatches_IgnoreCaseMatchesMixedCaseBytes()
    {
        var data = "Alpha aLpHa ALPHA beta"u8.ToArray();

        var matches = BinaryPatternSearcher.FindTextMatches(
            data,
            "alpha",
            true);

        Assert.Equal([0L, 6L, 12L], matches);
    }

    [Fact]
    public void CountTextMatchesInFile_UsesSameSearchLogicAsInMemoryPath()
    {
        var tempFilePath = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName());

        try
        {
            File.WriteAllBytes(tempFilePath, "OutfitF.ddx outfitf.ddx"u8.ToArray());

            var count = BinaryPatternSearcher.CountTextMatchesInFile(
                tempFilePath,
                "outfitf.ddx",
                true);

            Assert.Equal(2, count);
        }
        finally
        {
            File.Delete(tempFilePath);
        }
    }
}