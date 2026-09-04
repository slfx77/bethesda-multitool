using System.Numerics;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Skinning;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Vegetation;

/// <summary>
///     Synthetic regressions for classic Skyrim's animated-tree layout: a NiSwitchNode selects the
///     full internally-skinned subtree, and those shapes source topology from NiSkinPartition rather
///     than their zero-triangle NiTriShapeData blocks.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
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
    public void SkinPartitionInfluences_SkipClassicSkyrimFooterBetweenPartitions()
    {
        var data = BuildTwoPartitionTopology();

        var parsed = Assert.IsType<NifSkinPartitionExpander.SkinPartitionData>(
            NifSkinPartitionExpander.Parse(
                data,
                0,
                data.Length,
                false,
                bsVersion: SkyrimBsVersion));

        Assert.Equal(2u, parsed.NumPartitions);
        Assert.Collection(parsed.Partitions,
            first => Assert.Equal(new ushort[] { 0, 1, 2 }, first.VertexMap),
            second => Assert.Equal(new ushort[] { 3, 4, 5 }, second.VertexMap));
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

    [Fact]
    [Trait("Category", BucketBTestGuard.Category)]
    public void RetailSnowTrees_HaveFiniteSaneSkinnedRestPoseBounds()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = Environment.GetEnvironmentVariable("SKYRIM_MESHES_BSA") ??
                          RealAssetPaths.SteamGameFile("Skyrim", @"Data\Skyrim - Meshes.bsa");
        Assert.SkipWhen(!File.Exists(archivePath),
            "Skyrim LE Meshes BSA not installed (set SKYRIM_MESHES_BSA to run this fixture)");

        using var archive = ArchiveReader.Open(archivePath);
        AssertRetailTree(archive, @"meshes\landscape\trees\treepineforestsnow02.nif", 15, 100f, -100f);
        AssertRetailTree(archive, @"meshes\landscape\trees\treepineforestsnow03.nif", 13, -30f, -70f);
        AssertRetailTree(archive, @"meshes\landscape\trees\treepineforestsnow04.nif", 12, 50f, -100f);
    }

    [Fact]
    [Trait("Category", BucketBTestGuard.Category)]
    public void RetailSnow02_WithoutTreeNodeAncestry_UsesSlsf2TreeAnimIdentity()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = Environment.GetEnvironmentVariable("SKYRIM_MESHES_BSA") ??
                          RealAssetPaths.SteamGameFile("Skyrim", @"Data\Skyrim - Meshes.bsa");
        Assert.SkipWhen(!File.Exists(archivePath),
            "Skyrim LE Meshes BSA not installed (set SKYRIM_MESHES_BSA to run this fixture)");

        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(
            @"meshes\landscape\trees\treepineforestsnow02.nif"));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        foreach (var block in nif.Blocks.Where(static block =>
                     NifSceneGraphWalker.TreeAnimationNodeTypes.Contains(block.TypeName)))
        {
            // Retain the identical NiNode-compatible bytes and remove only the ancestry identity.
            // The extracted shape must therefore be classified by its retail SLSF2 bit alone.
            block.TypeName = "NiNode";
        }

        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap);
        Assert.Empty(NifSceneGraphWalker.CollectTreeAnimationShapes(nif, nodeChildren));

        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data,
            nif,
            bindPoseOnly: false,
            skipSkinning: false,
            treatRootsAsIdentity: true,
            collectBillboards: true,
            dropBoneAttachedShapes: true));
        var branch = Assert.Single(model.Submeshes, static submesh => submesh.SourceBlockIndex == 15);

        Assert.Equal("BSLightingShaderProperty", branch.ShaderMetadata?.PropertyType);
        Assert.True(branch.ShaderMetadata!.ShaderFlags2.HasValue);
        Assert.NotEqual(0u, branch.ShaderMetadata.ShaderFlags2.GetValueOrDefault() & (1u << 29));
        Assert.True(branch.IsTreeAnimation);
        Assert.False(branch.UseVertexAlphaForOpacity);
    }

    private static void AssertRetailTree(
        ArchiveReader archive,
        string path,
        int branchBlockIndex,
        float branchMinimumZ,
        float modelMinimumZ)
    {
        var data = Assert.IsType<byte[]>(archive.ReadFile(path));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data,
            nif,
            bindPoseOnly: false,
            skipSkinning: false,
            treatRootsAsIdentity: true,
            collectBillboards: true,
            dropBoneAttachedShapes: true));

        Assert.NotEmpty(model.Submeshes);
        var minimumZ = model.Submeshes.Min(MinimumZ);
        Assert.True(minimumZ > modelMinimumZ,
            $"{path} extends implausibly below its authored root: {minimumZ}");
        var branch = Assert.Single(model.Submeshes, submesh => submesh.SourceBlockIndex == branchBlockIndex);
        var branchZ = MinimumZ(branch);
        Assert.True(branchZ > branchMinimumZ,
            $"{path} block {branchBlockIndex} retained a partially skinned branch vertex: {branchZ}");
        Assert.True(branch.IsTreeAnimation);
        Assert.False(branch.UseVertexAlphaForOpacity);
        Assert.True(branch.HasAlphaTest);
        Assert.Equal((byte)112, branch.AlphaTestThreshold);
        Assert.Equal((byte)4, branch.AlphaTestFunction);
        var authoredColors = Assert.IsType<byte[]>(branch.VertexColors);
        Assert.Equal(branch.VertexCount * 4, authoredColors.Length);
        Assert.Contains(Enumerable.Range(0, branch.VertexCount),
            vertexIndex => authoredColors[vertexIndex * 4 + 3] < byte.MaxValue);

        var decodedBranch = ReferenceSubmeshDecoder12.Decode(
            branch,
            new ReferenceSubmeshDecodeOptions12(
                branch.DiffuseTexturePath,
                branch.NormalMapTexturePath,
                Nif: nif));
        Assert.True(decodedBranch.IsTreeAnimation);
        for (var vertexIndex = 0; vertexIndex < branch.VertexCount; vertexIndex++)
        {
            Assert.Equal(
                authoredColors[vertexIndex * 4 + 3] / 255f,
                decodedBranch.Vertices[vertexIndex].VertexColor.W,
                6);
        }

        foreach (var submesh in model.Submeshes)
        {
            Assert.Equal(0, submesh.Positions.Length % 3);
            Assert.Equal(0, submesh.Triangles.Length % 3);
            Assert.All(submesh.Positions, value => Assert.True(float.IsFinite(value)));
            Assert.All(submesh.Triangles, index => Assert.True(index < submesh.VertexCount));
            Assert.True(MaximumTriangleEdge(submesh) < 500f,
                $"{path} block {submesh.SourceBlockIndex} contains a giant triangle edge");
        }
    }

    private static float MinimumZ(RenderableSubmesh submesh)
    {
        var minimum = float.PositiveInfinity;
        for (var offset = 2; offset < submesh.Positions.Length; offset += 3)
        {
            minimum = MathF.Min(minimum, submesh.Positions[offset]);
        }

        return minimum;
    }

    private static float MaximumTriangleEdge(RenderableSubmesh submesh)
    {
        var maximum = 0f;
        for (var offset = 0; offset < submesh.Triangles.Length; offset += 3)
        {
            var a = Position(submesh, submesh.Triangles[offset]);
            var b = Position(submesh, submesh.Triangles[offset + 1]);
            var c = Position(submesh, submesh.Triangles[offset + 2]);
            maximum = MathF.Max(maximum,
                MathF.Max(Vector3.Distance(a, b),
                    MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a))));
        }

        return maximum;
    }

    private static Vector3 Position(RenderableSubmesh submesh, int index)
    {
        var offset = index * 3;
        return new Vector3(
            submesh.Positions[offset],
            submesh.Positions[offset + 1],
            submesh.Positions[offset + 2]);
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
