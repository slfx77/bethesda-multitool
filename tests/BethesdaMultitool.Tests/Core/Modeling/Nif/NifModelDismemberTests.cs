using System.Buffers.Binary;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 7, BSDismemberSkinInstance: each partition's body part and part flag become Face-domain UInt16 streams over
///     Faces = the primitive's triangles, matched to the partition triangles by shape vertex indices with winding kept.
///     Anything short of an exact match stays native with a diagnostic, and the skin itself is still typed.
/// </summary>
public class NifModelDismemberTests
{
    /// <summary>
    ///     Partition 0 holds the quad's second triangle through the vertex map [3, 1, 2] (local (1, 0, 2) is shape
    ///     (1, 3, 2)); partition 1 holds the first. So the faces read body parts 9 then 7, not the partition order.
    /// </summary>
    private static NifSkinFixture Fixture(bool bigEndian = false, (ushort, ushort)[]? bodyParts = null,
        ushort[]? firstPartitionTriangles = null)
    {
        return new NifSkinFixture
        {
            BigEndian = bigEndian,
            Dismember = true,
            BodyParts = bodyParts ?? new (ushort, ushort)[] { (0x0100, 7), (0x0001, 9) },
            Partitions =
            [
                new NifTestSkinPartition
                {
                    VertexCount = 3,
                    VertexMap = [3, 1, 2],
                    Triangles = firstPartitionTriangles ?? new ushort[] { 1, 0, 2 }
                },
                new NifTestSkinPartition { VertexCount = 3, VertexMap = [0, 1, 2], Triangles = [0, 1, 2] }
            ]
        };
    }

    /// <summary>
    ///     The streams hold each face's own partition values, the part flag exactly as stored (it is little-endian in
    ///     big-endian files, so both byte orders read 0x0001 and 0x0100). Control: assigning partitions to faces by
    ///     ordinal would give body parts 7 then 9.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BodyParts_BecomeFaceStreams_MatchedByTriangle(bool bigEndian)
    {
        var result = Read(Fixture(bigEndian).Build());
        var primitive = PrimitiveOf(result.Document, "Body");

        var faces = Assert.IsType<SceneFaceList>(primitive.Faces);
        Assert.Equal([3, 3], faces.FaceSizes);
        Assert.Equal([0, 1, 2, 1, 3, 2], faces.CornerIndices);
        Assert.Null(primitive.PrimaryColorAttributeIndex);
        Assert.Equal(2, primitive.Attributes.Count);

        var bodyPart = primitive.Attributes[0];
        Assert.Equal(NifModelDismemberFaces.BodyPartAttribute, bodyPart.Name);
        Assert.Equal(SceneAttributeDomain.Face, bodyPart.Domain);
        Assert.Equal(SceneAttributeComponentType.UInt16, bodyPart.ComponentType);
        Assert.Equal(new ushort[] { 9, 7 }, UInt16s(bodyPart));
        Assert.NotEqual(new ushort[] { 7, 9 }, UInt16s(bodyPart));

        var partFlag = primitive.Attributes[1];
        Assert.Equal(NifModelDismemberFaces.PartFlagAttribute, partFlag.Name);
        Assert.Equal(SceneAttributeDomain.Face, partFlag.Domain);
        Assert.Equal(new ushort[] { 0x0001, 0x0100 }, UInt16s(partFlag));

        Assert.NotNull(primitive.SkinInfluences);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:8").Kind);
        Assert.True((bool)PrimitivePayload(result.Document, 0)["skin"]!["dismember"]!["typed"]!);
        SceneValidation.ValidateStructure(result.Document);
    }

    /// <summary>
    ///     Case 0, a body-part list that does not cover the partitions; case 1, a primitive triangle no partition stores;
    ///     case 2, a partition storing the triangle with the opposite winding. None can be typed exactly: no faces, no
    ///     streams, a diagnostic, the partition stays NativeOnly, and the skin is still typed. Control: the exact fixture
    ///     types the streams.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InexactBodyParts_StayNative_WithADiagnostic(int inexactCase)
    {
        var (bodyParts, firstPartitionTriangles) = inexactCase switch
        {
            0 => (new (ushort, ushort)[] { (0x0100, 7) }, new ushort[] { 1, 0, 2 }),
            1 => (new (ushort, ushort)[] { (0x0100, 7), (0x0001, 9) }, Array.Empty<ushort>()),
            _ => (new (ushort, ushort)[] { (0x0100, 7), (0x0001, 9) }, new ushort[] { 0, 1, 2 })
        };

        var result = Read(Fixture(bodyParts: bodyParts, firstPartitionTriangles: firstPartitionTriangles).Build());
        var primitive = PrimitiveOf(result.Document, "Body");

        Assert.Null(primitive.Faces);
        Assert.Empty(primitive.Attributes);
        Assert.Contains(result.Document.Diagnostics, d => d.Code == NifModelSkinReader.DismemberDiagnostic);
        var partition = result.Coverage.GetClassification("block:8");
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, partition.Kind);
        Assert.Equal(NifModelCoverage.PartitionNativeReason, partition.Reason);
        Assert.Equal(0, result.Document.Nodes[4].SkinIndex);
        Assert.False((bool)PrimitivePayload(result.Document, 0)["skin"]!["dismember"]!["typed"]!);
        SceneValidation.ValidateStructure(result.Document);

        var exact = PrimitiveOf(Read(Fixture().Build()).Document, "Body");
        Assert.NotNull(exact.Faces);
    }

    private static ushort[] UInt16s(SceneAttributeStream stream)
    {
        var bytes = stream.CopyContent();
        var values = new ushort[stream.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        }

        return values;
    }
}
