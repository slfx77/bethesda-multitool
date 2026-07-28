using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     NiSkinInstance/NiSkinData layouts are version-gated (nif.xml): the instance's Skin Partition
///     ref exists only since 10.1.0.101, the data's Skin Partition ref exists 4.0.0.2–10.1.0.0, and
///     the data's Has Vertex Weights byte exists only since 4.2.1.0. Morrowind (4.0.0.2) misparsed on
///     all three before <see cref="NifSkinBlockParser" /> learned the gates — these encode the SAME
///     skin content in both era layouts and assert identical parse results.
/// </summary>
public class NifSkinBlockParserVersionTests
{
    private const uint Morrowind = 0x04000002; // 4.0.0.2
    private const uint Tes4 = 0x14000005; // 20.0.0.5 (Oblivion; 20.0.0.4 shares the gates)
    private const uint Modern = 0x14020007; // 20.2.0.7 (FO3+)

    private static readonly int[] ExpectedBoneRefs = [3, 5];

    [Fact]
    public void ParseNiSkinInstance_MorrowindLayout_HasNoPartitionRef()
    {
        // 4.0.0.2: Data(7) + SkeletonRoot(1) + NumBones(2) + bones[3, 5]
        var bytes = new List<byte>();
        AppendInt(bytes, 7);
        AppendInt(bytes, 1);
        AppendInt(bytes, 2);
        AppendInt(bytes, 3);
        AppendInt(bytes, 5);
        var data = bytes.ToArray();

        var parsed = NifSkinBlockParser.ParseNiSkinInstance(
            data, Block(data), false, Morrowind);

        Assert.NotNull(parsed);
        Assert.Equal(7, parsed.DataRef);
        Assert.Equal(1, parsed.SkeletonRootRef);
        Assert.Equal(ExpectedBoneRefs, parsed.BoneRefs);
        Assert.Equal(-1, parsed.SkinPartitionRef); // absent pre-10.1.0.101
    }

    [Fact]
    public void ParseNiSkinInstance_ModernLayout_ReadsPartitionRef()
    {
        // 20.2.0.7: Data(7) + SkinPartition(9) + SkeletonRoot(1) + NumBones(2) + bones[3, 5]
        var bytes = new List<byte>();
        AppendInt(bytes, 7);
        AppendInt(bytes, 9);
        AppendInt(bytes, 1);
        AppendInt(bytes, 2);
        AppendInt(bytes, 3);
        AppendInt(bytes, 5);
        var data = bytes.ToArray();

        var parsed = NifSkinBlockParser.ParseNiSkinInstance(
            data, Block(data), false, Modern);

        Assert.NotNull(parsed);
        Assert.Equal(7, parsed.DataRef);
        Assert.Equal(9, parsed.SkinPartitionRef);
        Assert.Equal(1, parsed.SkeletonRootRef);
        Assert.Equal(ExpectedBoneRefs, parsed.BoneRefs);
    }

    [Fact]
    public void ParseNiSkinInstance_Tes4Layout_ReadsPartitionRefLikeModern()
    {
        // 20.0.0.5 carries the instance-side partition ref (since 10.1.0.101) and no data-side
        // ref — the arena-spectator diagnosis leaned on this parse being byte-exact.
        var bytes = new List<byte>();
        AppendInt(bytes, 7);
        AppendInt(bytes, 9);
        AppendInt(bytes, 1);
        AppendInt(bytes, 2);
        AppendInt(bytes, 3);
        AppendInt(bytes, 5);
        var data = bytes.ToArray();

        var parsed = NifSkinBlockParser.ParseNiSkinInstance(
            data, Block(data), false, Tes4);

        Assert.NotNull(parsed);
        Assert.Equal(7, parsed.DataRef);
        Assert.Equal(9, parsed.SkinPartitionRef);
        Assert.Equal(1, parsed.SkeletonRootRef);
        Assert.Equal(ExpectedBoneRefs, parsed.BoneRefs);
    }

