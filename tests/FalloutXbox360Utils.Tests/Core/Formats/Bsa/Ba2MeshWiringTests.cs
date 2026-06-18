using System.Text;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Bsa;

/// <summary>
///     Locks the BA2 mesh-resolution wiring: a GNRL BA2 resolves a virtual mesh path to bytes through
///     <see cref="NpcMeshArchiveSet" /> (the abstraction the 3D-viewer reference pipeline + NPC
///     pipelines consume), and a mesh-bearing GNRL BA2 is discovered into the mesh set.
/// </summary>
public class Ba2MeshWiringTests
{
    private static readonly byte[] NifPayload = "fake-nif-bytes-for-mesh-resolution"u8.ToArray();

    [Fact]
    public void NpcMeshArchiveSet_ResolvesMeshFromBa2()
    {
        var ba2 = Path.Combine(Path.GetTempPath(), $"ba2mesh_{Guid.NewGuid():N}.ba2");
        File.WriteAllBytes(ba2, BuildGnrlBa2(0x1111, "meshes\\test.nif", NifPayload, out var dataOffset));
        try
        {
            using var set = NpcMeshArchiveSet.Open(ba2, extraMeshesBsaPaths: null);

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
            Directory.Delete(dir, recursive: true);
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
        bw.Write(0x9999u);                 // dirHash
        bw.Write(0u);                      // flags
        bw.Write((ulong)dataOffset);
        bw.Write(0u);                      // packedSize 0 == uncompressed
        bw.Write((uint)data.Length);       // realSize
        bw.Write(0u);                      // align

        bw.Write(data);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        bw.Write((ushort)nameBytes.Length);
        bw.Write(nameBytes);
        bw.Flush();
        return ms.ToArray();
    }
}
