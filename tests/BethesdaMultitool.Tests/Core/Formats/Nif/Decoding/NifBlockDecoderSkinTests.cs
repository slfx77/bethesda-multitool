using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Nif.Decoding.NifDecodingTestSupport;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;

/// <summary>
///     NiSkinData (nif.xml:12102-12126, BoneData 6986-7009) and NiSkinPartition (12176-12194, SkinPartition
///     6681-6781) through the schema-driven decoder: the <c>arg</c> hand-off to BoneData, two-dimensional weight and
///     bone-index arrays, and jagged strips.
/// </summary>
public class NifBlockDecoderSkinTests
{
    private static void Transform(NifTestBlockWriter w, float tx, float scale)
    {
        w.F32s(NifTestBlockLayouts.Identity); // NiTransform: Rotation (Matrix33)
        w.F32s(tx, 0f, 0f); // Translation
        w.F32(scale); // Scale
    }

    private static NifTestFileBuilder SkinData(bool bigEndian, bool hasVertexWeights)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        builder.AddBlock("NiSkinData", w =>
        {
            Transform(w, 0f, 1f); // Skin Transform
            w.U32(2); // Num Bones
            w.Bool(hasVertexWeights); // Has Vertex Weights (the Bone List arg)

            Transform(w, 1.5f, 1f); // bone 0: Skin Transform
            w.F32s(0f, 0f, 0f, 2f); // Bounding Sphere
            w.U16(2); // Num Vertices
            if (hasVertexWeights)
            {
                w.U16(0).F32(1f).U16(0x0102).F32(0.25f); // Vertex Weights: Index, Weight
            }

            Transform(w, -1.5f, 0.5f); // bone 1
            w.F32s(1f, 0f, 0f, 1f);
            w.U16(1);
            if (hasVertexWeights)
            {
                w.U16(1).F32(0.75f);
            }
        });
        return builder;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiSkinData_WithVertexWeights_ReadsEveryBoneAndWeight(bool bigEndian)
    {
        var block = DecodeStrict(SkinData(bigEndian, true), 0);

        Assert.True(block.IsComplete);
        AssertInteger(block.Root, "Num Bones", 2);
        AssertInteger(block.Root, "Has Vertex Weights", 1);
        var bones = block.Root.Get<NifArrayValue>("Bone List").Items.Cast<NifStructValue>().ToList();
        Assert.Equal(2, bones.Count);

        AssertTriple(bones[0].Get<NifStructValue>("Skin Transform"), "Translation", ["x", "y", "z"], 1.5f, 0f, 0f);
        AssertFloat(bones[1].Get<NifStructValue>("Skin Transform"), "Scale", 0.5f);
        AssertFloat(bones[0].Get<NifStructValue>("Bounding Sphere"), "Radius", 2f);

        var weights = bones[0].Get<NifArrayValue>("Vertex Weights").Items.Cast<NifStructValue>().ToList();
        Assert.Equal(2, weights.Count);
        AssertInteger(weights[1], "Index", 0x0102);
        AssertFloat(weights[1], "Weight", 0.25f);
        var single = Assert.Single(bones[1].Get<NifArrayValue>("Vertex Weights").Items);
        AssertFloat(Assert.IsType<NifStructValue>(single), "Weight", 0.75f);
    }

    /// <summary>
    ///     With Has Vertex Weights = 0 (as on Xbox), BoneData still stores Num Vertices but no weights: the
    ///     <c>cond="#ARG# != 0"</c> reads the argument NiSkinData passes. Reading the weights anyway would run into
    ///     the next bone and fail the size check.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiSkinData_WithoutVertexWeights_SkipsTheWeightArrays(bool bigEndian)
    {
        var block = DecodeStrict(SkinData(bigEndian, false), 0);

        Assert.True(block.IsComplete);
        var bones = block.Root.Get<NifArrayValue>("Bone List").Items.Cast<NifStructValue>().ToList();
        AssertInteger(bones[0], "Num Vertices", 2);
        Assert.False(bones[0].Contains("Vertex Weights"));
        Assert.False(bones[1].Contains("Vertex Weights"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NiSkinPartition_ReadsTwoDimensionalAndJaggedArrays(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        builder.AddBlock("NiSkinPartition", w =>
        {
            w.U32(2); // Num Partitions

            // Partition 0: a triangle list.
            w.U16(3).U16(1).U16(2).U16(0).U16(2); // Num Vertices, Num Triangles, Num Bones, Num Strips, Weights/Vertex
            w.U16(0).U16(1); // Bones
            w.Bool(true).U16(0).U16(1).U16(0x0102); // Has Vertex Map, Vertex Map
            w.Bool(true).F32s(1f, 0f, 0.5f, 0.5f, 0f, 1f); // Has Vertex Weights, Vertex Weights[3][2]
            w.Bool(true); // Has Faces (no Strip Lengths: Num Strips = 0)
            w.U16(0).U16(1).U16(2); // Triangles
            w.Bool(true).U8(0).U8(1).U8(0).U8(1).U8(1).U8(0); // Has Bone Indices, Bone Indices[3][2]

            // Partition 1: one strip, no bone indices.
            w.U16(4).U16(2).U16(1).U16(1).U16(1);
            w.U16(1);
            w.Bool(true).U16(3).U16(4).U16(5).U16(6);
            w.Bool(true).F32s(1f, 1f, 1f, 1f);
            w.U16(4); // Strip Lengths
            w.Bool(true); // Has Faces
            w.U16(0).U16(1).U16(0x0203).U16(3); // Strips[1][Strip Lengths]
            w.Bool(false); // Has Bone Indices
        });

        var block = DecodeStrict(builder, 0);

        Assert.True(block.IsComplete);
        var partitions = block.Root.Get<NifArrayValue>("Partitions").Items.Cast<NifStructValue>().ToList();
        Assert.Equal(2, partitions.Count);

        var first = partitions[0];
        Assert.Equal(new ushort[] { 0, 1, 0x0102 }, first.Get<NifUInt16ArrayValue>("Vertex Map").Values.ToArray());
        var weightRows = first.Get<NifArrayValue>("Vertex Weights");
        Assert.True(weightRows.IsRows);
        Assert.Equal(3, weightRows.Count);
        AssertFloats(Assert.IsType<NifFloatArrayValue>(weightRows.Items[1]), 1, 0.5f, 0.5f);
        Assert.Equal(new ushort[] { 0, 1, 2 }, first.Get<NifUInt16ArrayValue>("Triangles").Values.ToArray());
        Assert.False(first.Contains("Strips"));
        var boneIndexRows = first.Get<NifArrayValue>("Bone Indices");
        Assert.Equal(new byte[] { 1, 0 }, Assert.IsType<NifByteArrayValue>(boneIndexRows.Items[2]).Bytes.ToArray());

        var second = partitions[1];
        Assert.False(second.Contains("Triangles"));
        Assert.False(second.Contains("Bone Indices"));
        var strips = second.Get<NifArrayValue>("Strips");
        Assert.True(strips.IsRows);
        Assert.Equal(new ushort[] { 0, 1, 0x0203, 3 },
            Assert.IsType<NifUInt16ArrayValue>(Assert.Single(strips.Items)).Values.ToArray());
    }
}
