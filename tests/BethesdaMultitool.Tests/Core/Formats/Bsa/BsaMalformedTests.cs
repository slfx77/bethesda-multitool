using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Bsa;

/// <summary>
///     Locks the BSA read-path malformed-input contract: header count lies and out-of-range file
///     records throw <see cref="InvalidDataException" /> (the CLI catches broadly), with bounds
///     validated BEFORE any allocation is sized from them. Fixtures are minimal synthetic v104
///     archives written to a per-class temp dir — the extractor is memory-mapped, so bytes must
///     live on disk.
/// </summary>
public sealed class BsaMalformedTests : IDisposable
{
    private static readonly byte[] Payload = "bsa-malformed-roundtrip-payload"u8.ToArray();

    private readonly string _tempDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"bsa_malformed_{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // best-effort temp-dir cleanup; a lingering handle must not fail the test run
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort temp-dir cleanup; a lingering handle must not fail the test run
        }
    }

    private string WriteBsa(byte[] bytes)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.bsa");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    ///     Minimal valid v104 BSA: one folder ("meshes"), one uncompressed file ("test.nif"),
    ///     directory + file names present, no default compression, no embedded names. The single
    ///     file record's size/offset (and compression-toggle bit 30) can be overridden to author
    ///     malformed entries without disturbing the rest of the layout.
    /// </summary>
    private static byte[] BuildV104Bsa(
        byte[] payload,
        uint? fileOffsetOverride = null,
        uint? fileSizeOverride = null,
        bool compressionToggle = false,
        uint archiveFlags = 0x3u)
    {
        const string folderName = "meshes";
        const string fileName = "test.nif";
        // 36 header + 16 folder record + (1 + 7) folder name + 16 file record + 9 file name buffer
        const uint dataOffset = 85;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII);
        bw.Write("BSA\0"u8.ToArray());
        bw.Write(104u); // version
        bw.Write(36u); // folder record offset
        bw.Write(archiveFlags); // default: IncludeDirectoryNames | IncludeFileNames
        bw.Write(1u); // folder count
        bw.Write(1u); // file count
        bw.Write((uint)(folderName.Length + 1)); // total folder name length
        bw.Write((uint)(fileName.Length + 1)); // total file name length
        bw.Write((ushort)0x1); // file flags: meshes
        bw.Write((ushort)0); // padding

        bw.Write(0x1122334455667788ul); // folder name hash
        bw.Write(1u); // folder file count
        bw.Write(0u); // folder offset (informational)

        bw.Write((byte)(folderName.Length + 1)); // folder name length incl. null
        bw.Write(Encoding.ASCII.GetBytes(folderName));
        bw.Write((byte)0);

        bw.Write(0x99AABBCCDDEEFF00ul); // file name hash
        var rawSize = fileSizeOverride ?? (uint)payload.Length;
        if (compressionToggle)
        {
            rawSize |= 0x40000000;
        }

        bw.Write(rawSize);
        bw.Write(fileOffsetOverride ?? dataOffset);

        bw.Write(Encoding.ASCII.GetBytes(fileName));
        bw.Write((byte)0);

        bw.Write(payload);
        return ms.ToArray();
    }

    [Fact]
    public void Parse_FolderCountExceedsFileLength_ThrowsInvalidDataException()
    {
        var bytes = BuildV104Bsa(Payload);
        BinaryTestWriter.WriteUInt32LE(bytes, 16, 100_000); // folder count: 1.6 MB of records

        var ex = Assert.Throws<InvalidDataException>(() => BsaParser.Parse(WriteBsa(bytes)));
        Assert.Contains("folder count", ex.Message);
    }

    [Fact]
    public void Parse_FileCountExceedsFileLength_ThrowsInvalidDataException()
    {
        var bytes = BuildV104Bsa(Payload);
        BinaryTestWriter.WriteUInt32LE(bytes, 20, 50_000_000); // file count: 800 MB of records

        var ex = Assert.Throws<InvalidDataException>(() => BsaParser.Parse(WriteBsa(bytes)));
        Assert.Contains("file count", ex.Message);
    }

    [Fact]
    public void Parse_TruncatedHeader_ThrowsInvalidDataException()
    {
        // Cut mid-header (magic + version + offset only).
        var truncated = BuildV104Bsa(Payload)[..12];
        Assert.Throws<InvalidDataException>(() => BsaParser.Parse(WriteBsa(truncated)));

        // The existing magic/version checks keep throwing the same exception type.
        var badVersion = BuildV104Bsa(Payload);
        BinaryTestWriter.WriteUInt32LE(badVersion, 4, 999);
        Assert.Throws<InvalidDataException>(() => BsaParser.Parse(WriteBsa(badVersion)));
    }

    [Fact]
    public void ExtractFile_OffsetBeyondArchive_ThrowsInvalidDataException()
    {
        var bytes = BuildV104Bsa(Payload, 0x10000);
        using var extractor = new BsaExtractor(WriteBsa(bytes));
        var record = Assert.Single(extractor.Archive.AllFiles);

        var ex = Assert.Throws<InvalidDataException>(() => extractor.ExtractFile(record));
        Assert.Contains("outside", ex.Message);
    }

    [Fact]
    public void ExtractFile_SizeBeyondArchive_ThrowsInvalidDataException()
    {
        var bytes = BuildV104Bsa(Payload, fileSizeOverride: 1_000_000);
        using var extractor = new BsaExtractor(WriteBsa(bytes));
        var record = Assert.Single(extractor.Archive.AllFiles);

        var ex = Assert.Throws<InvalidDataException>(() => extractor.ExtractFile(record));
        Assert.Contains("outside", ex.Message);
    }

    [Fact]
    public void ExtractFile_UncompressedSizeAbsurd_ThrowsInvalidDataException()
    {
        // Compression-toggled entry whose leading size dword claims a ~4 GB decompressed payload.
        var entryData = new byte[12];
        BinaryTestWriter.WriteUInt32LE(entryData, 0, 0xFFFFFFF0);
        var bytes = BuildV104Bsa(entryData, compressionToggle: true);
        using var extractor = new BsaExtractor(WriteBsa(bytes));
        var record = Assert.Single(extractor.Archive.AllFiles);

        var ex = Assert.Throws<InvalidDataException>(() => extractor.ExtractFile(record));
        Assert.Contains("uncompressed size", ex.Message);
    }

    [Fact]
    public void ExtractFile_XMemCodecEntry_ThrowsNamedNotSupported()
    {
        // 0x0203 = XMemCodec | IncludeFileNames | IncludeDirectoryNames. Without the explicit
        // guard this falls into the zlib branch and surfaces as an opaque deflate error, which is
        // how an unsupported-codec archive ends up looking like a corrupt one.
        var entryData = new byte[12];
        BinaryTestWriter.WriteUInt32LE(entryData, 0, 8);
        var bytes = BuildV104Bsa(entryData, compressionToggle: true, archiveFlags: 0x203u);
        using var extractor = new BsaExtractor(WriteBsa(bytes));
        var record = Assert.Single(extractor.Archive.AllFiles);

        Assert.True(extractor.Archive.Header.UsesXMemCodec);
        var ex = Assert.Throws<NotSupportedException>(() => extractor.ExtractFile(record));
        Assert.Contains("XMem/LZX", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Fixture-correctness control: the untouched builder output must round-trip.</summary>
    [Fact]
    public void ExtractFile_ValidUncompressedFile_RoundTrips()
    {
        using var extractor = new BsaExtractor(WriteBsa(BuildV104Bsa(Payload)));

        var record = Assert.Single(extractor.Archive.AllFiles);
        Assert.Equal("meshes\\test.nif", record.FullPath);
        Assert.Equal(Payload, extractor.ExtractFile(record));
    }
}