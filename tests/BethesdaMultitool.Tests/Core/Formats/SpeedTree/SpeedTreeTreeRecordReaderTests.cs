using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SpeedTree;

public sealed class SpeedTreeTreeRecordReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveDimming_SchemaDecodedCnamPreservesSpeedsInBothEndianModes(bool bigEndian)
    {
        var cnam = new byte[32];
        WriteSingle(cnam, 12, 0.5f, bigEndian); // BranchDimmingValue
        WriteSingle(cnam, 16, 0.25f, bigEndian); // LeafDimmingValue
        WriteSingle(cnam, 24, 2f, bigEndian); // RockSpeed
        WriteSingle(cnam, 28, 3f, bigEndian); // RustleSpeed
        var decoded = SubrecordSchemaView.Read("CNAM", "TREE", cnam, bigEndian).Raw;

        var result = SpeedTreeTreeRecordReader.ResolveDimming(
            new Dictionary<string, object?> { ["CNAM"] = decoded }, decodedTree: null);

        Assert.Equal(new SpeedTreeDimming(0.25f, 0.5f, 2f, 3f), result);
    }

    [Fact]
    public void ResolveDimming_PreservesCnamDimmingAndPhaseSpeeds()
    {
        var fields = new Dictionary<string, object?>
        {
            ["CNAM"] = new Dictionary<string, object?>
            {
                ["LeafDimmingValue"] = 0.25f,
                ["BranchDimmingValue"] = 0.5f,
                ["RockSpeed"] = 2f,
                ["RustleSpeed"] = 3f
            }
        };

        var result = SpeedTreeTreeRecordReader.ResolveDimming(fields, decodedTree: null);

        Assert.Equal(new SpeedTreeDimming(0.25f, 0.5f, 2f, 3f), result);
    }

    [Fact]
    public void ResolveDimming_LegacyCnamDefaultsMissingSpeedsToOne()
    {
        var fields = new Dictionary<string, object?>
        {
            ["CNAM"] = new Dictionary<string, object?>
            {
                ["LeafDimmingValue"] = 0.25f,
                ["BranchDimmingValue"] = 0.5f
            }
        };

        var result = SpeedTreeTreeRecordReader.ResolveDimming(fields, decodedTree: null);

        Assert.Equal(new SpeedTreeDimming(0.25f, 0.5f, 1f, 1f), result);
    }

    [Fact]
    public void ResolveDimming_AuthoredZeroSpeedsRemainZero()
    {
        var fields = new Dictionary<string, object?>
        {
            ["CNAM"] = new Dictionary<string, object?>
            {
                ["RockSpeed"] = 0f,
                ["RustleSpeed"] = 0f
            }
        };

        var result = SpeedTreeTreeRecordReader.ResolveDimming(fields, decodedTree: null);

        Assert.Equal(new SpeedTreeDimming(0f, 0f, 0f, 0f), result);
    }

    private static void WriteSingle(byte[] data, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);
        }
    }
}
