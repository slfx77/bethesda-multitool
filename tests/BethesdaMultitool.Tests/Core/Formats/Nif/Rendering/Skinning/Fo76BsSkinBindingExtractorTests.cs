using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Skinning;

public sealed class Fo76BsSkinBindingExtractorTests
{
    [Fact]
    public void Extract_DerivesFourthWeight_UsesDirectIndices_AndPreservesInverseBindDirection()
    {
        var fixture = CreateFixture();

        var result = Fo76BsSkinBindingExtractor.Extract(
            fixture.Data,
            fixture.Nif,
            fixture.ShapeBlockIndex,
            fixture.NodeChildren);

        Assert.Equal(Fo76BsSkinBindingStatus.Success, result.Status);
        var binding = Assert.IsType<Fo76BsSkinBinding>(result.Binding);
        Assert.Equal(new[] { "Bone A", "Bone B" }, binding.BoneNames);
        Assert.Equal(new[] { fixture.BoneA, fixture.BoneB }, binding.BoneBlockIndices);

        // Packed lanes are (.5, .25, 0, serialized .875). The retail shader ignores serialized W
        // and derives 1-saturate(.75) = .25. Indices are direct BSSkin ordinals: [1,0,0,1].
        var influences = Assert.Single(binding.PerVertexInfluences);
        Assert.Equal(3, influences.Length);
        Assert.Equal((1, 0.5f), influences[0]);
        Assert.Equal((0, 0.25f), influences[1]);
        Assert.Equal((1, 0.25f), influences[2]);
        Assert.Equal(0.625f, binding.MaxStoredFourthWeightError, 6);
        Assert.Equal(0f, binding.MaxGltfNormalizationDelta, 6);

        Assert.Equal(Matrix4x4.Identity, binding.InverseBindMatrices[0]);
        AssertVectorNear(
            new Vector3(1f, 2f, 3f),
            Vector3.Transform(Vector3.Zero, binding.InverseBindMatrices[1]));

        // BSSkinBoneTrans is already skin/mesh-to-bone (inverse bind), so conversion is basis
        // conjugation only; there is no matrix inversion between these two assertions.
        var gltfInverseBinds = binding.CreateGltfInverseBindMatrices();
        AssertVectorNear(
            new Vector3(1f, 3f, -2f),
            Vector3.Transform(Vector3.Zero, gltfInverseBinds[1]));
    }

