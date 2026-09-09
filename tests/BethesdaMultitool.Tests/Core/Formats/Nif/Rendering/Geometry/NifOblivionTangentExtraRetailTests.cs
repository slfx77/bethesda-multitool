using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public sealed class NifOblivionTangentExtraRetailTests
{
    [Theory]
    [InlineData("greaves", "B6F4C148487771B9EE7273DA4A68B3A24EC8E108712AA715B51838169F54646D")]
    [InlineData("boots", "47D736A606CC1EB63C29C283B8600AD495E970D0C0302BA2C7556256B57DD3BC")]
    [InlineData("cuirass", "C02C9A80C4DE1F9AA3DFEA20A69EA9577E8ADEFAD01E2570CDD38D6AC32A3C9B")]
    public void InstalledFemaleIron_AllNineShapesRetainAuthoredBasisInCpuExportAndNativeContracts(
        string asset,
        string expectedSha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));
        // This fixture needs only three bounded NIF reads, not an ESM or a whole-master copy.
        // Archive ownership stays local; the sequential collection avoids other retail I/O churn.
        using var archive = ArchiveReader.Open(archivePath);
        var meshPath = $@"meshes\armor\iron\f\{asset}.nif";
        var data = Assert.IsType<byte[]>(archive.ReadFile(meshPath));
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(data)));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        Assert.False(nif.IsBigEndian);
        Assert.True(nif.HasInlineStrings);
        Assert.Equal(11u, nif.UserVersion);
        Assert.Equal(11u, nif.BsVersion);
        Assert.True(nif.BinaryVersion is 0x14000004 or 0x14000005);

        using var textures = new NifTextureResolver();
        var cpu = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data, nif, textures, true));
        var exported = NifExportExtractor.Extract(data, nif);
        var glb = Assert.IsType<GlbScene>(NifExportSceneBuilder.Build(data, nif, meshPath));
        var native = BethesdaViewerSceneGlbAdapter.FromGlbScene(
            glb, meshPath, BethesdaViewerScenePurpose.RawNif, game: BethesdaGame.Oblivion);
        var targets = Targets(asset);
        Assert.Equal(targets.Length, cpu.Submeshes.Count);
        Assert.Equal(targets.Length, exported.MeshParts.Count);
        Assert.Equal(targets.Length, native.MeshParts.Count);

        foreach (var target in targets)
        {
            AssertAuthoredContainer(data, nif, target);
            var cpuPart = Assert.Single(cpu.Submeshes, part => part.SourceBlockIndex == target.Shape);
            var exportPart = Assert.Single(exported.MeshParts, part => part.Submesh.SourceBlockIndex == target.Shape);
            var nativePart = Assert.Single(native.MeshParts, part => part.Submesh.SourceBlockIndex == target.Shape);
            Assert.Equal(target.Name, exportPart.Name);
            Assert.NotNull(exportPart.Skin);
            Assert.NotNull(nativePart.Skin);
            Assert.Equal(target.Vertices, cpuPart.VertexCount);
            Assert.Equal(target.Vertices, exportPart.Submesh.VertexCount);
            Assert.Equal(target.Vertices, nativePart.Submesh.VertexCount);
            AssertAuthoredArrays(data, nif.Blocks[target.Extra], target.Vertices,
                asset == "greaves" && target.Shape == 18 ? [132, 144, 398, 410] : [],
                cpuPart, exportPart.Submesh, nativePart.Submesh);
        }
    }

    private static void AssertAuthoredContainer(byte[] data, NifInfo nif, Target target)
    {
        var geometry = nif.Blocks[target.Geometry];
        Assert.True(geometry.TypeName is "NiTriShapeData" or "NiTriStripsData");
        Assert.Equal(target.Vertices, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(geometry.DataOffset + 4)));
        var flagsOffset = geometry.DataOffset + 9 + target.Vertices * 12;
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(flagsOffset)));
        Assert.Equal((byte)1, data[flagsOffset + 2]); // normals present, no inline tangent bit
        var extra = nif.Blocks[target.Extra];
        Assert.Equal("NiBinaryExtraData", extra.TypeName);
        Assert.Equal(50 + target.Vertices * 24, extra.Size);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(extra.DataOffset)));
        Assert.Equal("Tangent space (binormal & tangent vectors)",
            Encoding.ASCII.GetString(data, extra.DataOffset + 4, 42));
        Assert.Equal((uint)(target.Vertices * 24),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(extra.DataOffset + 46)));
    }

    private static void AssertAuthoredArrays(
        byte[] data,
        BlockInfo extra,
        int vertexCount,
        int[] expectedNearCollinearVertices,
        params RenderableSubmesh[] parts)
    {
        foreach (var part in parts)
        {
            Assert.NotNull(part.Tangents);
            Assert.NotNull(part.Bitangents);
            Assert.NotNull(part.Normals);
            Assert.Equal(vertexCount * 3, part.Tangents.Length);
            Assert.Equal(vertexCount * 3, part.Bitangents.Length);
            var gpuVertices = GpuMeshUploader.BuildVertices(part);
            var nearCollinearVertices = new List<int>();
            for (var i = 0; i < vertexCount; i++)
            {
                // Independent literal storage walk: first all Y vectors, then all X vectors.
                // Do not call the production extra-data reader to create its own expected result.
                var expectedB = ReadVector(data, extra.DataOffset + 50 + i * 12);
                var expectedT = ReadVector(data, extra.DataOffset + 50 + vertexCount * 12 + i * 12);
                Assert.Equal(expectedT, NifOblivionTangentTestData.VectorAt(part.Tangents, i));
                Assert.Equal(expectedB, NifOblivionTangentTestData.VectorAt(part.Bitangents, i));
                Assert.Equal(expectedT, gpuVertices[i].Tangent);
                Assert.Equal(expectedB, gpuVertices[i].Bitangent);
                Assert.True(float.IsFinite(expectedT.LengthSquared()) && expectedT.LengthSquared() > 0);
                Assert.True(float.IsFinite(expectedB.LengthSquared()) && expectedB.LengthSquared() > 0);
                var normal = Vector3.Normalize(NifOblivionTangentTestData.VectorAt(part.Normals, i));
                var orientation = Vector3.Dot(
                    Vector3.Cross(normal, Vector3.Normalize(expectedT)), Vector3.Normalize(expectedB));
                Assert.True(float.IsFinite(orientation));
                // Descriptive pin, NOT a decoder acceptance criterion. Four real greaves vertices
                // have finite, nonzero, nearly opposite T/B. Preserve their exact authored arrays.
                if (MathF.Abs(orientation) <= 1e-6f)
                    nearCollinearVertices.Add(i);
            }

            Assert.Equal(expectedNearCollinearVertices, nearCollinearVertices);
        }
    }

    private static Vector3 ReadVector(byte[] data, int offset)
    {
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 8)));
    }

    private static Target[] Targets(string asset)
    {
        return asset switch
        {
            "greaves" =>
            [
                new Target(1, 6, 2, 484, "LowerBody:0"),
                new Target(18, 23, 19, 725, "LowerBody:1"),
                new Target(27, 30, 28, 454, "LowerBody:2")
            ],
            "boots" => [new Target(1, 6, 2, 1840, "Foot")],
            "cuirass" =>
            [
                new Target(1, 6, 2, 444, "Arms"),
                new Target(23, 25, 24, 214, "UpperBody"),
                new Target(32, 37, 33, 282, "Arms"),
                new Target(41, 43, 42, 1331, "UpperBody"),
                new Target(49, 52, 50, 932, "UpperBody")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(asset))
        };
    }

    private readonly record struct Target(int Shape, int Geometry, int Extra, int Vertices, string Name);
}