using System.Text;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class TriParserTests
{
    [Fact]
    public void Parse_HeadHumanSample_ParsesStableHeaderCounts()
    {
        var path = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_pc\meshes\characters\head\headhuman.tri");
        Assert.SkipWhen(path == null, "Sample headhuman.tri not available.");

        var data = File.ReadAllBytes(path!);
        var tri = Assert.IsType<TriParser>(TriParser.Parse(data));

        Assert.Equal("FRTRI003", tri.Magic);
        Assert.Equal(1211, tri.VertexCount);
        Assert.Equal(2294, tri.TriangleCount);
        Assert.Equal((uint)1211, tri.GetHeaderWord(0x1C));
        Assert.Equal((uint)1, tri.GetHeaderWord(0x20));
        Assert.Equal((uint)38, tri.GetHeaderWord(0x24));
        Assert.Equal((uint)8, tri.GetHeaderWord(0x28));
        Assert.Equal((uint)238, tri.GetHeaderWord(0x2C));
        Assert.Equal(1211, tri.VertexBlock1Count);
        Assert.Equal(1, tri.StructuredSectionGroupCountHint);
        Assert.Equal(38, tri.InlineVectorMorphRecordCountHint);
        Assert.Equal(8, tri.IndexedMorphRecordCountHint);
        Assert.Equal(238, tri.NamedMetadataRecordCountHint);
        Assert.Equal(data.Length - TriParser.HeaderSize, tri.PayloadLength);
        Assert.Equal(1211, tri.VertexBlock0.Count);
        Assert.Equal(1211, tri.VertexBlock1.Count);
        Assert.Equal(330844, tri.RemainingPayloadLength);
        Assert.Collection(
            tri.RecordFamilies,
            named =>
            {
                Assert.Equal("NamedMetadata", named.Name);
                Assert.Equal(238, named.CountHint);
                Assert.Equal(0x2C, named.RecordSize);
                Assert.Equal(TriRecordPayloadKind.Opaque, named.PayloadKind);
                Assert.Equal(0, named.PayloadElementSize);
                Assert.Equal(0xB4, named.GenerationContextOffset);
                Assert.Null(named.MaterializedPayloadRootOffset);
                Assert.Null(named.MaterializedPayloadBeginOffset);
                Assert.Null(named.MaterializedPayloadEndOffset);
                Assert.Null(named.MaterializedPayloadCapacityOffset);
                Assert.Null(named.PreservedScalarOffset);
            },
            differential =>
            {
                Assert.Equal("DifferentialMorph", differential.Name);
                Assert.Equal(38, differential.CountHint);
                Assert.Equal(0x34, differential.RecordSize);
                Assert.Equal(TriRecordPayloadKind.Float3, differential.PayloadKind);
                Assert.Equal(12, differential.PayloadElementSize);
                Assert.Equal(0xCC, differential.GenerationContextOffset);
                Assert.Equal(0x1C, differential.MaterializedPayloadRootOffset);
                Assert.Equal(0x28, differential.MaterializedPayloadBeginOffset);
                Assert.Equal(0x2C, differential.MaterializedPayloadEndOffset);
                Assert.Equal(0x30, differential.MaterializedPayloadCapacityOffset);
                Assert.Null(differential.PreservedScalarOffset);
            },
            statistical =>
            {
                Assert.Equal("StatisticalMorph", statistical.Name);
                Assert.Equal(8, statistical.CountHint);
                Assert.Equal(0x38, statistical.RecordSize);
                Assert.Equal(TriRecordPayloadKind.UInt32, statistical.PayloadKind);
                Assert.Equal(4, statistical.PayloadElementSize);
                Assert.Equal(0xE4, statistical.GenerationContextOffset);
                Assert.Equal(0x20, statistical.MaterializedPayloadRootOffset);
                Assert.Equal(0x2C, statistical.MaterializedPayloadBeginOffset);
                Assert.Equal(0x30, statistical.MaterializedPayloadEndOffset);
                Assert.Equal(0x34, statistical.MaterializedPayloadCapacityOffset);
                Assert.Equal(0x1C, statistical.PreservedScalarOffset);
            });
        Assert.True(tri.TailStrings.Count >= 80);
        Assert.True(tri.IdentifierLikeTailStrings.Count >= 40);
        Assert.Contains(tri.TailStrings, info => info.Value == "Anger" && info.Offset > TriParser.HeaderSize);
        Assert.Contains(tri.IdentifierLikeTailStrings, info => info.Value == "BrowDownLeft");
        Assert.Contains(tri.IdentifierLikeTailStrings, info => info.Value == "MoodNeutral");
        Assert.Contains(tri.IdentifierLikeTailStrings, info => info.Value == "BlinkLeft");
        Assert.Contains(tri.IdentifierLikeTailStrings, info => info.Value == "LookUp");
        Assert.Equal(0.995286f, tri.VertexBlock0[0].X, 4);
        Assert.Equal(6.894405f, tri.VertexBlock0[0].Y, 4);
        Assert.Equal(-0.754721f, tri.VertexBlock0[0].Z, 4);
        Assert.Equal(-3.058735f, tri.VertexBlock1[0].X, 4);
        Assert.Equal(6.638306f, tri.VertexBlock1[0].Y, 4);
        Assert.Equal(7.190822f, tri.VertexBlock1[0].Z, 4);
        Assert.Equal(14, tri.HeaderWords.Count);
    }

    [Fact]
    public void Parse_EyeLeftHumanSample_ParsesStableHeaderCounts()
    {
        var path = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_pc\meshes\characters\head\eyelefthuman.tri");
        Assert.SkipWhen(path == null, "Sample eyelefthuman.tri not available.");

        var data = File.ReadAllBytes(path!);
        var tri = Assert.IsType<TriParser>(TriParser.Parse(data));

        Assert.Equal("FRTRI003", tri.Magic);
        Assert.Equal(49, tri.VertexCount);
        Assert.Equal(116, tri.TriangleCount);
        Assert.Equal((uint)49, tri.GetHeaderWord(0x1C));
        Assert.Equal((uint)1, tri.GetHeaderWord(0x20));
        Assert.Equal((uint)0, tri.GetHeaderWord(0x24));
        Assert.Equal((uint)4, tri.GetHeaderWord(0x28));
        Assert.Equal((uint)196, tri.GetHeaderWord(0x2C));
        Assert.Equal(49, tri.VertexBlock1Count);
        Assert.Equal(1, tri.StructuredSectionGroupCountHint);
        Assert.Equal(0, tri.InlineVectorMorphRecordCountHint);
        Assert.Equal(4, tri.IndexedMorphRecordCountHint);
        Assert.Equal(196, tri.NamedMetadataRecordCountHint);
        Assert.Equal(49, tri.VertexBlock0.Count);
        Assert.Equal(49, tri.VertexBlock1.Count);
        Assert.Equal(5791, tri.RemainingPayloadLength);
        Assert.Collection(
            tri.RecordFamilies,
            named => Assert.Equal(196, named.CountHint),
            differential => Assert.Equal(0, differential.CountHint),
            statistical => Assert.Equal(4, statistical.CountHint));
        Assert.Equal(
            ["LookDown", "LookLeft", "LookRight", "LookUp"],
            tri.IdentifierLikeTailStrings.Select(info => info.Value).ToArray());
        Assert.Equal(-2.361754f, tri.VertexBlock0[0].X, 4);
        Assert.Equal(6.803944f, tri.VertexBlock0[0].Y, 4);
        Assert.Equal(7.065372f, tri.VertexBlock0[0].Z, 4);
        Assert.Equal(-2.348898f, tri.VertexBlock1[0].X, 4);
        Assert.Equal(6.689672f, tri.VertexBlock1[0].Y, 4);
        Assert.Equal(6.696589f, tri.VertexBlock1[0].Z, 4);
    }

    [Fact]
    public void Parse_InvalidMagic_ReturnsNull()
    {
        var data = new byte[TriParser.HeaderSize];
        Encoding.ASCII.GetBytes("NOTTRI00").CopyTo(data, 0);

        Assert.Null(TriParser.Parse(data));
    }
}