    [Fact]
    public void Extract_NonZeroScaleArray_IsExplicitlyUnsupported()
    {
        var fixture = CreateFixture(scaleCount: 1);

        var result = Fo76BsSkinBindingExtractor.Extract(
            fixture.Data,
            fixture.Nif,
            fixture.ShapeBlockIndex,
            fixture.NodeChildren);

        Assert.Equal(Fo76BsSkinBindingStatus.UnsupportedNonZeroScales, result.Status);
        Assert.Null(result.Binding);
        Assert.Contains("NumScales=1", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_BoneOutsideSkeletonRootSubtree_IsExplicitlyUnsupported()
    {
        var fixture = CreateFixture(includeBoneBUnderRoot: false);

        var result = Fo76BsSkinBindingExtractor.Extract(
            fixture.Data,
            fixture.Nif,
            fixture.ShapeBlockIndex,
            fixture.NodeChildren);

        Assert.Equal(Fo76BsSkinBindingStatus.UnsupportedSkeleton, result.Status);
        Assert.Null(result.Binding);
        Assert.Contains("outside the skeleton-root subtree", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ActiveDirectIndexOutsideBoneArray_IsMalformed()
    {
        var fixture = CreateFixture(lastJointIndex: 2);

        var result = Fo76BsSkinBindingExtractor.Extract(
            fixture.Data,
            fixture.Nif,
            fixture.ShapeBlockIndex,
            fixture.NodeChildren);

        Assert.Equal(Fo76BsSkinBindingStatus.MalformedData, result.Status);
        Assert.Null(result.Binding);
        Assert.Contains("direct bone 2", result.Diagnostic, StringComparison.Ordinal);
    }

    private static SyntheticFixture CreateFixture(
        uint scaleCount = 0,
        bool includeBoneBUnderRoot = true,
        byte lastJointIndex = 1)
    {
        const int shapeIndex = 0;
        const int rootIndex = 1;
        const int instanceIndex = 2;
        const int boneAIndex = 3;
        const int boneBIndex = 4;
        const int boneDataIndex = 5;
        const int shapeSize = 180;

        var rootOffset = shapeSize;
        var instanceOffset = rootOffset + 4;
        var instanceSize = checked(24 + (int)scaleCount * 12);
        var boneAOffset = instanceOffset + instanceSize;
        var boneBOffset = boneAOffset + 4;
        var boneDataOffset = boneBOffset + 4;
        const int boneDataSize = 4 + 2 * 68;
        var data = new byte[boneDataOffset + boneDataSize];

        // BSTriShape NiAVObject base, FO76 bounding sphere + box, inline refs and descriptor.
        WriteInt32(data, 0, 0); // Shape name string index.
        WriteUInt32(data, 4, 0); // Num Extra Data.
        WriteInt32(data, 8, -1); // Controller.
        WriteUInt32(data, 12, 0); // Flags.
        WriteIdentityMatrix33(data, 28);
        WriteSingle(data, 64, 1f); // NiAVObject scale.
        WriteInt32(data, 68, -1); // Collision Object.
        WriteInt32(data, 112, instanceIndex);
        WriteInt32(data, 116, -1); // Shader Property.
        WriteInt32(data, 120, -1); // Alpha Property.

        const ulong descriptor =
            8UL | // 8 dwords = 32-byte vertex stride.
            (5UL << 28) | // skin lanes at byte 20.
            (0x41UL << 44); // Vertex + Skinned attributes.
        WriteUInt64(data, 124, descriptor);
        WriteUInt32(data, 132, 1); // Num Triangles.
        WriteUInt16(data, 136, 1); // Num Vertices.
        WriteUInt32(data, 138, 38); // Vertex + index bytes (informational).

        const int vertexOffset = 142;
        WriteHalf(data, vertexOffset + 20, 0.5f);
        WriteHalf(data, vertexOffset + 22, 0.25f);
        WriteHalf(data, vertexOffset + 24, 0f);
        WriteHalf(data, vertexOffset + 26, 0.875f); // Deliberately not the shader residual.
        data[vertexOffset + 28] = 1;
        data[vertexOffset + 29] = 0;
        data[vertexOffset + 30] = 0;
        data[vertexOffset + 31] = lastJointIndex;
        // One degenerate-but-bounded triangle at bytes 174..179.

        WriteInt32(data, rootOffset, 1);
        WriteInt32(data, boneAOffset, 2);
        WriteInt32(data, boneBOffset, 3);

        WriteInt32(data, instanceOffset, rootIndex);
        WriteInt32(data, instanceOffset + 4, boneDataIndex);
        WriteUInt32(data, instanceOffset + 8, 2);
        WriteInt32(data, instanceOffset + 12, boneAIndex);
        WriteInt32(data, instanceOffset + 16, boneBIndex);
        WriteUInt32(data, instanceOffset + 20, scaleCount);
        for (var scaleIndex = 0; scaleIndex < scaleCount; scaleIndex++)
        {
            var scaleOffset = instanceOffset + 24 + scaleIndex * 12;
            WriteSingle(data, scaleOffset, 1f);
            WriteSingle(data, scaleOffset + 4, 1f);
            WriteSingle(data, scaleOffset + 8, 1f);
        }

        WriteUInt32(data, boneDataOffset, 2);
        WriteBoneTransform(data, boneDataOffset + 4, Vector3.Zero);
        WriteBoneTransform(data, boneDataOffset + 4 + 68, new Vector3(1f, 2f, 3f));

        var nif = new NifInfo
        {
            BinaryVersion = NifVersions.Gamebryo202007,
            BsVersion = 155,
            IsBigEndian = false,
            BlockCount = 6
        };
        nif.Strings.AddRange(["Shape", "Root", "Bone A", "Bone B"]);
        nif.Blocks.AddRange(
        [
            Block(shapeIndex, "BSSubIndexTriShape", 0, shapeSize),
            Block(rootIndex, "NiNode", rootOffset, 4),
            Block(instanceIndex, "BSSkin::Instance", instanceOffset, instanceSize),
            Block(boneAIndex, "NiNode", boneAOffset, 4),
            Block(boneBIndex, "NiNode", boneBOffset, 4),
            Block(boneDataIndex, "BSSkin::BoneData", boneDataOffset, boneDataSize)
        ]);

        var children = includeBoneBUnderRoot
            ? new List<int> { boneAIndex, boneBIndex }
            : [boneAIndex];
        return new SyntheticFixture(
            data,
            nif,
            shapeIndex,
            boneAIndex,
            boneBIndex,
            new Dictionary<int, List<int>> { [rootIndex] = children });
    }

    private static BlockInfo Block(int index, string type, int offset, int size) => new()
    {
        Index = index,
        TypeName = type,
        DataOffset = offset,
        Size = size
    };

    private static void WriteBoneTransform(byte[] data, int offset, Vector3 translation)
    {
        // BSSkinBoneTrans begins with a 16-byte NiBound; its NiTransform follows.
        WriteIdentityMatrix33(data, offset + 16);
        WriteSingle(data, offset + 52, translation.X);
        WriteSingle(data, offset + 56, translation.Y);
        WriteSingle(data, offset + 60, translation.Z);
        WriteSingle(data, offset + 64, 1f);
    }

    private static void WriteIdentityMatrix33(byte[] data, int offset)
    {
        WriteSingle(data, offset, 1f);
        WriteSingle(data, offset + 16, 1f);
        WriteSingle(data, offset + 32, 1f);
    }

    private static void WriteHalf(byte[] data, int offset, float value)
    {
        WriteUInt16(data, offset, BitConverter.HalfToUInt16Bits((Half)value));
    }

    private static void WriteSingle(byte[] data, int offset, float value)
    {
        WriteInt32(data, offset, BitConverter.SingleToInt32Bits(value));
    }

    private static void WriteInt32(byte[] data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }

    private static void WriteUInt64(byte[] data, int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual, float epsilon = 1e-5f)
    {
        Assert.InRange(Vector3.Distance(expected, actual), 0f, epsilon);
    }

    private sealed record SyntheticFixture(
        byte[] Data,
        NifInfo Nif,
        int ShapeBlockIndex,
        int BoneA,
        int BoneB,
        Dictionary<int, List<int>> NodeChildren);
}

[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Fo76BsSkinBindingRetailTests(ITestOutputHelper output)
{
    [Fact]
    public void FemaleBody_RetailPackedSkin_ProducesBoundedExportBinding()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile(
            "Fallout76",
            @"Data\SeventySix - Meshes.ba2");
        Assert.SkipUnless(
            archivePath is not null,
            RealAssetPaths.SkipMessage("Fallout 76 SeventySix - Meshes.ba2"));

        using var service = NifBrowserService.CreateFromBsa(archivePath!);
        const string meshPath = @"meshes\actors\character\characterassets\femalebody.nif";
        var data = service.ReadNifData(meshPath);
        Assert.SkipUnless(data is not null, $"Retail fixture is absent from {Path.GetFileName(archivePath)}.");
        var nif = NifParser.Parse(data!);
        Assert.NotNull(nif);

        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(data!, nif!, nodeChildren, shapeDataMap);
        var candidate = Assert.Single(
            shapeDataMap.Keys.Where(shapeIndex =>
                Fo76BsSkinBindingExtractor.IsCandidate(data!, nif!, shapeIndex)));

        var result = Fo76BsSkinBindingExtractor.Extract(data!, nif!, candidate, nodeChildren);

        Assert.Equal(Fo76BsSkinBindingStatus.Success, result.Status);
        var binding = Assert.IsType<Fo76BsSkinBinding>(result.Binding);
        Assert.Equal(60, binding.BoneNames.Length);
        Assert.Equal(60, binding.InverseBindMatrices.Length);
        Assert.Equal(1723, binding.PerVertexInfluences.Length);
        Assert.InRange(binding.MaxStoredFourthWeightError, 0f, 0.0004f);
        Assert.InRange(binding.MaxGltfNormalizationDelta, 0f, 0.0004f);

        var first = binding.PerVertexInfluences[0];
        Assert.Equal(new[] { 57, 56, 54, 2 }, first.Select(influence => influence.BoneIdx).ToArray());
        Assert.InRange(Math.Abs(first[0].Weight - 0.84228516f), 0f, 1e-6f);
        Assert.InRange(Math.Abs(first[1].Weight - 0.07232666f), 0f, 1e-6f);
        Assert.InRange(Math.Abs(first[2].Weight - 0.067993164f), 0f, 1e-6f);
        Assert.InRange(Math.Abs(first[3].Weight - 0.01739502f), 0f, 1e-6f);
        Assert.InRange(Math.Abs(first.Sum(influence => influence.Weight) - 1f), 0f, 1e-6f);

        var exported = NifExportExtractor.Extract(data!, nif!);
        var skinnedPart = Assert.Single(exported.MeshParts.Where(part => part.Skin is not null));
        Assert.Equal(1723, skinnedPart.Submesh.VertexCount);
        Assert.Equal(60, skinnedPart.Skin!.BoneNames.Length);

        var glbScene = NifExportSceneBuilder.Build(data!, nif!, meshPath);
        Assert.NotNull(glbScene);
        var glbPart = Assert.Single(glbScene!.MeshParts.Where(part => part.Skin is not null));
        Assert.Equal(60, glbPart.Skin!.JointNodeIndices.Length);
        Assert.Equal(1723, glbPart.Skin.PerVertexInfluences.Length);

        var meshViewerGlb = service.BuildGlb(data!, meshPath);
        Assert.NotNull(meshViewerGlb);
        var artifactDirectory = Path.Combine(
            SourceContract.RepoRoot,
            "TestOutput",
            "fo76-starfield-parity",
            "mesh-viewer");
        Directory.CreateDirectory(artifactDirectory);
        var artifactPath = Path.Combine(artifactDirectory, "fo76-femalebody-skinned.glb");
        File.WriteAllBytes(artifactPath, meshViewerGlb!);
        output.WriteLine("Mesh Viewer review artifact: {0}", artifactPath);
        using var stream = new MemoryStream(meshViewerGlb!, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        var exportedSkin = Assert.Single(model.LogicalSkins);
        Assert.Equal(60, exportedSkin.JointsCount);
        Assert.Equal(60, exportedSkin.InverseBindMatrices.Count);
        Assert.Contains(model.LogicalImages, image =>
            (image.Name ?? image.AlternateWriteFileName ?? string.Empty).Contains(
                "normal", StringComparison.OrdinalIgnoreCase));
    }
}
