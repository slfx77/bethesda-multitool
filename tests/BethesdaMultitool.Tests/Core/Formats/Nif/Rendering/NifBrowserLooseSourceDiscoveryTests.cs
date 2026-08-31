using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifBrowserLooseSourceDiscoveryTests
{
    [Fact]
    public void CreateFromDirectory_DataMeshes_RegistersSiblingTexturesFromDataRoot()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_loose_textures_").FullName;
        try
        {
            var dataRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "Game", "Data")).FullName;
            var meshesRoot = Directory.CreateDirectory(Path.Combine(dataRoot, "meshes")).FullName;
            Directory.CreateDirectory(Path.Combine(dataRoot, "textures"));

            using var service = NifBrowserService.CreateFromDirectory(meshesRoot);

            Assert.Equal(dataRoot, Assert.Single(service.TexturePaths));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateFromDirectory_NestedBelowDataMeshes_RegistersSiblingMaterialDatabaseRoot()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_loose_materials_").FullName;
        try
        {
            var dataRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "Game", "Data")).FullName;
            var selectedRoot = Directory.CreateDirectory(
                Path.Combine(dataRoot, "meshes", "architecture", "testkit")).FullName;
            var materialsRoot = Directory.CreateDirectory(Path.Combine(dataRoot, "materials")).FullName;
            File.WriteAllBytes(Path.Combine(materialsRoot, "materialsbeta.cdb"), []);

            using var service = NifBrowserService.CreateFromDirectory(selectedRoot);

            Assert.Equal(dataRoot, Assert.Single(service.TexturePaths));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateFromDirectory_ExplicitTextureSources_TakePrecedenceOverDiscovery()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_loose_override_").FullName;
        try
        {
            var dataRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "Game", "Data")).FullName;
            var meshesRoot = Directory.CreateDirectory(Path.Combine(dataRoot, "meshes")).FullName;
            Directory.CreateDirectory(Path.Combine(dataRoot, "textures"));
            var explicitRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "ExplicitData")).FullName;

            using var service = NifBrowserService.CreateFromDirectory(meshesRoot, [explicitRoot]);

            Assert.Equal(explicitRoot, Assert.Single(service.TexturePaths));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateFromDirectory_SelectedDataRoot_RegistersRootRatherThanTexturesDirectory()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_loose_data_").FullName;
        try
        {
            var dataRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "Data")).FullName;
            Directory.CreateDirectory(Path.Combine(dataRoot, "meshes"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "textures"));

            using var service = NifBrowserService.CreateFromDirectory(dataRoot);

            Assert.Equal(dataRoot, Assert.Single(service.TexturePaths));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateFromDirectory_LooseStarfieldExternalMesh_BuildsReloadableGlb()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_loose_starfield_").FullName;
        try
        {
            var dataRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "Game", "Data")).FullName;
            var meshesRoot = Directory.CreateDirectory(Path.Combine(dataRoot, "meshes")).FullName;
            var geometryRoot = Directory.CreateDirectory(
                Path.Combine(dataRoot, "geometries", "test")).FullName;
            Directory.CreateDirectory(Path.Combine(dataRoot, "textures"));

            const string meshPath = @"test\triangle";
            var nifPath = Path.Combine(meshesRoot, "triangle.nif");
            File.WriteAllBytes(nifPath, BuildStarfieldNif(meshPath));
            File.WriteAllBytes(Path.Combine(geometryRoot, "triangle.mesh"), BuildStarfieldMesh());

            using var service = NifBrowserService.CreateFromDirectory(meshesRoot);
            var nifData = service.ReadNifData(nifPath);
            Assert.NotNull(nifData);

            var glb = service.BuildGlb(nifData!, nifPath);

            Assert.NotNull(glb);
            using var stream = new MemoryStream(glb!, writable: false);
            var model = ModelRoot.ReadGLB(stream);
            Assert.NotEmpty(model.LogicalMeshes);
            Assert.Contains(model.LogicalMeshes, mesh => mesh.Primitives.Count > 0);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static byte[] BuildStarfieldNif(string meshPath)
    {
        var geometryBlock = BuildBsGeometryBlock(meshPath);
        var shaderBlock = BuildNiObjectNet(nameIndex: 1);
        string[] blockTypes = ["BSGeometry", "BSLightingShaderProperty"];
        string[] strings = ["Triangle", @"materials\test\triangle.mat"];

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
        bytes.AddRange(BitConverter.GetBytes(0x14020007u));
        bytes.Add(1); // little-endian
        bytes.AddRange(BitConverter.GetBytes(12u));
        bytes.AddRange(BitConverter.GetBytes(2u));
        bytes.AddRange(BitConverter.GetBytes(173u));
        AddExportString(bytes, "test");
        bytes.AddRange(BitConverter.GetBytes(0u)); // BSStreamHeader unknown int
        AddExportString(bytes, "test");
        AddExportString(bytes, ""); // Starfield ExportDataSF

        bytes.AddRange(BitConverter.GetBytes((ushort)blockTypes.Length));
        foreach (var blockType in blockTypes)
        {
            var encoded = Encoding.ASCII.GetBytes(blockType);
            bytes.AddRange(BitConverter.GetBytes((uint)encoded.Length));
            bytes.AddRange(encoded);
        }

        bytes.AddRange(BitConverter.GetBytes((ushort)0));
        bytes.AddRange(BitConverter.GetBytes((ushort)1));
        bytes.AddRange(BitConverter.GetBytes((uint)geometryBlock.Length));
        bytes.AddRange(BitConverter.GetBytes((uint)shaderBlock.Length));

        bytes.AddRange(BitConverter.GetBytes((uint)strings.Length));
        bytes.AddRange(BitConverter.GetBytes((uint)strings.Max(static value => value.Length)));
        foreach (var value in strings)
        {
            var encoded = Encoding.ASCII.GetBytes(value);
            bytes.AddRange(BitConverter.GetBytes((uint)encoded.Length));
            bytes.AddRange(encoded);
        }

        bytes.AddRange(BitConverter.GetBytes(0u)); // no block groups
        bytes.AddRange(geometryBlock);
        bytes.AddRange(shaderBlock);
        return [.. bytes];
    }

    private static byte[] BuildBsGeometryBlock(string meshPath)
    {
        var bytes = new List<byte>(BuildNiObjectNet(nameIndex: 0));
        bytes.AddRange(BitConverter.GetBytes(0u)); // external mesh (inline-data flag clear)
        AddVector3(bytes, 0f, 0f, 0f);
        AddMatrix33Identity(bytes);
        bytes.AddRange(BitConverter.GetBytes(1f));
        bytes.AddRange(BitConverter.GetBytes(-1)); // no collision object
        bytes.AddRange(new byte[16 + 24]); // bounding sphere + bounding box
        bytes.AddRange(BitConverter.GetBytes(-1)); // no skin
        bytes.AddRange(BitConverter.GetBytes(1)); // shader block
        bytes.AddRange(BitConverter.GetBytes(-1)); // no alpha property
        bytes.Add(1); // LOD 0 has a mesh
        bytes.AddRange(new byte[12]); // indices size + vertex count + flags

        var encodedPath = Encoding.ASCII.GetBytes(meshPath);
        bytes.AddRange(BitConverter.GetBytes((uint)encodedPath.Length));
        bytes.AddRange(encodedPath);
        return [.. bytes];
    }

    private static byte[] BuildStarfieldMesh()
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(0u)); // mesh version
        bytes.AddRange(BitConverter.GetBytes(3u));
        foreach (ushort index in new ushort[] { 0, 1, 2 })
        {
            bytes.AddRange(BitConverter.GetBytes(index));
        }

        bytes.AddRange(BitConverter.GetBytes(1f));
        bytes.AddRange(BitConverter.GetBytes(0u)); // weights per vertex
        bytes.AddRange(BitConverter.GetBytes(3u));
        AddPackedPosition(bytes, 0, 0, 0);
        AddPackedPosition(bytes, short.MaxValue, 0, 0);
        AddPackedPosition(bytes, 0, short.MaxValue, 0);

        bytes.AddRange(BitConverter.GetBytes(3u));
        AddHalfPair(bytes, 0f, 0f);
        AddHalfPair(bytes, 1f, 0f);
        AddHalfPair(bytes, 0f, 1f);
        bytes.AddRange(BitConverter.GetBytes(0u)); // no second UV set
        bytes.AddRange(BitConverter.GetBytes(0u)); // no vertex colours

        const uint positiveZ = 512u | (512u << 10) | (1023u << 20) | (3u << 30);
        const uint positiveX = 1023u | (512u << 10) | (512u << 20) | (3u << 30);
        AddPackedDirections(bytes, positiveZ);
        AddPackedDirections(bytes, positiveX);
        bytes.AddRange(BitConverter.GetBytes(0u)); // no skin weights
        bytes.AddRange(BitConverter.GetBytes(0u)); // no meshlets
        bytes.AddRange(BitConverter.GetBytes(0u)); // no cull records
        return [.. bytes];
    }

    private static byte[] BuildNiObjectNet(int nameIndex)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(nameIndex));
        bytes.AddRange(BitConverter.GetBytes(0u)); // no extra-data refs
        bytes.AddRange(BitConverter.GetBytes(-1)); // no controller
        return [.. bytes];
    }

    private static void AddExportString(List<byte> bytes, string value)
    {
        var encoded = Encoding.ASCII.GetBytes(value);
        bytes.Add((byte)(encoded.Length + 1));
        bytes.AddRange(encoded);
        bytes.Add(0);
    }

    private static void AddVector3(List<byte> bytes, float x, float y, float z)
    {
        bytes.AddRange(BitConverter.GetBytes(x));
        bytes.AddRange(BitConverter.GetBytes(y));
        bytes.AddRange(BitConverter.GetBytes(z));
    }

    private static void AddMatrix33Identity(List<byte> bytes)
    {
        foreach (var value in new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f })
        {
            bytes.AddRange(BitConverter.GetBytes(value));
        }
    }

    private static void AddPackedPosition(List<byte> bytes, short x, short y, short z)
    {
        var xy = (uint)(ushort)x | ((uint)(ushort)y << 16);
        bytes.AddRange(BitConverter.GetBytes(xy));
        bytes.AddRange(BitConverter.GetBytes((ushort)z));
    }

    private static void AddHalfPair(List<byte> bytes, float u, float v)
    {
        var packed = (uint)BitConverter.HalfToUInt16Bits((Half)u) |
                     ((uint)BitConverter.HalfToUInt16Bits((Half)v) << 16);
        bytes.AddRange(BitConverter.GetBytes(packed));
    }

    private static void AddPackedDirections(List<byte> bytes, uint direction)
    {
        bytes.AddRange(BitConverter.GetBytes(3u));
        for (var index = 0; index < 3; index++)
        {
            bytes.AddRange(BitConverter.GetBytes(direction));
        }
    }
}
