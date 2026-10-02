using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Core.Modeling.Starfield;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Starfield.StarfieldMeshModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     The bounded probe (cut-2 plan section 4.1, decision D7, slice 3): every class of the walk on builder streams, the
///     tail-boundary rule with its W-0 control, every truncation refused, and the claim boundary against NIF and XnGine
///     content. The Bucket-B oracle (<see cref="Cut2MeshOracleTests" />) compares the same rule with the Python probe on
///     every retail cover file and decline control.
/// </summary>
public class StarfieldMeshModelProbeTests
{
    [Fact]
    public void ACompleteStreamWithTheTailIsSupportedAndConfirmed()
    {
        var bytes = StarfieldMeshTestBuilder.Quad().Build();

        var result = Probe(bytes);

        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
        Assert.Equal("Starfield .mesh v2, 4 vertices, 6 indices, meshlet tail", result.Evidence!.Description);
        Assert.Equal(bytes.Length, result.Evidence.ByteLength);
    }

    [Fact]
    public void ACompleteTailLessStreamIsSupportedAndConfirmed()
    {
        var result = Probe(StarfieldMeshTestBuilder.Quad(tail: false).Build());

        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
        Assert.EndsWith(StarfieldMeshModelProbe.NoTailNote, result.Evidence!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTailBoundaryCutIsRefusedByTheNormalWRule_AndTheSameCutWithWZeroIsNot()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        var cut = builder.Build()[..builder.TailOffset()];

        var result = Probe(cut);

        Assert.Equal(ModelProbeKind.Unsupported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, result.Confidence);
        Assert.Equal(StarfieldMeshModelProbe.TailBoundaryReason, result.Reason);

        // Control: the identical cut whose normals carry W 0 is exactly a retail tail-less stream, and is Supported.
        builder.Normals = [.. builder.Normals!.Select(static code => code & 0x3FFFFFFFu)];
        var control = Probe(builder.Build()[..builder.TailOffset()]);
        Assert.Equal(ModelProbeKind.Supported, control.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, control.Confidence);
    }

    /// <summary>
    ///     The smallest valid tail-less streams (review finding 2): indices [0, 0, 0], scale 1, no weights, every stream
    ///     and LOD count 0. Version 2 with one vertex is 60 bytes, version 0 with two is 62. The reader's checks accept
    ///     both, so the probe must too; the length bound is exactly their layout, so one byte less is not Supported.
    /// </summary>
    [Theory]
    [InlineData(2u, 1, 60)]
    [InlineData(1u, 1, 60)]
    [InlineData(0u, 2, 62)]
    public void TheSmallestTailLessStreams_AreSupported_AndOneByteLessIsNot(uint version, int vertices, int size)
    {
        var bytes = new StarfieldMeshTestBuilder
        {
            Version = version,
            Indices = [0, 0, 0],
            Scale = 1f,
            Positions = [.. Enumerable.Repeat(((short)0, (short)0, (short)0), vertices)],
            Tail = false
        }.Build();
        Assert.Equal(size, bytes.Length);

        var result = Probe(bytes);

        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
        Assert.EndsWith(StarfieldMeshModelProbe.NoTailNote, result.Evidence!.Description, StringComparison.Ordinal);
        // The reader's own checks accept the same bytes, so the probe and the read agree on them.
        var mesh = StarfieldMeshFile.Parse(bytes);
        Assert.NotNull(mesh);
        Assert.Equal(vertices, StarfieldMeshModelFacts.Measure(mesh, bytes.Length).VertexCount);
        // Control: the same stream one byte short is not Supported.
        Assert.NotEqual(ModelProbeKind.Supported, Probe(bytes[..^1]).Kind);
    }

    [Fact]
    public void TheIndexLengthBound_IsTheSmallestTailLessLayout()
    {
        // Version 2: 8 + 2 x 6 + 46 = 66 bytes is the least declared length the quad's index count admits.
        var quad = StarfieldMeshTestBuilder.Quad().Build();
        Assert.Equal(ModelProbeKind.Supported, Probe(quad, declaredLength: 66).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(quad, declaredLength: 65).Kind);

        // Version 0 has no LOD count: 8 + 2 x 6 + 42 = 62.
        var zero = StarfieldMeshTestBuilder.Quad(version: 0).Build();
        Assert.Equal(ModelProbeKind.Supported, Probe(zero, declaredLength: 62).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(zero, declaredLength: 61).Kind);
    }

    /// <summary>
    ///     A complete stream that runs out (review finding 5): after the vertex count validated it is Unsupported and
    ///     Tentative with <see cref="StarfieldMeshModelProbe.TruncatedReason" />; before it, inside the indices or between
    ///     them and the vertex count, NotAModel. Each has its control, the same prefix incomplete, which is Supported and
    ///     Tentative. The two early stages are reached with the whole stream's declared length, so the index and vertex
    ///     length checks pass and the stage decides.
    /// </summary>
    [Fact]
    public void ACompleteStreamThatRunsOut_IsTruncatedAfterTheVertexCount_AndNotAModelBeforeIt()
    {
        var bytes = StarfieldMeshTestBuilder.Quad().Build();

        // 70 bytes: the index bound (66) and the positions (to byte 56) fit, and the stream ends inside UV0.
        var afterVertexCount = Probe(bytes[..70]);
        Assert.Equal(ModelProbeKind.Unsupported, afterVertexCount.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, afterVertexCount.Confidence);
        Assert.Equal(StarfieldMeshModelProbe.TruncatedReason, afterVertexCount.Reason);
        Assert.Contains("4 vertices", afterVertexCount.Evidence!.Description, StringComparison.Ordinal);
        var afterIncomplete = Probe(bytes[..70], isComplete: false);
        Assert.Equal(ModelProbeKind.Supported, afterIncomplete.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, afterIncomplete.Confidence);

        // 26 bytes: the indices end at 20 and the scale at 24; the stream ends inside weightsPerVertex.
        var beforeVertexCount = Probe(bytes[..26], isComplete: true, declaredLength: bytes.Length);
        Assert.Equal(ModelProbeKind.NotAModel, beforeVertexCount.Kind);
        var beforeIncomplete = Probe(bytes[..26], isComplete: false, declaredLength: bytes.Length);
        Assert.Equal(ModelProbeKind.Supported, beforeIncomplete.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, beforeIncomplete.Confidence);
        Assert.DoesNotContain("vertices", beforeIncomplete.Evidence!.Description, StringComparison.Ordinal);

        // 14 bytes: the stream ends inside the index list.
        var insideIndices = Probe(bytes[..14], isComplete: true, declaredLength: bytes.Length);
        Assert.Equal(ModelProbeKind.NotAModel, insideIndices.Kind);
        Assert.Equal(ModelProbeKind.Supported, Probe(bytes[..14], isComplete: false, declaredLength: bytes.Length).Kind);
    }

    public static TheoryData<string> Streams()
    {
        return new TheoryData<string> { "quad", "tailless", "full", "version0", "version1" };
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void NoTruncationIsSupported(string stream)
    {
        var bytes = Build(stream);
        Assert.Equal(ModelProbeKind.Supported, Probe(bytes).Kind);

        for (var cut = 0; cut < bytes.Length; cut++)
        {
            var result = Probe(bytes[..cut]);
            Assert.True(result.Kind != ModelProbeKind.Supported,
                $"{stream}: the complete {cut}-byte truncation is {result.Kind} ({result.Evidence?.Description}).");
        }
    }

    public static TheoryData<string> Violations()
    {
        return new TheoryData<string>
        {
            "version3", "indexCount%3", "indexCount>length", "scale0", "scaleNaN", "weightsPerVertex9",
            "vertexCount0", "vertexCount65537", "index=vertexCount", "uv0Count", "normalsCount", "weightsCount",
            "lodCount9", "lodCount%3", "lodIndex=vertexCount", "cull!=meshlets", "trailingByte"
        };
    }

    [Theory]
    [MemberData(nameof(Violations))]
    public void EachViolatedCheckIsNotAModel(string violation)
    {
        var (bytes, declared) = Violate(violation);

        var result = Probe(bytes, declaredLength: declared);

        Assert.Equal(ModelProbeKind.NotAModel, result.Kind);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public void TheBoundsAdmitTheirLimits_TheControlsOfTheViolations()
    {
        // weightsPerVertex 8 and 8 LOD lists are admitted (9 of either is not); 65,536 vertices are admitted (65,537 are
        // not); version 0 and 1 are admitted (3 is not).
        var eight = StarfieldMeshTestBuilder.Quad();
        eight.WeightsPerVertex = 8;
        eight.Weights = [.. Enumerable.Repeat(((ushort)0, (ushort)8191), 32)];
        Assert.Equal(ModelProbeKind.Supported, Probe(eight.Build()).Kind);

        var lods = StarfieldMeshTestBuilder.Quad();
        lods.Lods = [.. Enumerable.Repeat(new ushort[] { 0, 1, 2 }, 8)];
        Assert.Equal(ModelProbeKind.Supported, Probe(lods.Build()).Kind);

        var large = LargeStream(65536);
        var tentative = Probe(large, isComplete: false);
        Assert.Equal(ModelProbeKind.Supported, tentative.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, tentative.Confidence);

        Assert.Equal(ModelProbeKind.Supported, Probe(StarfieldMeshTestBuilder.Quad(version: 0).Build()).Kind);
        Assert.Equal(ModelProbeKind.Supported, Probe(StarfieldMeshTestBuilder.Quad(version: 1).Build()).Kind);
    }

    [Fact]
    public void AnIncompletePrefixIsTentative_WhereverItEnds()
    {
        // 12,000 vertices put the end of the positions past the 64 KiB prefix.
        var afterVertexCount = Probe(LargeStream(12000), isComplete: false);
        Assert.Equal(ModelProbeKind.Supported, afterVertexCount.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, afterVertexCount.Confidence);
        Assert.EndsWith(StarfieldMeshModelProbe.IncompleteNote, afterVertexCount.Evidence!.Description,
            StringComparison.Ordinal);
        Assert.Contains("12000 vertices", afterVertexCount.Evidence.Description, StringComparison.Ordinal);

        // 33,000 indices put the end of the index list past the prefix, before the vertex count.
        var builder = StarfieldMeshTestBuilder.Quad();
        builder.Indices = [.. Enumerable.Range(0, 33000).Select(static i => (ushort)(i % 4))];
        var bytes = builder.Build();
        var insideIndices = Probe(bytes, isComplete: false);
        Assert.Equal(ModelProbeKind.Supported, insideIndices.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, insideIndices.Confidence);
        Assert.DoesNotContain("vertices", insideIndices.Evidence!.Description, StringComparison.Ordinal);

        // Control: the same prefix declared complete is a stream that ends inside its index list, which is no .mesh.
        // The whole stream's declared length passes the index length check (8 + 66,000 + 46 fits it), so it is the
        // walk's stage that decides, not the bound.
        Assert.True(8 + 2L * 33000 + 46 <= bytes.Length);
        Assert.Equal(ModelProbeKind.NotAModel,
            Probe(bytes[..ModelSourceCandidate.MaximumProbeBytes], isComplete: true, declaredLength: bytes.Length).Kind);
    }

    [Fact]
    public void TheProbeClaimsNoNifNoXnGineMeshAndNothingShorterThanEightBytes()
    {
        var nif = NifModelTestSupport.IdentityPrefix("Gamebryo File Format, Version 20.2.0.7", 0x14020007, false, 11, 3,
            34);
        var xngine = Encoding.ASCII.GetBytes("v2.7").Concat(new byte[60]).ToArray();

        Assert.Equal(ModelProbeKind.NotAModel, Probe(nif, isComplete: false).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(xngine).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe([2, 0, 0, 0, 0, 0, 0]).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe([2, 0, 0, 0, 0, 0, 0], isComplete: false).Kind);
    }

    [Fact]
    public void RecognitionIsByContentAlone()
    {
        var bytes = StarfieldMeshTestBuilder.Quad().Build();
        var asNif = new ModelSourceCandidate(new AssetEntry(new AssetReference(SourceId, "meshes/looks-like.nif"),
            bytes.Length), bytes, true);
        var png = new ModelSourceCandidate(new AssetEntry(new AssetReference(SourceId, "geometries/a/b.mesh"), 8),
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], true);

        Assert.Equal(ModelProbeKind.Supported, new StarfieldMeshModelReader().Probe(asNif).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, new StarfieldMeshModelReader().Probe(png).Kind);
    }

    [Fact]
    public void BesideTheNifReader_EachFileResolvesToExactlyOneReader()
    {
        var registry = new ModelSourceRegistry([new NifModelReader(), new StarfieldMeshModelReader()]);
        var mesh = StarfieldMeshTestBuilder.Quad().Build();
        var nif = NifModelTestSupport.IdentityPrefix("Gamebryo File Format, Version 20.2.0.7", 0x14020007, false, 11, 3,
            34);

        var meshSelection = registry.Probe(new ModelSourceCandidate(
            new AssetEntry(new AssetReference(SourceId, "geometries/a/b.mesh"), mesh.Length), mesh, true));
        var nifSelection = registry.Probe(new ModelSourceCandidate(
            new AssetEntry(new AssetReference(SourceId, "meshes/a.nif"), null), nif, false));

        Assert.Equal(ModelSourceSelectionKind.Supported, meshSelection.Kind);
        Assert.IsType<StarfieldMeshModelReader>(meshSelection.Reader);
        // Control: a NIF resolves to the NIF reader, not to this one.
        Assert.Equal(ModelSourceSelectionKind.Supported, nifSelection.Kind);
        Assert.IsType<NifModelReader>(nifSelection.Reader);
    }

    /// <summary>A stream of <paramref name="vertices" /> vertices with no attribute streams and one triangle.</summary>
    internal static byte[] LargeStream(int vertices)
    {
        return new StarfieldMeshTestBuilder
        {
            Indices = [0, 1, 2],
            Positions = [.. Enumerable.Range(0, vertices).Select(static i => ((short)(i % 30000), (short)0, (short)0))],
            Meshlets = [(3, 0, 1, 0)],
            Cull = [[0f, 0f, 0f, 1f, 1f, 1f]]
        }.Build();
    }

    private static byte[] Build(string stream)
    {
        return stream switch
        {
            "quad" => StarfieldMeshTestBuilder.Quad().Build(),
            "tailless" => StarfieldMeshTestBuilder.Quad(tail: false).Build(),
            "full" => StarfieldMeshFileLayoutTests.FullBuilder().Build(),
            "version0" => StarfieldMeshTestBuilder.Quad(version: 0).Build(),
            "version1" => StarfieldMeshTestBuilder.Quad(version: 1).Build(),
            _ => throw new ArgumentOutOfRangeException(nameof(stream), stream, "Unknown stream.")
        };
    }

    private static (byte[] Bytes, long? Declared) Violate(string violation)
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        long? declared = null;
        switch (violation)
        {
            case "version3":
                builder.Version = 3;
                break;
            case "indexCount%3":
                builder.IndexCountOverride = 5;
                break;
            case "indexCount>length":
                declared = 8 + 2 * 6 + 45;
                break;
            case "scale0":
                builder.Scale = 0f;
                break;
            case "scaleNaN":
                builder.Scale = float.NaN;
                break;
            case "weightsPerVertex9":
                builder.WeightsPerVertex = 9;
                builder.Weights = [.. Enumerable.Repeat(((ushort)0, (ushort)7281), 36)];
                break;
            case "vertexCount0":
                builder.Positions = [];
                builder.Indices = [];
                break;
            case "vertexCount65537":
                return (LargeStream(65537), null);
            case "index=vertexCount":
                builder.Indices = [0, 4, 2, 0, 2, 3];
                break;
            case "uv0Count":
                builder.Uv0 = builder.Uv0![..3];
                break;
            case "normalsCount":
                builder.Normals = [.. builder.Normals!, builder.Normals![0]];
                break;
            case "weightsCount":
                builder.WeightsPerVertex = 1;
                builder.Weights = [(0, 65535), (0, 65535), (0, 65535)];
                break;
            case "lodCount9":
                builder.Lods = [.. Enumerable.Repeat(new ushort[] { 0, 1, 2 }, 9)];
                break;
            case "lodCount%3":
                builder.Lods = [[0, 1]];
                break;
            case "lodIndex=vertexCount":
                builder.Lods = [[0, 1, 4]];
                break;
            case "cull!=meshlets":
                builder.Cull = [];
                break;
            case "trailingByte":
                builder.Trailing = [0];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(violation), violation, "Unknown violation.");
        }

        return (builder.Build(), declared);
    }
}
