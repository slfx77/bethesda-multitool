using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifBrowserSiblingMeshArchiveTests
{
    private const string NifPath = @"meshes\test\crossarchive.nif";
    private const string PrimaryMeshPath = @"geometries\primary\triangle.mesh";
    private const string SiblingMeshPath = @"geometries\sibling\triangle.mesh";

    [Fact]
    public void CreateFromBsa_CrossArchiveStarfieldGeometry_ResolvesEveryReferenceOpenedFirst()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_crossarchive_").FullName;
        try
        {
            var primaryArchive = Path.Combine(tempRoot, "Starfield - Meshes01.ba2");
            var siblingArchive = Path.Combine(tempRoot, "Starfield - Meshes02.ba2");
            var laterSiblingArchive = Path.Combine(tempRoot, "Starfield - Meshes03.ba2");
            File.WriteAllBytes(
                primaryArchive,
                BuildGnrlBa2(
                    (NifPath, BuildStarfieldNif(@"primary\triangle", @"sibling\triangle")),
                    (PrimaryMeshPath, BuildStarfieldMesh())));
            // Create Meshes03 before Meshes02 so filesystem enumeration/creation order cannot decide
            // which sibling wins. Ordinal archive precedence must select the valid Meshes02 entry.
            File.WriteAllBytes(
                laterSiblingArchive,
                BuildGnrlBa2((SiblingMeshPath, BitConverter.GetBytes(99u))));
            File.WriteAllBytes(
                siblingArchive,
                BuildGnrlBa2(
                    // If sibling precedence accidentally outranks the opened archive, this invalid
                    // duplicate is selected and the completeness diagnostic records a decode failure.
                    (PrimaryMeshPath, BitConverter.GetBytes(99u)),
                    (SiblingMeshPath, BuildStarfieldMesh())));

            using var service = NifBrowserService.CreateFromBsa(primaryArchive);

            Assert.Equal(
                [
                    Path.GetFullPath(primaryArchive),
                    Path.GetFullPath(siblingArchive),
                    Path.GetFullPath(laterSiblingArchive)
                ],
                service.ExternalMeshArchivePaths);
            var nifData = service.ReadNifData(NifPath);
            Assert.NotNull(nifData);

            var build = service.BuildGlbWithDiagnostics(nifData!, NifPath);

            Assert.NotNull(build.GlbBytes);
            Assert.Equal(2, build.ExternalGeometry.ReferencedCount);
            Assert.Equal(2, build.ExternalGeometry.ResolvedCount);
            Assert.Empty(build.ExternalGeometry.MissingPaths);
            Assert.Empty(build.ExternalGeometry.DecodeFailedPaths);
            Assert.True(build.ExternalGeometry.IsComplete);
            Assert.Null(build.ExternalGeometry.IncompleteWarningMessage);

            var primaryResolution = Assert.Single(
                build.ExternalGeometry.Resolutions,
                resolution => resolution.VirtualPath == PrimaryMeshPath);
            Assert.True(primaryResolution.Resolved);
            Assert.Equal(Path.GetFullPath(primaryArchive), primaryResolution.SourcePath);

            var siblingResolution = Assert.Single(
                build.ExternalGeometry.Resolutions,
                resolution => resolution.VirtualPath == SiblingMeshPath);
            Assert.True(siblingResolution.Resolved);
            Assert.Equal(Path.GetFullPath(siblingArchive), siblingResolution.SourcePath);

            using var stream = new MemoryStream(build.GlbBytes!, writable: false);
            var model = ModelRoot.ReadGLB(stream);
            var exportedVertexCount = model.LogicalMeshes
                .SelectMany(static mesh => mesh.Primitives)
                .Sum(static primitive => primitive.GetVertexAccessor("POSITION").AsVector3Array().Count);
            Assert.Equal(6, exportedVertexCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildGlbWithDiagnostics_PartialExternalGeometry_IsNotReportedComplete()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_partialgeometry_").FullName;
        try
        {
            var primaryArchive = Path.Combine(tempRoot, "Starfield - Meshes01.ba2");
            File.WriteAllBytes(
                primaryArchive,
                BuildGnrlBa2(
                    (NifPath, BuildStarfieldNif(@"primary\triangle", @"sibling\triangle")),
                    (PrimaryMeshPath, BuildStarfieldMesh())));

            using var service = NifBrowserService.CreateFromBsa(primaryArchive);
            var nifData = service.ReadNifData(NifPath);
            Assert.NotNull(nifData);

            var build = service.BuildGlbWithDiagnostics(nifData!, NifPath);

            // One valid part still produces a reloadable GLB. Completeness therefore must be pinned
            // independently instead of treating "at least one primitive" as end-to-end success.
            Assert.NotNull(build.GlbBytes);
            Assert.Equal(2, build.ExternalGeometry.ReferencedCount);
            Assert.Equal(1, build.ExternalGeometry.ResolvedCount);
            Assert.Equal([SiblingMeshPath], build.ExternalGeometry.MissingPaths);
            Assert.Empty(build.ExternalGeometry.DecodeFailedPaths);
            Assert.False(build.ExternalGeometry.IsComplete);
            Assert.Equal(
                "External geometry is incomplete: located 1 of 2 referenced blobs; 1 missing and " +
                "0 failed to decode. Preview and exports omit those parts.",
                build.ExternalGeometry.IncompleteWarningMessage);

            using var stream = new MemoryStream(build.GlbBytes!, writable: false);
            var model = ModelRoot.ReadGLB(stream);
            var exportedVertexCount = model.LogicalMeshes
                .SelectMany(static mesh => mesh.Primitives)
                .Sum(static primitive => primitive.GetVertexAccessor("POSITION").AsVector3Array().Count);
            Assert.Equal(3, exportedVertexCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static byte[] BuildGnrlBa2(params (string Path, byte[] Data)[] files)
    {
        const int headerSize = 24;
        const int recordSize = 36;
        var nextDataOffset = headerSize + files.Length * recordSize;
        var offsets = new int[files.Length];
        for (var index = 0; index < files.Length; index++)
        {
            offsets[index] = nextDataOffset;
            nextDataOffset += files[index].Data.Length;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("BTDX"u8.ToArray());
        writer.Write(1u);
        writer.Write("GNRL"u8.ToArray());
        writer.Write((uint)files.Length);
        writer.Write((ulong)nextDataOffset);

        for (var index = 0; index < files.Length; index++)
        {
            writer.Write((uint)(index + 1)); // name hash is irrelevant to exact path lookup
            writer.Write(ExtensionBytes(files[index].Path));
            writer.Write(0u); // directory hash
            writer.Write(0u); // flags
            writer.Write((ulong)offsets[index]);
            writer.Write(0u); // packed size 0 means uncompressed
            writer.Write((uint)files[index].Data.Length);
            writer.Write(0u); // alignment
        }

        foreach (var file in files)
        {
            writer.Write(file.Data);
        }

        foreach (var file in files)
        {
            var pathBytes = Encoding.UTF8.GetBytes(file.Path);
            writer.Write((ushort)pathBytes.Length);
            writer.Write(pathBytes);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] ExtensionBytes(string path)
    {
        var result = new byte[4];
        Encoding.ASCII.GetBytes(Path.GetExtension(path).TrimStart('.')).CopyTo(result, 0);
        return result;
    }

    private static byte[] BuildStarfieldNif(string primaryMeshPath, string siblingMeshPath)
    {
        var primaryGeometry = BuildBsGeometryBlock(nameIndex: 0, primaryMeshPath, shaderBlockIndex: 2);
        var siblingGeometry = BuildBsGeometryBlock(nameIndex: 1, siblingMeshPath, shaderBlockIndex: 2);
        var shaderBlock = BuildNiObjectNet(nameIndex: 2);
        string[] blockTypes = ["BSGeometry", "BSLightingShaderProperty"];
        string[] strings = ["Primary", "Sibling", @"materials\test\crossarchive.mat"];

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
        bytes.AddRange(BitConverter.GetBytes(0x14020007u));
        bytes.Add(1); // little-endian
        bytes.AddRange(BitConverter.GetBytes(12u));
        bytes.AddRange(BitConverter.GetBytes(3u));
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
        bytes.AddRange(BitConverter.GetBytes((ushort)0));
        bytes.AddRange(BitConverter.GetBytes((ushort)1));
        bytes.AddRange(BitConverter.GetBytes((uint)primaryGeometry.Length));
        bytes.AddRange(BitConverter.GetBytes((uint)siblingGeometry.Length));
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
        bytes.AddRange(primaryGeometry);
        bytes.AddRange(siblingGeometry);
        bytes.AddRange(shaderBlock);
        return [.. bytes];
    }

    private static byte[] BuildBsGeometryBlock(int nameIndex, string meshPath, int shaderBlockIndex)
    {
        var bytes = new List<byte>(BuildNiObjectNet(nameIndex));
        bytes.AddRange(BitConverter.GetBytes(0u)); // external mesh (inline-data flag clear)
        AddVector3(bytes, 0f, 0f, 0f);
        AddMatrix33Identity(bytes);
        bytes.AddRange(BitConverter.GetBytes(1f));
        bytes.AddRange(BitConverter.GetBytes(-1)); // no collision object
        bytes.AddRange(new byte[16 + 24]); // bounding sphere + bounding box
        bytes.AddRange(BitConverter.GetBytes(-1)); // no skin
        bytes.AddRange(BitConverter.GetBytes(shaderBlockIndex));
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
