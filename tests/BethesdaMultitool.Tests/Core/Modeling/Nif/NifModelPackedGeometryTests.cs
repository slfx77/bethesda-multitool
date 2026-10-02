using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 10, the console packed geometry (plan section 6, row 10): the six measured layouts decode to the exactly
///     widened halves and copied floats, vertex colors follow the platform's byte order (L6's order being inferred and
///     reported as such), bone indices are read from the reversed word, the slot-3 weight sentinel reads as 0 while a
///     real fourth influence and the exporter's residual are kept and counted apart on the PS3 (stored lanes), the X360
///     types the engine's lanes (the fourth weight derived as one minus the stored three, the stored fourth half never
///     read), weight sums are recorded with their half-precision deviation and never renormalized, a skinned shape's vertex domain is the partition order
///     with point indices and offset triangles (one position per shape vertex), and everything outside the measurement
///     stays native state with a precise reason and no color diagnostic. Every fixture is laid out from the
///     measurement table by <see cref="NifTestPackedLayouts" />, never from the production layout table.
/// </summary>
public class NifModelPackedGeometryTests
{
    private static readonly Dictionary<string, string> Ps3 = new() { [BethesdaModelRegistration.PlatformOption] = "ps3" };

    private static readonly Dictionary<string, string> X360 = new() { [BethesdaModelRegistration.PlatformOption] = "x360" };

    public static TheoryData<string> Layouts => new(NifTestPackedLayouts.Ids);

    /// <summary>
    ///     Every layout decodes to the exact half round trip (or the copied float3 frame of L5 and L6), with UV set 0,
    ///     the tangent basis with its handedness (the Bitangent channel as the xyz, w = sign(dot(cross(N, Bitangent),
    ///     -Tangent)); <see cref="NifModelTangentFrameTests" /> pins it against the UVs), and colors where the layout has
    ///     them; the shape, data and packed
    ///     blocks are Typed and the primitive facts name the layout, stride and the fourth-half census. Control: a
    ///     truncating half-to-float (two mantissa bits dropped) yields a different position sequence than the one
    ///     asserted, so a lossy decoder fails this test.
    /// </summary>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void EveryLayout_DecodesToTheExactHalfRoundTrip(string layout)
    {
        var (bytes, data, shapeBlock, dataBlock, packedBlock) = Build(layout);
        var result = Read(bytes, X360);
        var document = result.Document;
        var primitive = PrimitiveOf(document, NifTestPackedLayouts.IsSkinned(layout) ? "Body" : "Shape");
        var half = NifTestPackedLayouts.HasHalfFrame(layout);

        Assert.Equal(data.VertexCount, primitive.Vertices.Count);
        var expectedPositions = new List<Vector3>();
        var truncatedPositions = new List<Vector3>();
        for (var i = 0; i < data.VertexCount; i++)
        {
            var vertex = primitive.Vertices[i];
            expectedPositions.Add(Rounded(data.Positions, i, true));
            truncatedPositions.Add(new Vector3(Truncated(data.Positions[i * 3]), Truncated(data.Positions[i * 3 + 1]),
                Truncated(data.Positions[i * 3 + 2])));
            AssertBits(Rounded(data.Positions, i, true), vertex.Position);
            AssertBits(Rounded(data.Normals, i, half), vertex.Normal);
            Assert.Equal(NifTestPackedLayouts.RoundTrip(data.Uvs[i * 2]), vertex.TexCoord.X);
            Assert.Equal(NifTestPackedLayouts.RoundTrip(data.Uvs[i * 2 + 1]), vertex.TexCoord.Y);
            if (NifTestPackedLayouts.HasColors(layout))
            {
                var expected = new Vector4(data.Colors![i * 4] / 255f, data.Colors[i * 4 + 1] / 255f,
                    data.Colors[i * 4 + 2] / 255f, data.Colors[i * 4 + 3] / 255f);
                Assert.Equal(expected, vertex.Color);
            }
            else
            {
                Assert.Equal(Vector4.One, vertex.Color);
            }
        }

        Assert.NotEqual(expectedPositions, truncatedPositions);
        var tangents = Assert.IsType<SceneTangents>(primitive.Tangents);
        for (var i = 0; i < data.VertexCount; i++)
        {
            // The Bitangent channel (the lower-offset frame stream, the PC Bitangents array) runs along +dP/du and is
            // the xyz; the Tangent channel enters only the handedness.
            var b = Rounded(data.Bitangents, i, half);
            var n = Rounded(data.Normals, i, half);
            var t = Rounded(data.Tangents, i, half);
            var dot = Vector3.Dot(Vector3.Cross(n, b), -t);
            var w = dot < 0 ? -1f : 1f;
            Assert.Equal(new Vector4(b, w), tangents.Values[i]);
        }

        Assert.Equal(SceneNormalMode.Vertex, primitive.NormalMode);
        Assert.Equal(NifTestPackedLayouts.HasColors(layout) ? 0 : null, primitive.PrimaryColorAttributeIndex);
        foreach (var block in new[] { shapeBlock, dataBlock, packedBlock })
        {
            Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification($"block:{block}").Kind);
        }

        var packed = PrimitivePayload(document, 0)["packed"]!;
        Assert.Equal(layout, (string)packed["layout"]!);
        Assert.Equal(NifTestPackedLayouts.Stride(layout), (int)packed["stride"]!);
        Assert.Equal("big-endian", (string)packed["byteOrder"]!);
        Assert.True((bool)packed["structure"]!["shaderIndexMatchesMeasured"]!);
        var censuses = packed["fourthHalves"]!.AsArray();
        Assert.Equal(half ? 4 : 1, censuses.Count);
        foreach (var census in censuses)
        {
            Assert.Equal(data.VertexCount, (int)census!["fourthHalfExactlyOne"]!);
            Assert.Equal(0, (int)census["fourthHalfOther"]!);
        }

