using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Skinning;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Vegetation;

/// <summary>
///     Synthetic regressions for classic Skyrim's animated-tree layout: a NiSwitchNode selects the
///     full internally-skinned subtree, and those shapes source topology from NiSkinPartition rather
///     than their zero-triangle NiTriShapeData blocks.
/// </summary>
public sealed class SkyrimTreeFullDetailTests
{
    private const uint SkyrimNifVersion = 0x14020007;
    private const uint SkyrimBsVersion = 83;

    private static readonly int[] ExpectedInactiveFallbackShapes = [5, 6];

    [Fact]
    public void SkinPartitionTriangles_SkipClassicSkyrimFooterBetweenPartitions()
    {
        var data = BuildTwoPartitionTopology();

        var triangles = NifSkinPartitionParser.ExtractTriangles(
            data,
            0,
            data.Length,
            false,
            bsVersion: SkyrimBsVersion);

        Assert.Equal(new ushort[] { 0, 1, 2, 3, 4, 5 }, triangles);
    }

    [Fact]
    public void ZeroTriangleShapeData_AcceptsSkinPartitionTopology()
    {
        var data = BuildZeroTriangleShapeData();
        var block = new BlockInfo
        {
            Index = 0,
            TypeName = "NiTriShapeData",
            DataOffset = 0,
            Size = data.Length
        };

        var submesh = NifSubmeshExtractor.ExtractTriShapeData(
            data,
            block,
            false,
            SkyrimBsVersion,
            SkyrimNifVersion,
            Matrix4x4.Identity,
            fallbackTriangles: [0, 1, 2]);

        Assert.NotNull(submesh);
        Assert.Equal(3, submesh.VertexCount);
        Assert.Equal(new ushort[] { 0, 1, 2 }, submesh.Triangles);
    }

    [Fact]
    public void SwitchNode_PrunesStaticFallbackAndKeepsActiveFullDetailSubtree()
    {
        var switchBytes = BuildSwitchNode(0, [1, 2]);
        var nif = new NifInfo
        {
            BinaryVersion = SkyrimNifVersion,
            BsVersion = SkyrimBsVersion,
            IsBigEndian = false,
            BlockCount = 7
        };
        foreach (var (index, type) in new[]
                 {
                     (0, "NiSwitchNode"),
                     (1, "NiNode"),
                     (2, "NiNode"),
                     (3, "NiTriShape"),
                     (4, "NiTriShape"),
                     (5, "NiTriShape"),
                     (6, "NiTriShape")
                 })
        {
            nif.Blocks.Add(new BlockInfo
            {
                Index = index,
                TypeName = type,
                DataOffset = 0,
                Size = index == 0 ? switchBytes.Length : 0
            });
        }

        var graph = new Dictionary<int, List<int>>
        {
            [0] = [1, 2],
            [1] = [3, 4],
            [2] = [5, 6]
        };

        var inactive = NifSceneGraphWalker.CollectInactiveSwitchShapes(switchBytes, nif, graph);

        Assert.Contains("BSTreeNode", NifSceneGraphWalker.NodeTypes);
        Assert.Contains("NiSwitchNode", NifSceneGraphWalker.NodeTypes);
        Assert.Equal(ExpectedInactiveFallbackShapes, inactive.Order());
        Assert.DoesNotContain(3, inactive);
        Assert.DoesNotContain(4, inactive);
    }

    private static byte[] BuildTwoPartitionTopology()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2u); // Num Partitions
        WritePartition(writer, [0, 1, 2]);
        WritePartition(writer, [3, 4, 5]);
        return stream.ToArray();
    }

    private static void WritePartition(BinaryWriter writer, ushort[] vertexMap)
    {
        writer.Write((ushort)3); // Num Vertices
        writer.Write((ushort)1); // Num Triangles
        writer.Write((ushort)1); // Num Bones
        writer.Write((ushort)0); // Num Strips
        writer.Write((ushort)4); // Num Weights Per Vertex
        writer.Write((ushort)0); // Bones[0]
        writer.Write((byte)1); // Has Vertex Map
        foreach (var vertex in vertexMap)
        {
            writer.Write(vertex);
        }

        writer.Write((byte)0); // Has Vertex Weights
        writer.Write((byte)1); // Has Faces
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write((byte)0); // Has Bone Indices
        writer.Write((byte)0); // LOD Level (BS > 34)
        writer.Write((byte)0); // Global VB (BS > 34)
    }

    private static byte[] BuildZeroTriangleShapeData()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0u); // Group ID
        writer.Write((ushort)3); // Num Vertices
        writer.Write((byte)0); // Keep Flags
        writer.Write((byte)0); // Compress Flags
        writer.Write((byte)1); // Has Vertices
        WriteVector(writer, 0f, 0f, 0f);
        WriteVector(writer, 1f, 0f, 0f);
        WriteVector(writer, 0f, 1f, 0f);
        writer.Write((ushort)0); // BS Data Flags
        writer.Write(0u); // Material CRC (BS > 34)
        writer.Write((byte)0); // Has Normals
        writer.Write(new byte[16]); // Bounding Sphere
        writer.Write((byte)0); // Has Vertex Colors
        writer.Write((ushort)0); // Consistency Flags
        writer.Write(-1); // Additional Data ref
        writer.Write((ushort)0); // Num Triangles
        writer.Write(0u); // Num Triangle Points
        writer.Write((byte)0); // Has Triangles
        return stream.ToArray();
    }

    private static byte[] BuildSwitchNode(uint activeChildOrdinal, int[] children)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(-1); // NiObjectNET.Name
        writer.Write(0u); // Num Extra Data List
        writer.Write(-1); // Controller ref
        writer.Write(0x0000080Eu); // NiAVObject Flags
        WriteVector(writer, 0f, 0f, 0f);
        WriteIdentityRotation(writer);
        writer.Write(1f); // Scale
        writer.Write(-1); // Collision Object ref
        writer.Write((uint)children.Length);
        foreach (var child in children)
        {
            writer.Write(child);
        }

        writer.Write(0u); // Num Effects (#NI_BS_LT_FO4#)
        writer.Write((ushort)3); // Switch Node Flags
        writer.Write(activeChildOrdinal);
        return stream.ToArray();
    }

    private static void WriteIdentityRotation(BinaryWriter writer)
    {
        WriteVector(writer, 1f, 0f, 0f);
        WriteVector(writer, 0f, 1f, 0f);
        WriteVector(writer, 0f, 0f, 1f);
    }

    private static void WriteVector(BinaryWriter writer, float x, float y, float z)
    {
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
    }
}