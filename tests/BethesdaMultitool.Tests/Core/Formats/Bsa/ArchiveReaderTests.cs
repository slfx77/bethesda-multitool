using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Bsa;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Bsa;

/// <summary>
///     Locks the unified <see cref="ArchiveReader" /> mesh accessor: opening a BA2 by magic, listing its
///     entries, and round-tripping a file via both <see cref="ArchiveReader.Extract" /> (by listed entry)
///     and <see cref="ArchiveReader.ReadFile" /> (by virtual path, / or \). This is the abstraction the
///     CLI <c>render --archive</c> batch and the GUI NIF browser use to accept BSA or BA2 interchangeably.
/// </summary>
public class ArchiveReaderTests
{
    private static readonly byte[] NifPayload = "archive-reader-nif-payload"u8.ToArray();

    [Fact]
    public void Open_Ba2_ListsAndRoundTripsFile()
    {
        var ba2 = Path.Combine(Path.GetTempPath(), $"arcrdr_{Guid.NewGuid():N}.ba2");
        File.WriteAllBytes(ba2, BuildGnrlBa2(0x4242, "meshes\\clutter\\bottle.nif", NifPayload));
        try
        {
            using var reader = ArchiveReader.Open(ba2);

            Assert.True(reader.IsBa2);

            var entries = reader.ListFiles();
            Assert.Single(entries);
            Assert.Equal("meshes\\clutter\\bottle.nif", entries[0].FullPath);
            Assert.Equal(NifPayload, reader.Extract(entries[0]));

            // ReadFile by path, accepting either separator; a miss returns null.
            Assert.Equal(NifPayload, reader.ReadFile("meshes\\clutter\\bottle.nif"));
            Assert.Equal(NifPayload, reader.ReadFile("meshes/clutter/bottle.nif"));
            Assert.Null(reader.ReadFile("meshes\\clutter\\missing.nif"));
        }
        finally
        {
            File.Delete(ba2);
        }
    }

    [Fact]
    public void Open_Ba2_ResolvesEntryMetadataAndStats()
    {
        var ba2 = Path.Combine(Path.GetTempPath(), $"arcrdr_{Guid.NewGuid():N}.ba2");
        File.WriteAllBytes(ba2, BuildGnrlBa2(0x4242, "meshes\\clutter\\bottle.nif", NifPayload));
        try
        {
            using var reader = ArchiveReader.Open(ba2);

            Assert.Equal("BA2", reader.FormatName);
            Assert.Equal("PC", reader.PlatformLabel);
            Assert.Equal(1, reader.TotalFiles);

            var entry = Assert.Single(reader.ListFiles());
            Assert.Equal("meshes\\clutter\\bottle.nif", entry.FullPath);
            Assert.Equal("meshes\\clutter", entry.FolderPath);
            Assert.Equal("bottle.nif", entry.Name);
            Assert.Equal(".nif", entry.Extension);
            Assert.Equal(NifPayload.Length, entry.Size);
            Assert.False(entry.Compressed);

            Assert.Equal(1, reader.GetExtensionStats()[".nif"]);
            Assert.Equal(1, reader.GetFolderStats()["meshes\\clutter"]);
        }
        finally
        {
            File.Delete(ba2);
        }
    }

    [Fact]
    public async Task ExtractToDiskAsync_Ba2_WritesEntryToDisk()
    {
        var ba2 = Path.Combine(Path.GetTempPath(), $"arcrdr_{Guid.NewGuid():N}.ba2");
        var outDir = Path.Combine(Path.GetTempPath(), $"arcrdr_out_{Guid.NewGuid():N}");
        File.WriteAllBytes(ba2, BuildGnrlBa2(0x4242, "meshes\\clutter\\bottle.nif", NifPayload));
        try
        {
            using var reader = ArchiveReader.Open(ba2);
            var entry = reader.ListFiles()[0];

            Assert.True(await reader.ExtractToDiskAsync(entry, outDir));

            var written = Path.Combine(outDir, "meshes\\clutter\\bottle.nif");
            Assert.True(File.Exists(written));
            Assert.Equal(NifPayload, await File.ReadAllBytesAsync(written));
        }
        finally
        {
            File.Delete(ba2);
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, true);
            }
        }
    }

    [Fact]
    public void Open_Bsa_ListsEntriesAndResolvesMetadata()
    {
        var bsa = Path.Combine(Path.GetTempPath(), $"arcrdr_{Guid.NewGuid():N}.bsa");
        BuildBsa(bsa, ("meshes\\clutter\\bottle.nif", NifPayload));
        try
        {
            using var reader = ArchiveReader.Open(bsa);

            Assert.False(reader.IsBa2);
            Assert.Equal("BSA", reader.FormatName);
            Assert.Equal(1, reader.TotalFiles);

            var entry = Assert.Single(reader.ListFiles());
            Assert.Equal("meshes\\clutter\\bottle.nif", entry.FullPath);
            Assert.Equal("meshes\\clutter", entry.FolderPath);
            Assert.Equal("bottle.nif", entry.Name);
            Assert.Equal(".nif", entry.Extension);
            Assert.Equal(NifPayload.Length, entry.Size);

            // Same Extract / ReadFile / stats surface as the BA2 path.
            Assert.Equal(NifPayload, reader.Extract(entry));
            Assert.Equal(NifPayload, reader.ReadFile("meshes/clutter/bottle.nif"));
            Assert.Equal(1, reader.GetFolderStats()["meshes\\clutter"]);
        }
        finally
        {
            File.Delete(bsa);
        }
    }

    /// <summary>Builds an uncompressed, no-embedded-names BSA with the given entries.</summary>
    private static void BuildBsa(string bsaPath, params (string Path, byte[] Data)[] files)
    {
        using var writer = new BsaWriter(false, embedFileNames: false);
        foreach (var (path, data) in files)
        {
            writer.AddFile(path, data);
        }

        writer.Write(bsaPath);
    }

    /// <summary>Minimal version-1 GNRL BA2 with one uncompressed entry at <paramref name="name" />.</summary>
    private static byte[] BuildGnrlBa2(uint nameHash, string name, byte[] data)
    {
        const int headerSize = 24;
        const int recordSize = 36;
        long dataOffset = headerSize + recordSize;
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
