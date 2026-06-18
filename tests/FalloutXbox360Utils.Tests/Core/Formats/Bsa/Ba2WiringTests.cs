using System.Text;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Bsa;

/// <summary>
///     Locks the BA2 asset-resolution wiring: a DX10 BA2 in a Data folder is discovered as a texture
///     archive, and the texture-source factory builds a working BA2-backed source that resolves a
///     virtual texture path to bytes through the same <c>INifTextureSource</c> path BSA uses.
/// </summary>
public class Ba2WiringTests
{
    [Fact]
    public void Discover_Dx10Ba2_ClassifiedAsTextureArchive()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ba2disc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var ba2 = Path.Combine(dir, "SeventySix - Textures.ba2");
            File.WriteAllBytes(ba2, BuildDx10Ba2());
            // BsaDiscovery globs the directory of the given plugin path.
            var esmPath = Path.Combine(dir, "Game.esm");

            var result = BsaDiscovery.Discover(esmPath);

            Assert.Contains(ba2, result.TexturesBsaPaths);
            Assert.DoesNotContain(ba2, result.MeshesBsaPaths); // DX10 is textures-only
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Factory_Ba2WithTexturePath_ResolvesRawBytesViaTextureSource()
    {
        var payload = "not-a-real-dds-but-raw-bytes-roundtrip"u8.ToArray();
        var ba2 = Path.Combine(Path.GetTempPath(), $"ba2fac_{Guid.NewGuid():N}.ba2");
        File.WriteAllBytes(ba2, BuildGnrlBa2WithTexture("textures\\test.dds", payload));
        try
        {
            var sources = NifTextureArchiveSourceFactory.Create(ba2);
            try
            {
                var source = Assert.Single(sources);
                Assert.IsType<Ba2TextureArchiveSource>(source);

                Assert.Equal(payload, source.TryLoadRaw("textures\\test.dds"));
                Assert.Equal(payload, source.TryLoadRaw("textures/test.dds".Replace('/', '\\')));
                Assert.Null(source.TryLoadRaw("textures\\missing.dds"));
            }
            finally
            {
                foreach (var s in sources)
                {
                    s.Dispose();
                }
            }
        }
        finally
        {
            File.Delete(ba2);
        }
    }

    /// <summary>Minimal version-1 DX10 BA2: one texture entry with zero chunks, no name table.</summary>
    private static byte[] BuildDx10Ba2()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write("BTDX"u8.ToArray());
        bw.Write(1u);                         // version
        bw.Write("DX10"u8.ToArray());
        bw.Write(1u);                         // file count
        bw.Write(0ul);                        // no name table

        // DX10 record header (24 bytes), 0 chunks
        bw.Write(0x1111u);                    // nameHash
        bw.Write(Ext("dds"));
        bw.Write(0x2222u);                    // dirHash
        bw.Write((byte)0);                    // unk1
        bw.Write((byte)0);                    // numChunks
        bw.Write((ushort)24);                 // chunkHdrLen
        bw.Write((ushort)64);                 // height
        bw.Write((ushort)64);                 // width
        bw.Write((byte)1);                    // numMips
        bw.Write((byte)71);                   // format (BC1_UNORM)
        bw.Write((byte)0);                    // isCubemap
        bw.Write((byte)8);                    // tileMode (PC)
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Minimal version-1 GNRL BA2 with one uncompressed entry named via the name table.</summary>
    private static byte[] BuildGnrlBa2WithTexture(string name, byte[] data)
    {
        const int headerSize = 24;
        const int recordSize = 36;
        var offset = (ulong)(headerSize + recordSize);
        var nameTableOffset = offset + (ulong)data.Length;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write("BTDX"u8.ToArray());
        bw.Write(1u);
        bw.Write("GNRL"u8.ToArray());
        bw.Write(1u);
        bw.Write(nameTableOffset);

        bw.Write(0x1111u);                    // nameHash
        bw.Write(Ext("dds"));
        bw.Write(0x2222u);                    // dirHash
        bw.Write(0u);                         // flags
        bw.Write(offset);
        bw.Write(0u);                         // packedSize 0 == uncompressed
        bw.Write((uint)data.Length);          // realSize
        bw.Write(0u);                         // align

        bw.Write(data);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        bw.Write((ushort)nameBytes.Length);
        bw.Write(nameBytes);
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] Ext(string ext)
    {
        var bytes = new byte[4];
        Encoding.ASCII.GetBytes(ext).CopyTo(bytes, 0);
        return bytes;
    }
}