        var packedRow = BlockPayload(document, packedBlock);
        Assert.Equal("BSPackedAdditionalGeometryData", (string)packedRow["type"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     The half widening is bit-exact on every one of the 65,536 binary16 patterns: finite values and infinities
    ///     equal the runtime's widening bit for bit (subnormals included), and a NaN stays a NaN. Control: the
    ///     truncating widening used above differs from the exact one on patterns with low mantissa bits set.
    /// </summary>
    [Fact]
    public void HalfWidening_IsBitExact_OnEveryPattern()
    {
        var differing = 0;
        for (var bits = 0; bits <= ushort.MaxValue; bits++)
        {
            var pattern = (ushort)bits;
            var exact = NifPackedHalf.ToSingle(pattern);
            var runtime = (float)BitConverter.UInt16BitsToHalf(pattern);
            if (float.IsNaN(runtime))
            {
                Assert.True(float.IsNaN(exact));
            }
            else
            {
                Assert.Equal(BitConverter.SingleToUInt32Bits(runtime), BitConverter.SingleToUInt32Bits(exact));
            }

            if (!float.IsNaN(runtime) && Truncated(pattern) != exact)
            {
                differing++;
            }
        }

        Assert.True(differing > 40000, $"the truncating control differs on only {differing} patterns");
    }

    /// <summary>
    ///     A packed color is read A, R, G, B on the X360 and A, G, B, R on the PS3, both to the same R, G, B, A. Under
    ///     the default (assumed) platform the reader says so once per packed block with colors; an explicit platform
    ///     silences it, and a layout without colors never raises it. Controls: the PS3 bytes read under the X360 order
    ///     give different colors on every vertex whose R and B differ; and a color layout kept as native state (a
    ///     static count mismatch, a skinned count mismatch, a skinned shape on a weightless layout) raises the
    ///     not-typed diagnostic but no platform diagnostic, because no color reached typed state (the typed L1 read's
    ///     single platform diagnostic is the positive control, so dropping the emit altogether is caught too).
    /// </summary>
    [Fact]
    public void Colors_FollowThePlatformByteOrder_AndTheDefaultIsReported()
    {
        var colors = NifPackedFixture.QuadColors;
        var expected = Enumerable.Range(0, 4).Select(i => new Vector4(colors[i * 4] / 255f, colors[i * 4 + 1] / 255f,
            colors[i * 4 + 2] / 255f, colors[i * 4 + 3] / 255f)).ToList();

        var x360 = Read(new NifPackedFixture { Layout = "L1", Data = NifPackedFixture.Quad(colors) }.Build());
        Assert.Equal(expected, PrimitiveOf(x360.Document, "Shape").Vertices.Select(v => v.Color));
        var assumed = Assert.Single(x360.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.PlatformAssumedDiagnostic);
        Assert.Contains(NifPackedGeometryReader.PlatformAssumedMessage, DiagnosticText(assumed));
        var platform = PrimitivePayload(x360.Document, 0)["packed"]!["platform"]!;
        Assert.True((bool)platform["assumed"]!);
        Assert.Equal("x360", (string)platform["value"]!);
        Assert.Equal("A,R,G,B", (string)platform["colorByteOrder"]!);
        Assert.Equal(nameof(SceneValueProvenance.Assumed), (string)platform["colorByteOrderProvenance"]!);

        var ps3 = Read(new NifPackedFixture { Layout = "L1", Data = NifPackedFixture.Quad(colors), Ps3Order = true }
            .Build(), Ps3);
        Assert.Equal(expected, PrimitiveOf(ps3.Document, "Shape").Vertices.Select(v => v.Color));
        Assert.DoesNotContain(ps3.Document.Diagnostics, d => d.Code == NifPackedGeometryReader.PlatformAssumedDiagnostic);
        var declared = PrimitivePayload(ps3.Document, 0)["packed"]!["platform"]!;
        Assert.False((bool)declared["assumed"]!);
        Assert.Equal("ps3", (string)declared["value"]!);
        Assert.Equal("A,G,B,R", (string)declared["colorByteOrder"]!);
        Assert.Equal(nameof(SceneValueProvenance.ReverseEngineered), (string)declared["colorByteOrderProvenance"]!);

        var explicitX360 = Read(new NifPackedFixture { Layout = "L1", Data = NifPackedFixture.Quad(colors) }.Build(),
            X360);
        Assert.DoesNotContain(explicitX360.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.PlatformAssumedDiagnostic);

        var noColors = Read(new NifPackedFixture { Layout = "L2" }.Build());
        Assert.DoesNotContain(noColors.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.PlatformAssumedDiagnostic);

        var wrongOrder = Read(new NifPackedFixture { Layout = "L1", Data = NifPackedFixture.Quad(colors), Ps3Order = true }
            .Build());
        var misread = PrimitiveOf(wrongOrder.Document, "Shape").Vertices.Select(v => v.Color).ToList();
        for (var i = 0; i < 4; i++)
        {
            Assert.NotEqual(expected[i], misread[i]);
        }

        // Controls: a color layout kept as native state under the default platform raises no platform diagnostic.
        var staticMismatch = Read(new NifPackedFixture
        {
            Layout = "L1", Data = NifPackedFixture.Quad(colors), DeclaredVertices = 5
        }.Build());
        AssertReason(staticMismatch, [1, 2, 3], NifModelCoverage.PackedPayloadReason);
        Assert.DoesNotContain(staticMismatch.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.PlatformAssumedDiagnostic);

        var six = NifPackedSkinFixture.SixVertices(SkinColors());
        var skinnedMismatch = Read(new NifPackedSkinFixture
        {
            Layout = "L4",
            Data = new NifTestPackedVertexData
            {
                Positions = six.Positions[..15],
                Normals = six.Normals[..15],
                Uvs = six.Uvs[..10],
                Tangents = six.Tangents[..15],
                Bitangents = six.Bitangents[..15],
                Colors = six.Colors![..20],
                Weights = six.Weights![..20],
                BoneIndices = six.BoneIndices![..20]
            }
        }.Build());
        AssertReason(skinnedMismatch, [4, 5, 6, 7, 8, 9], NifModelCoverage.PackedPayloadReason);
        Assert.DoesNotContain(skinnedMismatch.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.PlatformAssumedDiagnostic);

        var weightless = Read(new NifPackedSkinFixture
        {
            Layout = "L1", Data = NifPackedSkinFixture.SixVertices(SkinColors()), DeclaredVertices = 6
        }.Build());
        AssertReason(weightless, [4, 5, 6, 7, 8, 9], NifModelCoverage.PackedSkinnedStaticLayoutReason);
        Assert.DoesNotContain(weightless.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.PlatformAssumedDiagnostic);

        SceneValidation.ValidateStructure(x360.Document);
        SceneValidation.ValidateStructure(ps3.Document);
    }

    /// <summary>
    ///     L6's color byte order is carried over from L1 and L4, not measured (every retail L6 vertex is white), so an
    ///     L6 color channel carries Assumed provenance even under a declared platform, the evidence text in its facts,
    ///     the count of vertices on which the two orders would differ, and the inferred-order diagnostic once per
    ///     packed block (beside the assumed-platform one when the platform was not declared). Controls: the same bytes
    ///     in an L1 file under the same declared platform are ReverseEngineered with no diagnostic and no evidence; a
    ///     grey-only L6 quad reports zero order-sensitive vertices while still carrying Assumed and the diagnostic; and
    ///     the layout table marks only L6.
    /// </summary>
    [Fact]
    public void InferredColorOrder_IsAssumedAndReported_OnL6Only()
    {
        var colors = NifPackedFixture.QuadColors;
        var expected = Enumerable.Range(0, 4).Select(i => new Vector4(colors[i * 4] / 255f, colors[i * 4 + 1] / 255f,
            colors[i * 4 + 2] / 255f, colors[i * 4 + 3] / 255f)).ToList();

        var l6 = Read(new NifPackedFixture { Layout = "L6", Data = NifPackedFixture.Quad(colors) }.Build(), X360);
        Assert.Equal(expected, PrimitiveOf(l6.Document, "Shape").Vertices.Select(v => v.Color));
        var payload = PrimitivePayload(l6.Document, 0);
        var platform = payload["packed"]!["platform"]!;
        Assert.False((bool)platform["assumed"]!);
        Assert.Equal(nameof(SceneValueProvenance.Assumed), (string)platform["colorByteOrderProvenance"]!);
        Assert.Equal(NifPackedGeometryLayout.L6ColorInference, (string)platform["colorByteOrderEvidence"]!);
        Assert.Equal(4, (int)platform["colorOrderSensitiveVertices"]!);
        var vertexColors = payload["vertexColors"]!;
        Assert.Equal(nameof(SceneValueProvenance.Assumed), (string)vertexColors["byteOrderProvenance"]!);
        Assert.Equal(NifPackedGeometryLayout.L6ColorInference, (string)vertexColors["colorByteOrderEvidence"]!);
        Assert.Equal(4, (int)vertexColors["colorOrderSensitiveVertices"]!);
        var inferred = Assert.Single(l6.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.ColorOrderInferredDiagnostic);
        Assert.Contains("4 of 4 vertices", DiagnosticText(inferred));
        Assert.Contains(NifPackedGeometryLayout.L6ColorInference, DiagnosticText(inferred));
        Assert.DoesNotContain(l6.Document.Diagnostics, d => d.Code == NifPackedGeometryReader.PlatformAssumedDiagnostic);

        var assumedL6 = Read(new NifPackedFixture { Layout = "L6", Data = NifPackedFixture.Quad(colors) }.Build());
        Assert.Single(assumedL6.Document.Diagnostics, d => d.Code == NifPackedGeometryReader.PlatformAssumedDiagnostic);
        Assert.Single(assumedL6.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.ColorOrderInferredDiagnostic);

        // Control: the same bytes in an L1 file under the same declared platform were measured.
        var l1 = Read(new NifPackedFixture { Layout = "L1", Data = NifPackedFixture.Quad(colors) }.Build(), X360);
        var measured = PrimitivePayload(l1.Document, 0)["packed"]!["platform"]!;
        Assert.Equal(nameof(SceneValueProvenance.ReverseEngineered), (string)measured["colorByteOrderProvenance"]!);
        Assert.Null(measured["colorByteOrderEvidence"]);
        Assert.Equal(4, (int)measured["colorOrderSensitiveVertices"]!);
        Assert.Null(PrimitivePayload(l1.Document, 0)["vertexColors"]!["colorByteOrderEvidence"]);
        Assert.DoesNotContain(l1.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.ColorOrderInferredDiagnostic);

        // Control: a grey-only L6 quad decodes identically under either order, and says so with a zero count.
        byte[] grey = [255, 255, 255, 255, 0, 0, 0, 128, 90, 90, 90, 64, 17, 17, 17, 0];
        var greyL6 = Read(new NifPackedFixture { Layout = "L6", Data = NifPackedFixture.Quad(grey) }.Build(), X360);
        var greyPlatform = PrimitivePayload(greyL6.Document, 0)["packed"]!["platform"]!;
        Assert.Equal(0, (int)greyPlatform["colorOrderSensitiveVertices"]!);
        Assert.Equal(nameof(SceneValueProvenance.Assumed), (string)greyPlatform["colorByteOrderProvenance"]!);
        var greyDiagnostic = Assert.Single(greyL6.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.ColorOrderInferredDiagnostic);
        Assert.Contains("0 of 4 vertices", DiagnosticText(greyDiagnostic));

        foreach (var layout in NifPackedGeometryLayout.Known)
        {
            Assert.Equal(layout.Id == "L6", layout.ColorByteOrderInferred);
        }

        var l6Layout = Assert.Single(NifPackedGeometryLayout.Known, l => l.Id == "L6");
        Assert.Equal(NifPackedGeometryLayout.L6ColorInference,
            l6Layout.Stream(NifPackedStreamKind.VertexColor)!.Inference);
        Assert.Null(Assert.Single(NifPackedGeometryLayout.Known, l => l.Id == "L1")
            .Stream(NifPackedStreamKind.VertexColor)!.Inference);
        Assert.Null(Assert.Single(NifPackedGeometryLayout.Known, l => l.Id == "L4")
            .Stream(NifPackedStreamKind.VertexColor)!.Inference);
        SceneValidation.ValidateStructure(l6.Document);
        SceneValidation.ValidateStructure(greyL6.Document);
    }

    /// <summary>
    ///     The bone-index word is big-endian with slot 0 in its low byte, so slot k is byte 3 - k, each into the owning
    ///     partition's bone list; the weights come stride 4 in packed order. A slot whose weight is 0 is padded with
    ///     joint 0 rather than the partition's own bone there (the second partition's Bones[0] is 2, which the old
    ///     reading left in every unused slot of its vertices). Control: the memory byte order names different joints
    ///     for every vertex with a non-zero slot 1.
    /// </summary>
    [Fact]
    public void BoneIndices_AreReadFromTheReversedWord_IntoThePartitionBoneList()
    {
        var document = Read(new NifPackedSkinFixture().Build(), X360).Document;
        var influences = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(document, "Body").SkinInfluences);

        Assert.Equal(4, influences.InfluencesPerVertex);
        int[] expected = [0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 2, 1, 0, 0, 1, 2, 0, 0, 2, 0, 0, 0];
        Assert.Equal(expected, influences.JointIndices);
        Assert.Equal(Bits([1f, 0f, 0f, 0f, 0.5f, 0.5f, 0f, 0f, 0.25f, 0.75f, 0f, 0f, 0.75f, 0.25f, 0f, 0f, 0.5f, 0.5f, 0f,
            0f, 1f, 0f, 0f, 0f]), Bits(influences.Weights));

        // Control: reading the four bytes in memory order (slot k = byte k) would name these joints instead.
        int[] memoryOrder = [0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 2, 2, 1, 2, 2, 2, 2, 1, 2, 2, 2, 2];
        Assert.NotEqual(memoryOrder, influences.JointIndices);
        var facts = PrimitivePayload(document, 0)["skin"]!["influences"]!;
        Assert.Equal("BSPackedAdditionalGeometryData", (string)facts["source"]!);
        Assert.False((bool)facts["normalized"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A point shared by two partitions yields identical joint and weight lanes on both packed copies, because unused
    ///     slots are padded with joint 0 on every partition. The fixture is shaped like the retail case that Blender's
    ///     welding importer refused on three X360 creature packages (gate-1a hop D, 2026-09-26; 92 of 563 welded corpus
    ///     pairs differed only in zero-weight slots, none in a weighted slot): shape vertices 1 and 2 appear in both
    ///     partitions, each weighted 1.0 on joint 1, which is local bone 1 in partition 0 (Bones [0, 1]) and in
    ///     partition 1 (Bones [2, 1]). Without the padding the second partition's copies read [1, 2, 2, 2] against the
    ///     first's [1, 0, 0, 0]. Control: the old reading, computed from the partitions' bone lists, differs on both points.
    /// </summary>
    [Fact]
    public void SharedPointsAcrossPartitions_CarryIdenticalLanes_BecauseUnusedSlotsPadWithJointZero()
    {
        var data = NifPackedSkinFixture.SixVertices();
        // Packed order [0, 1, 2, 2, 3, 1]: packed vertices 1 and 5 are shape vertex 1; 2 and 3 are shape vertex 2.
        foreach (var packed in new[] { 1, 5, 2, 3 })
        {
            for (var k = 0; k < 4; k++)
            {
                data.Weights![packed * 4 + k] = k == 0 ? 1f : 0f;
                data.BoneIndices![packed * 4 + k] = (byte)(k == 0 ? 1 : 0);
            }
        }

        var document = Read(new NifPackedSkinFixture { Data = data }.Build(), X360).Document;
        var influences = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(document, "Body").SkinInfluences);
        var joints = influences.JointIndices;
        var weights = influences.Weights;
        foreach (var (first, second) in new[] { (1, 5), (2, 3) })
        {
            Assert.Equal([1, 0, 0, 0], joints.Skip(first * 4).Take(4));
            Assert.Equal(joints.Skip(first * 4).Take(4), joints.Skip(second * 4).Take(4));
            Assert.Equal(Bits(weights.Skip(first * 4).Take(4)), Bits(weights.Skip(second * 4).Take(4)));
        }

        // Control: the old reading padded each unused slot with the owning partition's Bones[bone index], so the
        // second partition (Bones [2, 1]) named joint 2 where the first (Bones [0, 1]) named joint 0.
        int[] bones0 = [0, 1];
        int[] bones1 = [2, 1];
        int[] oldFirst = [bones0[1], bones0[0], bones0[0], bones0[0]];
        int[] oldSecond = [bones1[1], bones1[0], bones1[0], bones1[0]];
        Assert.Equal([1, 2, 2, 2], oldSecond);
        Assert.NotEqual(oldFirst, oldSecond);
        var facts = PrimitivePayload(document, 0)["skin"]!["influences"]!;
        Assert.Equal(17, (int)facts["zeroWeightSlotsPaddedWithJointZero"]!);
        Assert.Contains("padded with joint 0", (string)facts["rule"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A slot-3 half of exactly 1.0 whose three siblings sum to 1 within 2e-3 is the sentinel and reads as 0; a
    ///     genuine fourth influence (siblings summing to less than 1) is kept, including one with slots 1 and 2 empty
    ///     and one whose siblings are all 0 (the sentinel rule has two conditions, and a 1.0 beside siblings summing
    ///     to 0 is not it); a vertex whose four slots sum to 0.9 keeps that sum; and a slot-3 half of 2^-24 (the
    ///     exporter's residual) is carried raw and counted apart from the non-zero fourth weights. Controls: the raw
    ///     reading keeps 1.0 in the sentinel slots and fails the equality; a decoder that zeroes every slot-3 1.0 fails
    ///     the [0, 0, 0, 1] vertex; a renormalizing decoder fails the 0.9 sum; and a single non-zero count would read
    ///     5 where the two counts read 4 and 1. Read under the PS3, which keeps the stored lanes; the X360 reading of the
    ///     same bytes derives the fourth weight instead (<see cref="EngineLanes_DeriveTheFourthWeight_OnX360" />).
    /// </summary>
    [Fact]
    public void SentinelWeight_ReadsAsZero_ButARealFourthInfluenceIsKept()
    {
        const float residual = 1f / 16777216f;
        var weights = NineVertices(
        [
            0.5f, 0.3f, 0.2f, 1f,
            0.25f, 0.25f, 0.25f, 0.25f,
            0.4f, 0f, 0f, 0.6f,
            0.3f, 0.7f, 0f, 1f,
            1f, 0f, 0f, 0f,
            0.5f, 0.5f, 0f, 0f,
            0f, 0f, 0f, 1f,
            0.3f, 0.3f, 0f, 0.3f,
            1f, 0f, 0f, residual
        ]);

        var document = Read(new NifPackedSkinFixture { Data = weights, Partitions = ThreeAndSix }.Build(), Ps3).Document;
        var influences = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(document, "Body").SkinInfluences);

        var expected = weights.Weights!.Select(NifTestPackedLayouts.RoundTrip).ToArray();
        expected[3] = 0f;
        expected[15] = 0f;
        Assert.Equal(Bits(expected), Bits(influences.Weights));
        Assert.Equal(NifTestPackedLayouts.RoundTrip(0.25f), influences.Weights[7]);
        Assert.Equal(NifTestPackedLayouts.RoundTrip(0.6f), influences.Weights[11]);
        Assert.Equal(0f, influences.Weights[9]);
        Assert.Equal(0f, influences.Weights[10]);
        Assert.Equal(1f, influences.Weights[27]);
        Assert.Equal(0.900146484375, (double)influences.Weights[28] + influences.Weights[29] + influences.Weights[30] +
                                     influences.Weights[31]);
        Assert.Equal(BitConverter.SingleToUInt32Bits(residual), BitConverter.SingleToUInt32Bits(influences.Weights[35]));

        var raw = weights.Weights.Select(NifTestPackedLayouts.RoundTrip).ToArray();
        Assert.NotEqual(Bits(raw), Bits(influences.Weights));
        var facts = PrimitivePayload(document, 0)["packed"]!["weights"]!;
        Assert.Equal(2, (int)facts["sentinelSlot3ReadAsZero"]!);
        Assert.Equal(4, (int)facts["slot3NonZero"]!);
        Assert.Equal(1, (int)facts["slot3Residual"]!);
        Assert.Null(facts["slot3RealInfluence"]);
        Assert.Equal(5, (int)facts["slot3NonZero"]! + (int)facts["slot3Residual"]!);
        var skinFacts = PrimitivePayload(document, 0)["skin"]!["influences"]!;
        Assert.Equal(2, (int)skinFacts["sentinelSlot3ReadAsZero"]!);
        Assert.Equal(4, (int)skinFacts["slot3NonZero"]!);
        Assert.Equal(1, (int)skinFacts["slot3Residual"]!);
        Assert.False((bool)skinFacts["normalized"]!);
        Assert.Equal("stored", (string)skinFacts["lanes"]!);
        Assert.Equal(NifModelSkinInfluences.PackedRule, (string)skinFacts["rule"]!);
        Assert.Null(skinFacts["engineLanes"]);
        Assert.Equal([0, 1, 2, 2, 3, 1, 0, 3, 1], Assert.IsType<ScenePointIndices>(PrimitiveOf(document, "Body").PointIndices).Values);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     The packed weights are binary16, so a vertex's four slots sum to one only within the half's precision; the
    ///     influences keep the widened halves and the facts record the largest deviation with the note naming the
    ///     precision, so a later lowering step can state the loss as the format's. Control: a renormalizing decoder
    ///     would report a zero deviation and unit sums; the fixture whose halves are exact reports zero. Read under the
    ///     PS3 (the stored lanes); on the X360 the engine lanes of the same vertex sum to exactly one
    ///     (<see cref="EngineLanes_TheFlagHalves_ThroughTheReader_OnX360" />).
    /// </summary>
    [Fact]
    public void PackedWeightSums_RecordTheHalfDeviation_WithoutRenormalizing()
    {
        var data = NifPackedSkinFixture.SixVertices();
        data.Weights![4] = 0.3f;
        data.Weights[5] = 0.7f;

        var document = Read(new NifPackedSkinFixture { Data = data }.Build(), Ps3).Document;
        var influences = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(document, "Body").SkinInfluences);

        Assert.Equal(NifTestPackedLayouts.RoundTrip(0.3f), influences.Weights[4]);
        Assert.Equal(NifTestPackedLayouts.RoundTrip(0.7f), influences.Weights[5]);
        var sum = (double)influences.Weights[4] + influences.Weights[5] + influences.Weights[6] + influences.Weights[7];
        Assert.Equal(1.000244140625, sum);
        var facts = PrimitivePayload(document, 0)["skin"]!["influences"]!;
        Assert.Equal(0.000244140625, (double)facts["weightSumMaxDeviation"]!);
        Assert.Equal(1, (int)facts["verticesWhoseSumIsNotOneWithin1e-4"]!);
        Assert.Equal(NifModelSkinInfluences.PackedWeightSumNote, (string)facts["weightSumNote"]!);
        Assert.False((bool)facts["normalized"]!);

        var exact = PrimitivePayload(Read(new NifPackedSkinFixture().Build(), Ps3).Document, 0)["skin"]!["influences"]!;
        Assert.Equal(0.0, (double)exact["weightSumMaxDeviation"]!);
        Assert.Equal(0, (int)exact["verticesWhoseSumIsNotOneWithin1e-4"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     On the X360 the influences are the engine's lanes: slots 0-2 as stored, the fourth weight derived as
    ///     1f - ((w0 + w1) + w2) on the stored slot-3 bone (partition Bones[byte 0]; joint 0 in partition 0, joint 2 in
    ///     partition 1), merged into a positive lane on the same joint; the stored fourth half is never read. On the nine
    ///     vertices of <see cref="SentinelWeight_ReadsAsZero_ButARealFourthInfluenceIsKept" />: the sentinel vertex keeps
    ///     its three weights (r = 0), the 0.9-sum vertex gets its missing 0.1 on joint 2 (merged, sum exactly one), the
    ///     [0.3, 0.7, 0, 1] vertex's r = -2^-12 merges into its joint-2 lane, [0, 0, 0, 1] becomes a new lane of 1 on
    ///     joint 2, and the residual half 2^-24 is gone. The stored fourth half is not the engine's weight on four
    ///     vertices, counted on the decoded half before the sentinel rule: the sentinel vertex 0 (stored 1.0, r = 0), the
    ///     [0.3, 0.7, 0, 1] sentinel (r = -2^-12), [0.3, 0.3, 0, 0.3] and the 2^-24 residual; comparing after the sentinel
    ///     rule would miss vertex 0 and read 3. Control: the PS3 reading of the same bytes keeps the stored lanes and the
    ///     0.9 sum.
    /// </summary>
    [Fact]
    public void EngineLanes_DeriveTheFourthWeight_OnX360()
    {
        var weights = NineVertices(
        [
            0.5f, 0.3f, 0.2f, 1f,
            0.25f, 0.25f, 0.25f, 0.25f,
            0.4f, 0f, 0f, 0.6f,
            0.3f, 0.7f, 0f, 1f,
            1f, 0f, 0f, 0f,
            0.5f, 0.5f, 0f, 0f,
            0f, 0f, 0f, 1f,
            0.3f, 0.3f, 0f, 0.3f,
            1f, 0f, 0f, 1f / 16777216f
        ]);
        var bytes = new NifPackedSkinFixture { Data = weights, Partitions = ThreeAndSix }.Build();
        var document = Read(bytes, X360).Document;
        var influences = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(document, "Body").SkinInfluences);

        int[] joints = [0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 2, 1, 0, 0, 2, 0, 0, 0, 2, 1, 0, 0, 0, 0, 0, 2, 2, 1, 0, 0, 2, 0, 0, 0];
        float[] lanes =
        [
            0.5f, 0.300048828125f, 0.199951171875f, 0f,
            0.5f, 0.25f, 0.25f, 0f,
            1f, 0f, 0f, 0f,
            0.2998046875f, 0.7001953125f, 0f, 0f,
            1f, 0f, 0f, 0f,
            0.5f, 0.5f, 0f, 0f,
            0f, 0f, 0f, 1f,
            0.699951171875f, 0.300048828125f, 0f, 0f,
            1f, 0f, 0f, 0f
        ];
        Assert.Equal(joints, influences.JointIndices);
        Assert.Equal(Bits(lanes), Bits(influences.Weights));
        var facts = PrimitivePayload(document, 0)["skin"]!["influences"]!;
        Assert.Equal("engine", (string)facts["lanes"]!);
        Assert.Equal("x360", (string)facts["platform"]!);
        Assert.False((bool)facts["platformAssumed"]!);
        Assert.Equal(NifPackedEngineLanes.Rule, (string)facts["rule"]!);
        Assert.Equal(NifPackedEngineLanes.WeightSumNote, (string)facts["weightSumNote"]!);
        Assert.Equal(0.0, (double)facts["weightSumMaxDeviation"]!);
        Assert.Equal(0, (int)facts["verticesWhoseSumIsNotOneWithin1e-4"]!);
        var engine = facts["engineLanes"]!;
        Assert.Equal(4, (int)engine["derivedZero"]!);
        Assert.Equal(3, (int)engine["mergedPositive"]!);
        Assert.Equal(1, (int)engine["mergedNegative"]!);
        Assert.Equal(1, (int)engine["newLane"]!);
        Assert.Equal(0, (int)engine["signedLane"]!);
        Assert.Equal(1.0, (double)engine["maximumAbsoluteDerivedWeight"]!);
        Assert.Equal(0.000244140625, (double)engine["maximumNegativeDerivedWeight"]!);
        Assert.Equal(4, (int)engine["storedSlot3NotTheEngineWeight"]!);
        Assert.Equal("ReverseEngineered", (string)engine["provenance"]!);
        // The packed decode itself is unchanged: its census still counts the stored fourth halves.
        Assert.Equal(2, (int)PrimitivePayload(document, 0)["packed"]!["weights"]!["sentinelSlot3ReadAsZero"]!);

        var stored = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(Read(bytes, Ps3).Document, "Body").SkinInfluences);
        Assert.NotEqual(Bits(lanes), Bits(stored.Weights));
        Assert.Equal(0.900146484375, (double)stored.Weights[28] + stored.Weights[29] + stored.Weights[30] + stored.Weights[31]);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     Without the platform option the packed platform is not established and X360 stands in as Assumed, so the reader
    ///     types the X360 engine lanes, exactly the lanes a declared X360 read gives, but records them with provenance
    ///     Assumed, platformAssumed true and the assumption as their platform source. A PS3 file therefore keeps its stored
    ///     lanes only when the shell passes the platform option (<c>--platform ps3</c>). Control: the declared X360 read
    ///     records ReverseEngineered and the option as its source; the declared PS3 read keeps the stored lanes.
    /// </summary>
    [Fact]
    public void EngineLanes_UnderTheAssumedPlatform_AreTheX360LanesWithAssumedProvenance()
    {
        var weights = NineVertices(
        [
            0.5f, 0.3f, 0.2f, 1f,
            0.25f, 0.25f, 0.25f, 0.25f,
            0.4f, 0f, 0f, 0.6f,
            0.3f, 0.7f, 0f, 1f,
            1f, 0f, 0f, 0f,
            0.5f, 0.5f, 0f, 0f,
            0f, 0f, 0f, 1f,
            0.3f, 0.3f, 0f, 0.3f,
            1f, 0f, 0f, 1f / 16777216f
        ]);
        var bytes = new NifPackedSkinFixture { Data = weights, Partitions = ThreeAndSix }.Build();
        var assumed = Read(bytes).Document;
        var declared = Read(bytes, X360).Document;
        var lanes = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(assumed, "Body").SkinInfluences);
        var x360 = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(declared, "Body").SkinInfluences);

        Assert.Equal(x360.JointIndices, lanes.JointIndices);
        Assert.Equal(Bits(x360.Weights.ToArray()), Bits(lanes.Weights.ToArray()));
        var facts = PrimitivePayload(assumed, 0)["skin"]!["influences"]!;
        Assert.Equal("engine", (string)facts["lanes"]!);
        Assert.Equal("x360", (string)facts["platform"]!);
        Assert.True((bool)facts["platformAssumed"]!);
        Assert.Equal(NifPackedEngineLanes.Rule, (string)facts["rule"]!);
        var engine = facts["engineLanes"]!;
        Assert.Equal(nameof(SceneValueProvenance.Assumed), (string)engine["provenance"]!);
        Assert.Equal(NifPackedPlatformOption.AssumedEvidence, (string)engine["platformSource"]!);

        var declaredEngine = PrimitivePayload(declared, 0)["skin"]!["influences"]!["engineLanes"]!;
        Assert.Equal(nameof(SceneValueProvenance.ReverseEngineered), (string)declaredEngine["provenance"]!);
        Assert.Equal($"{BethesdaModelRegistration.PlatformOption}=x360", (string)declaredEngine["platformSource"]!);
        Assert.False((bool)PrimitivePayload(declared, 0)["skin"]!["influences"]!["platformAssumed"]!);
        var ps3 = PrimitivePayload(Read(bytes, Ps3).Document, 0)["skin"]!["influences"]!;
        Assert.Equal("stored", (string)ps3["lanes"]!);
        Assert.Null(ps3["engineLanes"]);
        SceneValidation.ValidateStructure(assumed);
    }

    /// <summary>
    ///     The exact binary16 halves of nv_ncr_flag's vertex 44 (0.55859375, 0.31884765625, 0.1229248046875, Float32 sum
    ///     1.0003662109375) through the whole reader, with a partition whose Bones are [0, 1, 2]: the slot-3 byte naming
    ///     bone 1 merges r = -3 * 2^-13 into the joint-1 lane. A two-lane vertex (0.55859375, 0.44189453125) whose slot-3
    ///     byte names the unused bone 2 keeps r = -2^-11 as a signed lane on joint 2, and nv_ncr_flag's vertex 31 halves
    ///     (0.7998046875, 0.199951171875) get a new lane of +2^-12 on joint 2. Every vertex sums to exactly one. Control:
    ///     the PS3 reading of the same bytes keeps the stored lanes, and vertex 0 then sums to 1.0003662109375.
    /// </summary>
    [Fact]
    public void EngineLanes_TheFlagHalves_ThroughTheReader_OnX360()
    {
        float[] stored =
        [
            0.55859375f, 0.31884765625f, 0.1229248046875f, 0f,
            0.55859375f, 0.44189453125f, 0f, 0f,
            0.7998046875f, 0.199951171875f, 0f, 0f
        ];
        byte[] bones = [0, 1, 2, 1, 0, 1, 0, 2, 0, 1, 0, 2];
        var partitions = new[]
        {
            new NifTestSkinPartition
            {
                VertexCount = 3, Bones = [0, 1, 2], WeightsPerVertex = 4, VertexMap = [0, 1, 2], Triangles = [0, 1, 2]
            }
        };
        var bytes = new NifPackedSkinFixture { Data = ThreeVertices(stored, bones), Partitions = partitions, DeclaredVertices = 3 }
            .Build();
        var document = Read(bytes, X360).Document;
        var influences = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(document, "Body").SkinInfluences);

        Assert.Equal([0, 1, 2, 0, 0, 1, 0, 2, 0, 1, 0, 2], influences.JointIndices);
        Assert.Equal(Bits(
        [
            0.55859375f, 0.3184814453125f, 0.1229248046875f, 0f,
            0.55859375f, 0.44189453125f, 0f, -0.00048828125f,
            0.7998046875f, 0.199951171875f, 0f, 0.000244140625f
        ]), Bits(influences.Weights));
        for (var vertex = 0; vertex < 3; vertex++)
        {
            Assert.Equal(1.0, influences.Weights.Skip(vertex * 4).Take(4).Sum(static weight => (double)weight));
        }

        var engine = PrimitivePayload(document, 0)["skin"]!["influences"]!["engineLanes"]!;
        Assert.Equal(1, (int)engine["mergedNegative"]!);
        Assert.Equal(1, (int)engine["signedLane"]!);
        Assert.Equal(1, (int)engine["newLane"]!);
        Assert.Equal(0.00048828125, (double)engine["maximumNegativeDerivedWeight"]!);
        Assert.Equal(1, (int)PrimitivePayload(document, 0)["skin"]!["influences"]!["negativeWeights"]!);

        var ps3 = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(Read(bytes, Ps3).Document, "Body").SkinInfluences);
        Assert.Equal(1.0003662109375, ps3.Weights.Take(4).Sum(static weight => (double)weight));
        Assert.Equal([0, 1, 2, 0], ps3.JointIndices.Take(4));
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A skinned shape's vertex domain is the concatenated partition vertex maps: six primitive vertices for a
    ///     four-vertex shape, point indices [0, 1, 2, 2, 3, 1] over four points, and each partition's triangles offset
    ///     by its first vertex. The skin, its joints and every skin block are typed. Control: a welding reader would
    ///     emit four vertices and the shape-domain triangles, which the assertions reject.
    /// </summary>
    [Fact]
    public void SkinnedShape_UsesThePartitionOrder_WithPointIndicesAndOffsetTriangles()
    {
        var result = Read(new NifPackedSkinFixture().Build(), X360);
        var document = result.Document;
        var primitive = PrimitiveOf(document, "Body");

        Assert.Equal(6, primitive.Vertices.Count);
        var points = Assert.IsType<ScenePointIndices>(primitive.PointIndices);
        Assert.Equal(4, points.PointCount);
        Assert.Equal([0, 1, 2, 2, 3, 1], points.Values);
        Assert.Equal([0, 1, 2, 3, 4, 5], primitive.Indices);
        Assert.Equal(primitive.Vertices[2].Position, primitive.Vertices[3].Position);
        Assert.Equal(primitive.Vertices[1].Position, primitive.Vertices[5].Position);
        Assert.NotEqual(4, primitive.Vertices.Count);
        Assert.NotEqual([0, 1, 2, 2, 3, 1], primitive.Indices);

        var skin = Assert.Single(document.Skins);
        Assert.Equal(0, document.Nodes[4].SkinIndex);
        Assert.Equal([1, 2, 3], skin.JointNodeIndices);
        Assert.All(new[] { 1, 2, 3 }, node => Assert.Equal(SceneNodeRole.Joint, document.Nodes[node].Role));
        foreach (var block in new[] { 4, 5, 6, 7, 8, 9 })
        {
            Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification($"block:{block}").Kind);
        }

        var payload = PrimitivePayload(document, 0);
        Assert.Equal("partition order", (string)payload["vertexOrder"]!["form"]!);
        Assert.Equal([0, 3], payload["vertexOrder"]!["partitionStarts"]!.AsArray().Select(v => (int)v!).ToArray());
        Assert.Equal("partitions", (string)payload["triangles"]!["form"]!);
        Assert.Equal(2, (int)payload["triangles"]!["kept"]!);
        Assert.True((bool)payload["skin"]!["partitionFeedsTypedState"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A dismember instance's body parts become face streams over the packed triangles, one entry per partition in
    ///     partition order. Control: swapping the two entries changes the stream.
    /// </summary>
    [Fact]
    public void DismemberBodyParts_FollowThePartitionOrder()
    {
        var document = Read(new NifPackedSkinFixture { Dismember = true }.Build(), X360).Document;
        var primitive = PrimitiveOf(document, "Body");

        var faces = Assert.IsType<SceneFaceList>(primitive.Faces);
        Assert.Equal(2, faces.FaceCount);
        var bodyParts = Assert.Single(primitive.Attributes, a => a.Name == NifModelDismemberFaces.BodyPartAttribute);
        Assert.Equal([32, 33], UInt16s(bodyParts));

        var swapped = Read(new NifPackedSkinFixture
        {
            Dismember = true,
            BodyParts = new (ushort, ushort)[] { (1, 33), (1, 32) }
        }.Build(), X360).Document;
        var swappedParts = Assert.Single(PrimitiveOf(swapped, "Body").Attributes,
            a => a.Name == NifModelDismemberFaces.BodyPartAttribute);
        Assert.Equal([33, 32], UInt16s(swappedParts));
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A static NiTriStrips keeps its strips inline: the identity domain, the strip rule's triangles and no point
    ///     indices.
    /// </summary>
    [Fact]
    public void StaticStrips_KeepTheIdentityDomain()
    {
        var document = Read(new NifPackedFixture { Layout = "L2", Strips = true }.Build(), X360).Document;
        var primitive = PrimitiveOf(document, "Shape");

        Assert.Null(primitive.PointIndices);
        Assert.Equal([0, 1, 2, 1, 3, 2], primitive.Indices);
        Assert.Equal("identity", (string)PrimitivePayload(document, 0)["vertexOrder"]!["form"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A stream table outside the six layouts (here L2 with its UV channel first) is native state with the "packed
    ///     layout unknown" reason on the shape, its data and the packed block, and a diagnostic; nothing is guessed.
    ///     Control: the measured order of the same channels is typed.
    /// </summary>
    [Fact]
    public void UnknownStreamTable_IsNativeOnly_WithTheLayoutReason()
    {
        var data = NifPackedFixture.Quad();
        var swapped = NifTestPackedLayouts.StreamTable("L2").Select(s => (s.Type, s.UnitSize, s.Offset)).ToArray();
        (swapped[1], swapped[2]) = (swapped[2], swapped[1]);
        var result = Read(new NifPackedFixture
        {
            CustomPacked = w => NifTestBlockLayouts.PackedAdditionalGeometryData(w, 4, swapped, 36,
                NifTestPackedLayouts.Payload("L2", data), 124)
        }.Build());

        Assert.Empty(result.Document.Meshes);
        foreach (var block in new[] { 1, 2, 3 })
        {
            var row = result.Coverage.GetClassification($"block:{block}");
            Assert.Equal(ModelSourceCoverageKind.NativeOnly, row.Kind);
            Assert.Equal(NifModelCoverage.PackedLayoutUnknownReason, row.Reason);
        }

        var diagnostic = Assert.Single(result.Document.Diagnostics,
            d => d.Code == NifPackedGeometryReader.NotTypedDiagnostic);
        Assert.Contains("matches none of the six measured layouts", DiagnosticText(diagnostic));
        SceneValidation.ValidateStructure(result.Document);

        Assert.Single(Read(new NifPackedFixture { Layout = "L2", Data = data }.Build()).Document.Meshes);
    }

    /// <summary>
    ///     A packed count that is not the data block's Num Vertices (static) or the sum of the partition counts
    ///     (skinned), a channel table whose flags are not the measured 2, and a skinned shape whose layout carries no
    ///     weights are each native state with their precise reason. Control: the consistent fixtures are typed.
    /// </summary>
    [Fact]
    public void InconsistentCounts_AndUnmeasuredForms_AreNativeOnly()
    {
        var staticMismatch = Read(new NifPackedFixture { Layout = "L2", DeclaredVertices = 5 }.Build());
        AssertReason(staticMismatch, [1, 2, 3], NifModelCoverage.PackedPayloadReason);

        var five = NifPackedSkinFixture.SixVertices();
        var skinnedMismatch = Read(new NifPackedSkinFixture
        {
            Data = new NifTestPackedVertexData
            {
                Positions = five.Positions[..15],
                Normals = five.Normals[..15],
                Uvs = five.Uvs[..10],
                Tangents = five.Tangents[..15],
                Bitangents = five.Bitangents[..15],
                Weights = five.Weights![..20],
                BoneIndices = five.BoneIndices![..20]
            }
        }.Build());
        AssertReason(skinnedMismatch, [4, 5, 6, 7, 8, 9], NifModelCoverage.PackedPayloadReason);

        var table = NifTestPackedLayouts.StreamTable("L2").Select(s => (s.Type, s.UnitSize, s.Offset)).ToArray();
        var wrongFlags = Read(new NifPackedFixture
        {
            CustomPacked = w => NifTestBlockLayouts.PackedAdditionalGeometryData(w, 4, table, 36,
                NifTestPackedLayouts.Payload("L2", NifPackedFixture.Quad()), 124, flags: 0)
        }.Build());
        AssertReason(wrongFlags, [1, 2, 3], NifModelCoverage.PackedPayloadReason);

        var weightless = Read(new NifPackedSkinFixture { Layout = "L2", DeclaredVertices = 6 }.Build());
        AssertReason(weightless, [4, 5, 6, 7, 8, 9], NifModelCoverage.PackedSkinnedStaticLayoutReason);

        Assert.Single(Read(new NifPackedFixture { Layout = "L2" }.Build()).Document.Meshes);
        Assert.Single(Read(new NifPackedSkinFixture().Build()).Document.Meshes);
    }

    /// <summary>
    ///     A non-finite packed position is corrupt input naming the block, channel, vertex and offset, as an inline
    ///     non-finite position is. Control: the finite fixture reads.
    /// </summary>
    [Fact]
    public void NonFinitePackedPosition_IsCorrupt()
    {
        var data = NifPackedFixture.Quad();
        data.Positions[4] = float.PositiveInfinity;

        var error = Assert.Throws<InvalidDataException>(() =>
            Read(new NifPackedFixture { Layout = "L2", Data = data }.Build()));

        Assert.Contains("NIF block 3 (BSPackedAdditionalGeometryData), packed Position channel, vertex 1 component 1",
            error.Message);
        Assert.Contains("offset 0x", error.Message);
        Assert.Single(Read(new NifPackedFixture { Layout = "L2" }.Build()).Document.Meshes);
    }

    /// <summary>An option value that names no console is refused, never replaced by a default.</summary>
    [Fact]
    public void UnknownPlatformOption_IsRefused()
    {
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.PlatformOption] = "wii" };

        var error = Assert.Throws<ArgumentException>(() => Read(new NifPackedFixture { Layout = "L2" }.Build(), options));

        Assert.Contains("bmt.platform", error.Message);
    }

    /// <summary>
    ///     A shape vertex that two partitions both map must decode to the same position from both packed copies, as
    ///     Shared requires one position per source point: a second copy differing by one half ulp is native state with
    ///     the packed payload reason on the shape, its data, its skin blocks and the packed block, a not-typed
    ///     diagnostic naming the partition and the shape vertex, and a document Shared's structural validation accepts
    ///     (no primitive reaches it). Controls: the unedited fixture stays typed with point indices [0, 1, 2, 2, 3, 1]
    ///     and passes the validation; a copy differing only by the sign of zero stays typed (Shared compares floats, so
    ///     a bits-comparing implementation fails this control).
    /// </summary>
    [Fact]
    public void RepeatedShapeVertex_WithDifferingPackedPositions_IsNativeOnly()
    {
        // Packed vertex 3 is partition 1 local 0, the second copy of shape vertex 2 at (0, 1, 0): its y moved one half ulp.
        var edited = NifPackedSkinFixture.SixVertices();
        edited.Positions[3 * 3 + 1] = 1.0009765625f;

        var result = Read(new NifPackedSkinFixture { Data = edited }.Build(), X360);

        AssertReason(result, [4, 5, 6, 7, 8, 9], NifModelCoverage.PackedPayloadReason);
        Assert.Contains(result.Document.Diagnostics, d => d.Code == NifPackedGeometryReader.NotTypedDiagnostic &&
                                                          DiagnosticText(d).Contains(
                                                              "partition 1 vertex 0 (packed vertex 3) repeats shape vertex 2",
                                                              StringComparison.Ordinal));
        SceneValidation.ValidateStructure(result.Document);

        var intact = Read(new NifPackedSkinFixture().Build(), X360);
        var primitive = PrimitiveOf(intact.Document, "Body");
        Assert.Equal([0, 1, 2, 2, 3, 1], Assert.IsType<ScenePointIndices>(primitive.PointIndices).Values);
        Assert.Equal(ModelSourceCoverageKind.Typed, intact.Coverage.GetClassification("block:9").Kind);
        SceneValidation.ValidateStructure(intact.Document);

        var negativeZero = NifPackedSkinFixture.SixVertices();
        negativeZero.Positions[3 * 3] = BitConverter.UInt32BitsToSingle(0x80000000u);
        var signed = Read(new NifPackedSkinFixture { Data = negativeZero }.Build(), X360);
        var signedPrimitive = PrimitiveOf(signed.Document, "Body");
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(signedPrimitive.Vertices[3].Position.X));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(signedPrimitive.Vertices[2].Position.X));
        Assert.Equal(ModelSourceCoverageKind.Typed, signed.Coverage.GetClassification("block:9").Kind);
        Assert.DoesNotContain(signed.Document.Diagnostics, d => d.Code == NifPackedGeometryReader.NotTypedDiagnostic);
        SceneValidation.ValidateStructure(signed.Document);
    }

    /// <summary>
    ///     A packed block declaring zero vertices is the same authored condition as an inline data block with Num
    ///     Vertices 0, so it carries the same reason ("no drawable triangles") on every platform, with the detail naming
    ///     the zero count. Controls: an empty payload beside a non-zero count keeps the packed payload reason, and the
    ///     little-endian inline block with zero vertices reports the same constant.
    /// </summary>
    [Fact]
    public void ZeroDeclaredVertices_IsEmptyGeometry_AsInline()
    {
        var zero = new NifTestPackedVertexData { Positions = [], Normals = [], Uvs = [], Tangents = [], Bitangents = [] };

        var result = Read(new NifPackedFixture { Layout = "L2", Data = zero }.Build());

        AssertReason(result, [1, 2, 3], NifModelCoverage.EmptyGeometryReason);
        var diagnostic = Assert.Single(result.Document.Diagnostics, d => d.Code == NifPackedGeometryReader.NotTypedDiagnostic);
        Assert.Contains("declares zero vertices", DiagnosticText(diagnostic));

        var table = NifTestPackedLayouts.StreamTable("L2").Select(s => (s.Type, s.UnitSize, s.Offset)).ToArray();
        var emptyPayload = Read(new NifPackedFixture
        {
            CustomPacked = w => NifTestBlockLayouts.PackedAdditionalGeometryData(w, 4, table, 36, [], 124)
        }.Build());
        AssertReason(emptyPayload, [1, 2, 3], NifModelCoverage.PackedPayloadReason);
        Assert.Contains(emptyPayload.Document.Diagnostics, d => d.Code == NifPackedGeometryReader.NotTypedDiagnostic &&
                                                                DiagnosticText(d).Contains("payload is empty although it declares 4 vertices",
                                                                    StringComparison.Ordinal));

        var inline = Read(SingleTriShape(new NifTestGeometryStreams { Vertices = [], HasVertices = false, NumVertices = 0 },
            []));
        var row = inline.Coverage.GetClassification("block:2");
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, row.Kind);
        Assert.Equal(NifModelCoverage.EmptyGeometryReason, row.Reason);
    }

    /// <summary>
    ///     Two partitions over the four-vertex shape of <see cref="NifPackedSkinFixture.TwoPartitions" />, the second
    ///     mapping six vertices [2, 3, 1, 0, 3, 1], so the packed order holds nine vertices; used with
    ///     <see cref="NineVertices" />.
    /// </summary>
    private static NifTestSkinPartition[] ThreeAndSix =>
    [
        new NifTestSkinPartition
        {
            VertexCount = 3, Bones = [0, 1], WeightsPerVertex = 4, VertexMap = [0, 1, 2], Triangles = [0, 1, 2]
        },
        new NifTestSkinPartition
        {
            VertexCount = 6, Bones = [2, 1], WeightsPerVertex = 4, VertexMap = [2, 3, 1, 0, 3, 1], Triangles = [0, 1, 2]
        }
    ];

    /// <summary>The nine packed vertices of <see cref="ThreeAndSix" /> (a point keeps one position), with the given weights.</summary>
    private static NifTestPackedVertexData NineVertices(float[] weights)
    {
        float[] shape = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0.5f];
        int[] points = [0, 1, 2, 2, 3, 1, 0, 3, 1];
        var positions = new float[27];
        var normals = new float[27];
        var tangents = new float[27];
        var bitangents = new float[27];
        var uvs = new float[18];
        var bones = new byte[36];
        for (var i = 0; i < 9; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                positions[i * 3 + c] = shape[points[i] * 3 + c];
            }

            normals[i * 3 + 2] = 1f;
            tangents[i * 3] = 1f;
            bitangents[i * 3 + 1] = i % 2 == 0 ? 1f : -1f;
            uvs[i * 2] = points[i] * 0.25f;
            uvs[i * 2 + 1] = 0.5f;
            bones[i * 4 + 1] = 1;
        }

        return new NifTestPackedVertexData
        {
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Tangents = tangents,
            Bitangents = bitangents,
            Weights = weights,
            BoneIndices = bones
        };
    }

    /// <summary>
    ///     Three packed vertices at (0, 0, 0), (1, 0, 0) and (0, 1, 0) for a single three-vertex partition, with the given
    ///     weights and bone-index bytes (slot order).
    /// </summary>
    private static NifTestPackedVertexData ThreeVertices(float[] weights, byte[] bones)
    {
        float[] positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f];
        var normals = new float[9];
        var tangents = new float[9];
        var bitangents = new float[9];
        var uvs = new float[6];
        for (var i = 0; i < 3; i++)
        {
            normals[i * 3 + 2] = 1f;
            tangents[i * 3] = 1f;
            bitangents[i * 3 + 1] = 1f;
            uvs[i * 2] = i * 0.25f;
            uvs[i * 2 + 1] = 0.5f;
        }

        return new NifTestPackedVertexData
        {
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Tangents = tangents,
            Bitangents = bitangents,
            Weights = weights,
            BoneIndices = bones
        };
    }

    private static (byte[] Bytes, NifTestPackedVertexData Data, int Shape, int DataBlock, int Packed) Build(string layout)
    {
        if (NifTestPackedLayouts.IsSkinned(layout))
        {
            var data = NifPackedSkinFixture.SixVertices(layout == "L4" ? SkinColors() : null);
            return (new NifPackedSkinFixture { Layout = layout, Data = data }.Build(), data,
                NifPackedSkinFixture.ShapeBlock, NifPackedSkinFixture.DataBlock, NifPackedSkinFixture.PackedBlock);
        }

        var quad = NifPackedFixture.Quad(NifTestPackedLayouts.HasColors(layout) ? NifPackedFixture.QuadColors : null);
        return (new NifPackedFixture { Layout = layout, Data = quad }.Build(), quad, NifPackedFixture.ShapeBlock,
            NifPackedFixture.DataBlock, NifPackedFixture.PackedBlock);
    }

    private static byte[] SkinColors()
    {
        return [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 255, 255, 40, 80, 120, 200, 0, 255, 0, 255];
    }

    private static void AssertReason(ModelReadResult result, int[] blocks, string reason)
    {
        Assert.Empty(result.Document.Meshes);
        foreach (var block in blocks)
        {
            var row = result.Coverage.GetClassification($"block:{block}");
            Assert.Equal(ModelSourceCoverageKind.NativeOnly, row.Kind);
            Assert.Equal(reason, row.Reason);
        }

        Assert.Contains(result.Document.Diagnostics, d => d.Code == NifPackedGeometryReader.NotTypedDiagnostic);
        SceneValidation.ValidateStructure(result.Document);
    }

    /// <summary>The expected decoded vector: the half round trip of each component, or the float itself for a float3 frame.</summary>
    private static Vector3 Rounded(float[] values, int vertex, bool half)
    {
        return half
            ? new Vector3(NifTestPackedLayouts.RoundTrip(values[vertex * 3]),
                NifTestPackedLayouts.RoundTrip(values[vertex * 3 + 1]), NifTestPackedLayouts.RoundTrip(values[vertex * 3 + 2]))
            : new Vector3(values[vertex * 3], values[vertex * 3 + 1], values[vertex * 3 + 2]);
    }

    /// <summary>A lossy widening: the half's two low mantissa bits dropped before the exact widening (the control).</summary>
    private static float Truncated(float value)
    {
        return Truncated(NifTestPackedLayouts.HalfBits(value));
    }

    private static float Truncated(ushort bits)
    {
        return (float)BitConverter.UInt16BitsToHalf((ushort)(bits & 0xFFFC));
    }

    private static void AssertBits(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.X), BitConverter.SingleToUInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Y), BitConverter.SingleToUInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Z), BitConverter.SingleToUInt32Bits(actual.Z));
    }

    private static uint[] Bits(IEnumerable<float> values)
    {
        return values.Select(BitConverter.SingleToUInt32Bits).ToArray();
    }

    private static int[] UInt16s(SceneAttributeStream stream)
    {
        var data = stream.CopyContent();
        var values = new int[stream.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = data[i * 2] | (data[i * 2 + 1] << 8);
        }

        return values;
    }
}