    [Theory]
    [InlineData(Morrowind)]
    [InlineData(Tes4)]
    [InlineData(Modern)]
    public void ParseNiSkinData_BothEraLayouts_YieldTheSameBonesAndWeights(uint version)
    {
        var data = BuildSkinData(version);

        var parsed = NifSkinBlockParser.ParseNiSkinData(data, Block(data), false, version);

        Assert.NotNull(parsed);
        Assert.True(parsed.HasVertexWeights); // Morrowind implies it; modern reads the byte
        Assert.Equal(2, parsed.Bones.Length);

        // The inverse bind translations distinguish the bones — if a version gate misfires the
        // stream desyncs and these become garbage (or the parse fails outright).
        Assert.Equal(-30.7f, parsed.Bones[0].InverseBindPose.M43, 3);
        Assert.Equal(-41.0f, parsed.Bones[1].InverseBindPose.M43, 3);

        Assert.Equal(2, parsed.Bones[0].VertexWeights.Length);
        Assert.Equal((ushort)0, parsed.Bones[0].VertexWeights[0].VertexIndex);
        Assert.Equal(0.75f, parsed.Bones[0].VertexWeights[0].Weight, 3);
        Assert.Equal((ushort)1, parsed.Bones[0].VertexWeights[1].VertexIndex);
        Assert.Equal(0.25f, parsed.Bones[0].VertexWeights[1].Weight, 3);
        var single = Assert.Single(parsed.Bones[1].VertexWeights);
        Assert.Equal((ushort)1, single.VertexIndex);
        Assert.Equal(1.0f, single.Weight, 3);
    }

    /// <summary>
    ///     Same skin content in each era's byte layout: overall transform + 2 bones with
    ///     distinct bind translations and small weight lists.
    /// </summary>
    private static byte[] BuildSkinData(uint version)
    {
        var bytes = new List<byte>();
        AppendTransform(bytes, 0f);
        AppendInt(bytes, 2); // num bones
        if (version is >= 0x04000002 and <= 0x0A010000)
        {
            AppendInt(bytes, -1); // NiSkinData Skin Partition ref (4.0.0.2 – 10.1.0.0 only)
        }

        if (version >= 0x04020100)
        {
            bytes.Add(1); // Has Vertex Weights byte (since 4.2.1.0 only)
        }

        // Bone 0: bind translation Z -30.7, two weights.
        AppendTransform(bytes, -30.7f);
        AppendBoundingSphere(bytes);
        AppendUShort(bytes, 2);
        AppendUShort(bytes, 0);
        AppendFloat(bytes, 0.75f);
        AppendUShort(bytes, 1);
        AppendFloat(bytes, 0.25f);

        // Bone 1: bind translation Z -41.0, one weight.
        AppendTransform(bytes, -41.0f);
        AppendBoundingSphere(bytes);
        AppendUShort(bytes, 1);
        AppendUShort(bytes, 1);
        AppendFloat(bytes, 1.0f);

        return bytes.ToArray();
    }

    private static BlockInfo Block(byte[] data)
    {
        return new BlockInfo
        {
            Index = 0,
            TypeName = "NiSkinData",
            DataOffset = 0,
            Size = data.Length
        };
    }

    private static void AppendTransform(List<byte> bytes, float tz)
    {
        // Identity 3x3 rotation + translation (0, 0, tz) + scale 1.
        float[] rot = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        foreach (var v in rot) AppendFloat(bytes, v);
        AppendFloat(bytes, 0f);
        AppendFloat(bytes, 0f);
        AppendFloat(bytes, tz);
        AppendFloat(bytes, 1f);
    }

    private static void AppendBoundingSphere(List<byte> bytes)
    {
        AppendFloat(bytes, 0f);
        AppendFloat(bytes, 0f);
        AppendFloat(bytes, 0f);
        AppendFloat(bytes, 1f);
    }

    private static void AppendInt(List<byte> bytes, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        bytes.AddRange(b.ToArray());
    }

    private static void AppendUShort(List<byte> bytes, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        bytes.AddRange(b.ToArray());
    }

    private static void AppendFloat(List<byte> bytes, float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        bytes.AddRange(b.ToArray());
    }
}