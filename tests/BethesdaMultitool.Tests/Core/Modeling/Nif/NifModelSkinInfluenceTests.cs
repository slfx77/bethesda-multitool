using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 7, the vertex influences: from NiSkinData's per-bone weights exactly as authored (bone order, stride = the
///     most entries of any vertex, padding (0, 0.0), no sorting, pruning or normalization); from NiSkinPartition when
///     NiSkinData stores none; and no typed skin at all when neither does. PC partitions are native state with
///     consistency facts unless they feed typed state.
/// </summary>
public class NifModelSkinInfluenceTests
{
    /// <summary>
    ///     Vertex 0 has three entries summing to 0.875, vertex 1 and 2 one each, vertex 3 two in bone order. Controls: a
    ///     normalizing implementation changes vertex 0's weights, sorting by weight swaps vertex 3's joints, and the
    ///     four-slot glTF layout would hold 16 slots instead of 12.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Weights_AreKeptAsAuthored_InBoneOrder_WithStrideAndPadding(bool bigEndian)
    {
        var document = Read(new NifSkinFixture { BigEndian = bigEndian }.Build()).Document;
        var influences = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(document, "Body").SkinInfluences);

        Assert.Equal(3, influences.InfluencesPerVertex);
        Assert.Equal([0, 1, 2, 1, 0, 0, 0, 0, 0, 1, 2, 0], influences.JointIndices);
        float[] authored = [0.5f, 0.25f, 0.125f, 1f, 0f, 0f, 1f, 0f, 0f, 0.4f, 0.6f, 0f];
        Assert.Equal(Bits(authored), Bits(influences.Weights));

        var normalized = authored.ToArray();
        for (var k = 0; k < 3; k++)
        {
            normalized[k] /= 0.875f;
        }

        Assert.NotEqual(Bits(normalized), Bits(influences.Weights));
        Assert.NotEqual([2, 1], influences.JointIndices.Skip(9).Take(2));
        Assert.NotEqual(16, influences.JointIndices.Count);

