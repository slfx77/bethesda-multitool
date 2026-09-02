using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Core.Formats.Bsa;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Pins the complete source set behind a Starfield Mesh Viewer session. Geometry lives in a
///     GNRL meshes archive, material records live in a different GNRL archive, and their image slots
///     can land in any of many DX10 shards. An explicit priority source must not make the latter two
///     classes disappear.
/// </summary>
public sealed class StarfieldNifBrowserTextureSourceSetTests
{
    private const string MaterialDatabasePath = @"materials\materialsbeta.cdb";

    [Fact]
    public void CreateFromBsa_ExplicitSourcePrecedesButRetainsCdbAndEveryDx10Shard()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_sf_sources_").FullName;
        try
        {
            var explicitRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "PriorityData")).FullName;
            var meshes = WriteGnrl(
                tempRoot,
                "Starfield - Meshes01.ba2",
                @"meshes\test\fixture.nif",
                "nif"u8.ToArray());
            var materials = WriteGnrl(
                tempRoot,
                "Starfield - Materials.ba2",
                MaterialDatabasePath,
                "synthetic-cdb"u8.ToArray());
            var textures01 = Path.Combine(tempRoot, "Starfield - Textures01.ba2");
            var textures02 = Path.Combine(tempRoot, "Starfield - Textures02.ba2");
            File.WriteAllBytes(textures01, BuildDx10Ba2());
            File.WriteAllBytes(textures02, BuildDx10Ba2());

            // A GNRL sibling without meshes, textures, or materials must not leak into either set.
            var shaders = WriteGnrl(
                tempRoot,
                "Starfield - Shaders.ba2",
                @"shadersfx\fixture.bin",
                "shader"u8.ToArray());

            using var service = NifBrowserService.CreateFromBsa(meshes, [explicitRoot]);

            Assert.Equal(
                [explicitRoot, materials, textures01, textures02],
                service.TexturePaths);
            Assert.DoesNotContain(shaders, service.TexturePaths);

            // The resolver's cheap dependency identity proves the exact canonical CDB entry is
            // reachable through the merged set; a filename-only Materials.ba2 assertion would not.
            using var resolver = new NifTextureResolver(service.TexturePaths);
            var identity = Assert.IsType<string>(resolver.StarfieldMaterialDatabaseCacheIdentity);
            Assert.Contains(Path.GetFullPath(materials), identity, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"assetPath={MaterialDatabasePath}", identity, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateFromBsa_DeduplicatesAnExplicitSiblingWithoutLosingItsPrecedence()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_sf_dedupe_").FullName;
        try
        {
            var meshes = WriteGnrl(
                tempRoot,
                "Starfield - Meshes01.ba2",
                @"meshes\test\fixture.nif",
                "nif"u8.ToArray());
            var materials = WriteGnrl(
                tempRoot,
                "Starfield - Materials.ba2",
                MaterialDatabasePath,
                "synthetic-cdb"u8.ToArray());
            var textures = Path.Combine(tempRoot, "Starfield - Textures01.ba2");
            File.WriteAllBytes(textures, BuildDx10Ba2());

            var relativeMaterials = Path.GetRelativePath(Environment.CurrentDirectory, materials);
            using var service = NifBrowserService.CreateFromBsa(
                meshes,
                [relativeMaterials, textures]);

            Assert.Equal(relativeMaterials, service.TexturePaths[0]);
            Assert.Equal(textures, service.TexturePaths[1]);
            Assert.Equal(2, service.TexturePaths.Length);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string WriteGnrl(
        string directory,
        string fileName,
        string virtualPath,
        byte[] payload)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(
            path,
            ArchiveReaderTests.BuildGnrlBa2(0x5151u, virtualPath, payload));
        return path;
    }

    /// <summary>
    ///     Header-only version-1 DX10 archive. Discovery classifies DX10 by its content tag, exactly
    ///     as it does for each retail Starfield texture shard; no texture decode is needed here.
    /// </summary>
    private static byte[] BuildDx10Ba2()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("BTDX"u8.ToArray());
        writer.Write(1u);
        writer.Write("DX10"u8.ToArray());
        writer.Write(1u);
        writer.Write(0ul); // no name table

        writer.Write(0x1111u); // name hash
        writer.Write(new byte[] { (byte)'d', (byte)'d', (byte)'s', 0 });
        writer.Write(0x2222u); // directory hash
        writer.Write((byte)0);
        writer.Write((byte)0); // zero chunks: sufficient for source classification/index wiring
        writer.Write((ushort)24);
        writer.Write((ushort)4);
        writer.Write((ushort)4);
        writer.Write((byte)1);
        writer.Write((byte)71); // BC1_UNORM
        writer.Write((byte)0);
        writer.Write((byte)8);
        writer.Flush();
        return stream.ToArray();
    }
}
