using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Bsa;

/// <summary>
///     Pins the BSA archive-flag heuristic against vanilla FNV archive shapes. Texture-only
///     archives use <c>EmbedFileNames</c> (0x100), while DLC Main-style mixed archives
///     containing DDS + NIF + voice content are uncompressed and do not embed names.
/// </summary>
public class BsaWriterFlagsTests
{
    [Fact]
    public void CreateWithAutoFlags_TexturesOnly_SetsEmbedFileNames()
    {
        using var writer = BsaWriter.CreateWithAutoFlags(["textures\\armor\\foo.dds"]);
        WriteAndAssertFlags(writer, expectedArchiveFlags: 0x107, expectedFileFlags: 0x02);
    }

    [Fact]
    public void CreateWithAutoFlags_MixedTexturesAndMeshes_UsesDlcMainLayout()
    {
        using var writer = BsaWriter.CreateWithAutoFlags([
            "meshes\\armor\\foo.nif",
            "textures\\armor\\foo.dds"
        ]);
        // Vanilla DLC Main BSAs with mixed DDS/NIF content use 0x083, not the
        // texture-only compressed+embedded 0x107 shape.
        WriteAndAssertFlags(writer, expectedArchiveFlags: 0x83, expectedFileFlags: 0x03);
    }

    [Fact]
    public void CreateWithAutoFlags_MeshesOnly_DoesNotSetEmbedFileNames()
    {
        using var writer = BsaWriter.CreateWithAutoFlags(["meshes\\armor\\foo.nif"]);
        // Matches vanilla `Fallout - Meshes.bsa`: compressed, no embedded names,
        // RetainStringsDuringStartup set.
        WriteAndAssertFlags(writer, expectedArchiveFlags: 0x87, expectedFileFlags: 0x01);
    }

    [Fact]
    public void CreateWithAutoFlags_VoicesOnly_DoesNotSetEmbedFileNames()
    {
        using var writer = BsaWriter.CreateWithAutoFlags(["sound\\voice\\foo\\bar.ogg"]);
        // Matches vanilla `Fallout - Voices1.bsa`: uncompressed and voices-only.
        WriteAndAssertFlags(writer, expectedArchiveFlags: 0x03, expectedFileFlags: 0x10);
    }

    [Fact]
    public void CreateWithAutoFlags_SoundsAndVoices_UsesVanillaSoundLayout()
    {
        using var writer = BsaWriter.CreateWithAutoFlags([
            "sound\\fx\\bar.wav",
            "sound\\voice\\foo\\bar.ogg"
        ]);
        // Matches vanilla `Fallout - Sound.bsa`: uncompressed, RetainFileNames set.
        WriteAndAssertFlags(writer, expectedArchiveFlags: 0x13, expectedFileFlags: 0x18);
    }

    [Fact]
    public void CreateWithAutoFlags_FullPluginBsa_UsesDlcMainLayout()
    {
        // The exact mix our DMP→ESP pipeline produces for a real build.
        using var writer = BsaWriter.CreateWithAutoFlags([
            "meshes\\armor\\foo.nif",
            "textures\\armor\\foo.dds",
            "sound\\fx\\bar.wav",
            "sound\\voice\\plugin\\actor\\baz.ogg"
        ]);
        // File flags: Meshes (0x01) | Textures (0x02) | Sounds (0x08) | Voices (0x10) = 0x1b.
        // Mixed texture archives follow DLC Main/Update layout: 0x083.
        WriteAndAssertFlags(writer, expectedArchiveFlags: 0x83, expectedFileFlags: 0x1b);
    }

    [Fact]
    public void Write_MixedTextureArchive_DoesNotEmbedOrCompressTextureBytes()
    {
        var ddsBytes = Encoding.ASCII.GetBytes("DDS test payload");
        var nifBytes = Encoding.ASCII.GetBytes("Gamebryo File Format");

        using var writer = BsaWriter.CreateWithAutoFlags([
            "textures\\armor\\foo.dds",
            "meshes\\armor\\foo.nif"
        ]);
        writer.AddFile("textures\\armor\\foo.dds", ddsBytes);
        writer.AddFile("meshes\\armor\\foo.nif", nifBytes);

        using var ms = new MemoryStream();
        writer.Write(ms);
        var bytes = ms.ToArray();

        ms.Position = 0;
        var archive = BsaParser.Parse(ms);
        var texture = archive.FindFile("textures\\armor\\foo.dds");

        Assert.NotNull(texture);
        Assert.Equal(0x83u, (uint)archive.Header.ArchiveFlags);
        Assert.False(texture!.CompressionToggle);
        Assert.True(ddsBytes.SequenceEqual(bytes.Skip((int)texture.Offset).Take(ddsBytes.Length)));
    }

    [Fact]
    public void HashPath_MatchesKnownVanillaTextureEntries()
    {
        var method = typeof(BsaWriter)
            .GetMethod("HashPath", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var folderHash = (ulong)method!.Invoke(null, ["textures\\characters\\male", true])!;
        var fileHash = (ulong)method.Invoke(null, ["upperbodymale_n.dds", false])!;

        Assert.Equal(0xE081F3D674186C65ul, folderHash);
        Assert.Equal(0x32803A61750FDFEEul, fileHash);
    }

    [Fact]
    public void Write_FolderRecordOffset_IncludesTotalFileNameLength()
    {
        // Bethesda BSA v104 quirk: the per-folder file-record offset stored in the folder
        // record table must have totalFileNameLength ADDED to the raw byte offset. The FNV
        // runtime subtracts that back at lookup time, so writing the raw offset leaves the
        // engine seeking into garbage memory and silently dropping every file lookup. This
        // is what caused Ulysses's BSA-packed textures to render with stale GPU memory.
        using var writer = BsaWriter.CreateWithAutoFlags([
            "textures\\armor\\foo.dds",
            "meshes\\armor\\foo.nif"
        ]);
        writer.AddFile("textures\\armor\\foo.dds", new byte[] { 1, 2, 3, 4 });
        writer.AddFile("meshes\\armor\\foo.nif", new byte[] { 5, 6, 7, 8 });

        using var ms = new MemoryStream();
        writer.Write(ms);
        var bytes = ms.ToArray();

        var folderRecordsOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var folderCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
        var totalFileNameLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28, 4));

        // First folder record's offset field (at +12 within its 16-byte record).
        var firstFolderOffset =
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)folderRecordsOffset + 12, 4));
        var fileRecordBlockStart = folderRecordsOffset + folderCount * 16;
        var delta = firstFolderOffset - fileRecordBlockStart;

        Assert.Equal(totalFileNameLength, delta);
    }

    /// <summary>
    ///     Round-trips the writer through an in-memory file and reads the on-disk header
    ///     bytes back to verify the flag values that the FNV engine will actually see.
    /// </summary>
    private static void WriteAndAssertFlags(
        BsaWriter writer,
        uint expectedArchiveFlags,
        ushort expectedFileFlags)
    {
        writer.AddFile("placeholder\\placeholder.bin", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        using var ms = new MemoryStream();
        writer.Write(ms);
        var bytes = ms.ToArray();

        Assert.True(bytes.Length >= 36, "BSA header is at least 36 bytes");

        // Header layout: magic(4) version(4) folderOff(4) archiveFlags(4) folderCount(4)
        //                fileCount(4) folderNameTotal(4) fileNameTotal(4) fileFlags(4)
        var actualArchiveFlags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        var actualFileFlags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32, 4));

        Assert.Equal(expectedArchiveFlags, actualArchiveFlags);
        Assert.Equal(expectedFileFlags, (ushort)actualFileFlags);
    }
}
