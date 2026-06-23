using System.Text;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Bsa;

/// <summary>
///     Locks the BA2 mesh-resolution wiring: a GNRL BA2 resolves a virtual mesh path to bytes through
///     <see cref="MeshArchiveSet" /> (the abstraction the 3D-viewer reference pipeline + NPC
///     pipelines consume), and a mesh-bearing GNRL BA2 is discovered into the mesh set.
/// </summary>
public class Ba2MeshWiringTests
{
    private static readonly byte[] NifPayload = "fake-nif-bytes-for-mesh-resolution"u8.ToArray();

    [Fact]
    public void MeshArchiveSet_ResolvesMeshFromBa2()
    {
        var ba2 = Path.Combine(Path.GetTempPath(), $"ba2mesh_{Guid.NewGuid():N}.ba2");
        File.WriteAllBytes(ba2, BuildGnrlBa2(0x1111, "meshes\\test.nif", NifPayload, out var dataOffset));
        try
        {
            using var set = MeshArchiveSet.Open(ba2, null);

            Assert.True(set.TryExtractFile("meshes\\test.nif", out var data, out var archivePath));
            Assert.Equal(NifPayload, data);
            Assert.Equal(Path.GetFullPath(ba2), archivePath);

            // Forward-slash input normalizes to the same entry.
            Assert.True(set.TryExtractFile("meshes/test.nif", out _, out _));
            Assert.False(set.TryExtractFile("meshes\\missing.nif", out _, out _));

            var meta = set.GetLookupMetadata("meshes\\test.nif");
            Assert.True(meta.Found);
            Assert.Equal(0x1111u, meta.FileNameHash);
            Assert.Equal((uint)dataOffset, meta.FileOffset);
            Assert.Equal((uint)NifPayload.Length, meta.FileSize);
        }
        finally
        {
            File.Delete(ba2);
        }
    }

    [Fact]
    public void Discover_GnrlBa2WithMeshes_ClassifiedAsMeshArchive()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ba2meshdisc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var ba2 = Path.Combine(dir, "SeventySix - Main.ba2");
            File.WriteAllBytes(ba2, BuildGnrlBa2(0x2222, "meshes\\architecture\\wall.nif", NifPayload, out _));

            var result = BsaDiscovery.Discover(Path.Combine(dir, "Game.esm"));

            Assert.Contains(ba2, result.MeshesBsaPaths);
            Assert.DoesNotContain(ba2, result.TexturesBsaPaths);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Discover_GnrlBa2WithMaterials_ClassifiedAsTextureArchive()
    {
        // A FO4/FO76 "… - Materials.ba2" holds materials\*.bgsm — neither meshes\ nor textures\. The
        // texture resolver follows a NIF's .bgsm material to its real textures, so the materials archive
        // must land in the TEXTURE source set; otherwise every FO76 shape resolves no diffuse (the
        // "no textures on anything" bug). It must NOT be treated as a mesh archive.
        var dir = Path.Combine(Path.GetTempPath(), $"ba2matdisc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var ba2 = Path.Combine(dir, "SeventySix - Materials.ba2");
            File.WriteAllBytes(ba2, BuildGnrlBa2(0x3333, "materials\\architecture\\wall.bgsm", NifPayload, out _));

            var result = BsaDiscovery.Discover(Path.Combine(dir, "Game.esm"));

            Assert.Contains(ba2, result.TexturesBsaPaths);
            Assert.DoesNotContain(ba2, result.MeshesBsaPaths);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DataFolderIndex_FromArchivePaths_IndexesBa2_AndResolvesExact()
    {
        // The unified backend's explicit-archive construction must index BA2 (FO4/76) the same way
        // it indexes BSA, returning a Ba2AssetSource whose Read() yields the entry bytes.
        var ba2 = Path.Combine(Path.GetTempPath(), $"ba2idx_{Guid.NewGuid():N}.ba2");
        File.WriteAllBytes(ba2, BuildGnrlBa2(0x4444, "meshes\\test.nif", NifPayload, out _));
        try
        {
            using var index = DataFolderIndex.FromArchivePaths([ba2]);

            Assert.True(index.TryResolveExact("meshes\\test.nif", out var source));
            Assert.IsType<Ba2AssetSource>(source);
            Assert.Equal(NifPayload, source.Read());
            Assert.False(index.TryResolveExact("meshes\\missing.nif", out _));
        }
        finally
        {
            File.Delete(ba2);
        }
    }

    /// <summary>Minimal version-1 GNRL BA2 with one uncompressed entry at <paramref name="name" />.</summary>
    private static byte[] BuildGnrlBa2(uint nameHash, string name, byte[] data, out long dataOffset)
    {
        const int headerSize = 24;
        const int recordSize = 36;
        dataOffset = headerSize + recordSize;
        var nameTableOffset = (ulong)(dataOffset + data.Length);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write("BTDX"u8.ToArray());
        bw.Write(1u);
        bw.Write("GNRL"u8.ToArray());
        bw.Write(1u);
        bw.Write(nameTableOffset);

        bw.Write(nameHash);
        var ext = new byte[4];
        Encoding.ASCII.GetBytes("nif").CopyTo(ext, 0);
        bw.Write(ext);
        bw.Write(0x9999u); // dirHash
        bw.Write(0u); // flags
        bw.Write((ulong)dataOffset);
        bw.Write(0u); // packedSize 0 == uncompressed
        bw.Write((uint)data.Length); // realSize
        bw.Write(0u); // align

        bw.Write(data);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        bw.Write((ushort)nameBytes.Length);
        bw.Write(nameBytes);
        bw.Flush();
        return ms.ToArray();
    }
}