        var facts = PrimitivePayload(document, 0)["skin"]!["influences"]!;
        Assert.Equal("NiSkinData", (string)facts["source"]!);
        Assert.Equal(5L, (long)facts["paddedSlots"]!);
        Assert.Equal(1, (int)facts["verticesWhoseSumIsNotOneWithin1e-4"]!);
        Assert.False((bool)facts["normalized"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     With Has Vertex Weights 0 the partition's rows are the influences, zero-weight slots included, joints through
    ///     the partition's Bones, and the partition is Typed. Control: the same file with Has Vertex Weights 1 takes the
    ///     NiSkinData weights (stride 3) instead.
    /// </summary>
    [Fact]
    public void NoSkinDataWeights_TakeThePartitionWeights()
    {
        NifTestSkinPartition[] partitions =
        [
            new NifTestSkinPartition
            {
                VertexCount = 4,
                Bones = [0, 2],
                WeightsPerVertex = 2,
                VertexMap = [0, 1, 2, 3],
                Weights = [[0.7f, 0.3f], [1f, 0f], [0.5f, 0.5f], [0.9f, 0f]],
                BoneIndices = [[0, 1], [0, 1], [1, 0], [1, 0]],
                Triangles = [0, 1, 2, 1, 3, 2]
            }
        ];

        var result = Read(new NifSkinFixture { HasVertexWeights = false, Partitions = partitions }.Build());
        var influences = Assert.IsType<SceneSkinInfluences>(PrimitiveOf(result.Document, "Body").SkinInfluences);

        Assert.Equal(2, influences.InfluencesPerVertex);
        Assert.Equal([0, 2, 0, 2, 2, 0, 2, 0], influences.JointIndices);
        Assert.Equal(Bits([0.7f, 0.3f, 1f, 0f, 0.5f, 0.5f, 0.9f, 0f]), Bits(influences.Weights));
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:8").Kind);
        Assert.Equal("NiSkinPartition",
            (string)PrimitivePayload(result.Document, 0)["skin"]!["influences"]!["source"]!);
        SceneValidation.ValidateStructure(result.Document);

        var control = Read(new NifSkinFixture { HasVertexWeights = true, Partitions = partitions }.Build()).Document;
        Assert.Equal(3, PrimitiveOf(control, "Body").SkinInfluences!.InfluencesPerVertex);
    }

    /// <summary>
    ///     A vertex that two partitions share keeps the first partition's row when both give it the same non-zero
    ///     influences, even though their unused slots name different joints (each partition's own Bones[0]) and they
    ///     store different Num Weights Per Vertex. Control: the same shared vertex with a different weight in the second
    ///     partition is a real conflict, and the skin is not typed.
    /// </summary>
    [Theory]
    [InlineData(0.6f, true)]
    [InlineData(0.5f, false)]
    public void SharedPartitionVertex_ComparesOnlyTheNonZeroInfluences(float laterWeight, bool typed)
    {
        NifTestSkinPartition[] partitions =
        [
            new NifTestSkinPartition
            {
                VertexCount = 3,
                Bones = [0, 2],
                WeightsPerVertex = 3,
                VertexMap = [0, 1, 2],
                Weights = [[0.6f, 0.4f, 0f], [1f, 0f, 0f], [1f, 0f, 0f]],
                BoneIndices = [[0, 1, 0], [0, 0, 0], [1, 0, 0]],
                Triangles = [0, 1, 2]
            },
            new NifTestSkinPartition
            {
                VertexCount = 3,
                Bones = [2, 0],
                WeightsPerVertex = 2,
                VertexMap = [0, 2, 3],
                Weights = [[0.4f, laterWeight], [1f, 0f], [1f, 0f]],
                BoneIndices = [[0, 1], [0, 0], [1, 0]],
                Triangles = [0, 2, 1]
            }
        ];

        var result = Read(new NifSkinFixture { HasVertexWeights = false, Partitions = partitions }.Build());
        var influences = PrimitiveOf(result.Document, "Body").SkinInfluences;

        Assert.Equal(typed, influences is not null);
        if (influences is not null)
        {
            Assert.Equal(3, influences.InfluencesPerVertex);
            Assert.Equal([0, 2, 0], influences.JointIndices.Take(3));
            Assert.Equal(Bits([0.6f, 0.4f, 0f]), Bits(influences.Weights.Take(3).ToArray()));
            SceneValidation.ValidateStructure(result.Document);
        }
        else
        {
            Assert.Equal(NifModelCoverage.SkinNoInfluencesReason, result.Coverage.GetClassification("block:6").Reason);
        }
    }

    /// <summary>
    ///     With no weights anywhere the skin is not typed: no palette, no skin index, no influences, a diagnostic, and the
    ///     skin blocks NativeOnly with the reason; the geometry stays placed in bind space and the bones keep the Transform
    ///     role. Control: the default fixture types the skin and gives its bones the Joint role.
    /// </summary>
    [Fact]
    public void NoWeightsAnywhere_LeavesTheGeometryUnskinned_AndReportsIt()
    {
        var result = Read(new NifSkinFixture { HasVertexWeights = false }.Build());
        var document = result.Document;

        Assert.Empty(document.Skins);
        Assert.Null(document.Nodes[4].SkinIndex);
        Assert.Equal(0, document.Nodes[4].MeshIndex);
        Assert.Null(PrimitiveOf(document, "Body").SkinInfluences);
        Assert.Contains(document.Diagnostics, d => d.Code == NifModelSkinReader.NotTypedDiagnostic);
        Assert.Equal(SceneNodeRole.Transform, document.Nodes[1].Role);
        foreach (var identity in new[] { "block:6", "block:7" })
        {
            var row = result.Coverage.GetClassification(identity);
            Assert.Equal(ModelSourceCoverageKind.NativeOnly, row.Kind);
            Assert.Equal(NifModelCoverage.SkinNoInfluencesReason, row.Reason);
        }

        var skin = PrimitivePayload(document, 0)["skin"]!;
        Assert.False((bool)skin["typed"]!);
        SceneValidation.ValidateStructure(document);

        var control = Read(new NifSkinFixture().Build()).Document;
        Assert.Single(control.Skins);
        Assert.Equal(SceneNodeRole.Joint, control.Nodes[1].Role);
    }

    /// <summary>
    ///     With NiSkinData weights, a plain skin's partition feeds nothing typed: it is NativeOnly with the partition
    ///     reason and its agreement with the typed skin is native state. Here three partition rows equal NiSkinData's and
    ///     vertex 3's differs by 0.1. Control: the influences still come from NiSkinData (vertex 3 keeps 0.4 and 0.6).
    /// </summary>
    [Fact]
    public void Partition_OfASkinWithWeights_IsNative_WithConsistencyFacts()
    {
        NifTestSkinPartition[] partitions =
        [
            new NifTestSkinPartition
            {
                VertexCount = 4,
                Bones = [0, 1, 2],
                WeightsPerVertex = 3,
                VertexMap = [0, 1, 2, 3],
                Weights = [[0.5f, 0.25f, 0.125f], [1f, 0f, 0f], [1f, 0f, 0f], [0.5f, 0.5f, 0f]],
                BoneIndices = [[0, 1, 2], [1, 0, 0], [0, 0, 0], [1, 2, 0]],
                Triangles = [0, 1, 2, 1, 3, 2]
            }
        ];

        var result = Read(new NifSkinFixture { Partitions = partitions }.Build());
        var document = result.Document;

        var partition = result.Coverage.GetClassification("block:8");
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, partition.Kind);
        Assert.Equal(NifModelCoverage.PartitionNativeReason, partition.Reason);
        var facts = PrimitivePayload(document, 0)["skin"]!["partitions"]!;
        Assert.Equal(4, (int)facts["verticesMapped"]!);
        Assert.Equal(2, (int)facts["triangles"]!["foundInPartitions"]!);
        var weights = facts["weightsAgainstSkinData"]!;
        Assert.Equal(4, (int)weights["comparedPartitionVertices"]!);
        Assert.Equal(3, (int)weights["identical"]!);
        Assert.Equal(0.1, (double)weights["maximumAbsoluteDifference"]!, 1e-6);

        var influences = PrimitiveOf(document, "Body").SkinInfluences!;
        Assert.Equal(Bits([0.4f, 0.6f, 0f]), Bits(influences.Weights.Skip(9)));
        Assert.Equal(0, document.Nodes[4].SkinIndex);
        SceneValidation.ValidateStructure(document);
    }

    private static uint[] Bits(IEnumerable<float> values)
    {
        return values.Select(BitConverter.SingleToUInt32Bits).ToArray();
    }
}
