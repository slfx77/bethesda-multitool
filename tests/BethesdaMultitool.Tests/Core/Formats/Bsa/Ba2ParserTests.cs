using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Bsa;

/// <summary>
///     Round-trips a synthesized BA2 (Fallout 4 / Fallout 76) archive through the parser +
///     extractor: a GNRL archive with one uncompressed and one zlib-compressed entry plus a name
///     table, and a DX10 DDS-header synthesis check. No real game assets required.
/// </summary>
public class Ba2ParserTests
{
    private static readonly byte[] PlainData = "Hello, BA2! (uncompressed)"u8.ToArray();

    private static readonly byte[] CompressibleData =
        Encoding.ASCII.GetBytes(new string('A', 4096) + "tail");

    [Fact]
    public void Parse_GnrlArchive_ReadsHeaderAndEntries()
    {
        var path = WriteGnrlBa2();
        try
        {
            var archive = Ba2Parser.Parse(path);

            Assert.Equal(Ba2HeaderType.General, archive.Header.Type);
            Assert.Equal(1u, archive.Header.Version);
            Assert.Equal(Ba2CompressionFormat.Zip, archive.Header.CompressionFormat);
            Assert.Equal(2, archive.TotalFiles);
            Assert.True(archive.Header.HasNameTable);

            Assert.Equal("data\\plain.txt", archive.Files[0].FullPath);
            Assert.Equal("data\\packed.txt", archive.Files[1].FullPath);
            Assert.False(archive.Files[0].Compressed);
            Assert.True(archive.Files[1].Compressed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtractFile_RoundTripsUncompressedAndZlibEntries()
    {
        var path = WriteGnrlBa2();
        try
        {
            using var extractor = new Ba2Extractor(path);

            var plain = extractor.ExtractFile(extractor.Archive.FindFile("data/plain.txt")!);
            var packed = extractor.ExtractFile(extractor.Archive.FindFile("data\\packed.txt")!);

            Assert.Equal(PlainData, plain);
            Assert.Equal(CompressibleData, packed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExtractAllAsync_StartsEntriesConcurrentlyAndWritesBothPayloads()
    {
        var path = WriteGnrlBa2();
        var outputDir = Path.Combine(Path.GetTempPath(), $"ba2extract_{Guid.NewGuid():N}");
        using var progress = new RendezvousProgress(2);
        try
        {
            using var extractor = new Ba2Extractor(path);

            var extracted = await extractor.ExtractAllAsync(
                outputDir,
                progress: progress,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, extracted);
            Assert.Equal(2, progress.Seen.Count);
            Assert.Equal([1, 2], progress.Seen.Select(static p => p.current).Order().ToArray());
            Assert.Equal(
                ["data\\packed.txt", "data\\plain.txt"],
                progress.Seen.Select(static p => p.fileName).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(PlainData, await File.ReadAllBytesAsync(
                Path.Combine(outputDir, "data", "plain.txt"), TestContext.Current.CancellationToken));
            Assert.Equal(CompressibleData, await File.ReadAllBytesAsync(
                Path.Combine(outputDir, "data", "packed.txt"), TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    [Fact]
    public void IsBa2File_DetectsMagic()
    {
        var path = WriteGnrlBa2();
        try
        {
            Assert.True(Ba2Parser.IsBa2File(path));
            Assert.True(Ba2Parser.IsBa2File("BTDX"u8.ToArray()));
            Assert.False(Ba2Parser.IsBa2File("BSA\0"u8.ToArray()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildDdsHeader_Bc1Texture_EmitsValidDdsMagicAndDimensions()
    {
        var tex = new Ba2TextureInfo
        {
            Unknown = 0,
            ChunkCount = 1,
            ChunkHeaderLength = 24,
            Height = 64,
            Width = 128,
            MipCount = 1,
            Format = (byte)Ba2DxgiFormat.BC1_UNORM,
            IsCubemap = 0,
            TileMode = 8, // PC default — no DXT10/Xbox trailer for BC1_UNORM
            Chunks =
            [
                new Ba2TextureChunk
                {
                    Offset = 0, PackedSize = 0, FullSize = 128 * 64 / 2, StartMip = 0, EndMip = 0, Align = 0
                }
            ]
        };

        var header = Ba2DdsHeaderWriter.BuildHeader(tex, 1);

        // magic "DDS " + 124-byte DDS_HEADER == 128 bytes (BC1_UNORM needs no DX10 header)
        Assert.Equal(128, header.Length);
        Assert.Equal((byte)'D', header[0]);
        Assert.Equal((byte)'D', header[1]);
        Assert.Equal((byte)'S', header[2]);
        Assert.Equal((byte)' ', header[3]);

        var dwHeight = BitConverter.ToUInt32(header, 4 + 8);
        var dwWidth = BitConverter.ToUInt32(header, 4 + 12);
        Assert.Equal(64u, dwHeight);
        Assert.Equal(128u, dwWidth);
    }

    [Fact]
    public void BuildDdsHeader_Bc7Texture_AppendsDxt10Header()
    {
        var tex = new Ba2TextureInfo
        {
            Unknown = 0, ChunkCount = 1, ChunkHeaderLength = 24,
            Height = 32, Width = 32, MipCount = 1,
            Format = (byte)Ba2DxgiFormat.BC7_UNORM,
            IsCubemap = 0, TileMode = 8,
            Chunks =
            [
                new Ba2TextureChunk { Offset = 0, PackedSize = 0, FullSize = 1024, StartMip = 0, EndMip = 0, Align = 0 }
            ]
        };

        var header = Ba2DdsHeaderWriter.BuildHeader(tex, 1);

        // BC7 requires the DX10 extension header: 4 (magic) + 124 (DDS_HEADER) + 20 (DXT10) = 148.
        Assert.Equal(148, header.Length);
        var dxgiFormat = BitConverter.ToUInt32(header, 128);
        Assert.Equal((uint)Ba2DxgiFormat.BC7_UNORM, dxgiFormat);
    }

    /// <summary>
    ///     Writes a minimal version-1 GNRL BA2 to a temp file: 24-byte header, two 36-byte file
    ///     records, the two data blobs, then a name table. Returns the temp path (caller deletes).
    /// </summary>
    [Theory]
    // Versions 1/7/8 have no extra dwords; 2 has two; 3 has three, the last being CompressionMethod.
    // The count depends on the VERSION ONLY — a version-3 GNRL header is 36 bytes just like DX10.
    // fo76utils sizes it at 32, which would read the record table 4 bytes early; xEdit's writer and
    // bsa-rs both agree on 36. Retail Starfield ships no v3 GNRL, so only a rebuilt archive hits it.
    [InlineData(1u, "GNRL", 0u, Ba2CompressionFormat.Zip)]
    [InlineData(2u, "GNRL", 0u, Ba2CompressionFormat.Zip)]
    [InlineData(2u, "DX10", 0u, Ba2CompressionFormat.Zip)]
    [InlineData(3u, "GNRL", 3u, Ba2CompressionFormat.Lz4)]
    [InlineData(3u, "DX10", 3u, Ba2CompressionFormat.Lz4)]
    [InlineData(3u, "GNRL", 0u, Ba2CompressionFormat.Zip)] // v3 re-saved with zlib
    [InlineData(3u, "DX10", 0u, Ba2CompressionFormat.Zip)]
    [InlineData(7u, "GNRL", 0u, Ba2CompressionFormat.Zip)]
    [InlineData(8u, "GNRL", 0u, Ba2CompressionFormat.Zip)]
    public void Parse_HeaderSizingAndCodec_FollowVersionNotTag(
        uint version, string tag, uint compressionMethod, Ba2CompressionFormat expected)
    {
        var path = WriteHeaderOnlyBa2(version, tag, compressionMethod);
        try
        {
            var archive = Ba2Parser.Parse(path);

            Assert.Equal(version, archive.Header.Version);
            Assert.Equal(expected, archive.Header.CompressionFormat);

            // The real assertion: the record table began at the right offset. A mis-sized header
            // shifts every field, so the single entry's decoded values would be garbage.
            Assert.Equal(1, archive.TotalFiles);
            Assert.Equal("txt", archive.Files[0].Extension);
            Assert.Equal(0x1111u, archive.Files[0].NameHash);
            Assert.Equal(0x2222u, archive.Files[0].DirHash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_Version2_CompressionMethodDwordIsNotConsulted()
    {
        // Guard against keying the codec off the FIRST extra dword: that is Unknown1, and it reads 1
        // in every retail archive including all the v2 ones, so a v2 archive must stay Zip regardless.
        var path = WriteHeaderOnlyBa2(2u, "DX10", 0u, 1u);
        try
        {
            Assert.Equal(Ba2CompressionFormat.Zip, Ba2Parser.Parse(path).Header.CompressionFormat);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     A minimal BA2 with one GNRL-shaped record and a name table, whose header carries the extra
    ///     dwords for <paramref name="version" />. DX10 archives use the same record start offset, so a
    ///     GNRL record body is enough to prove the header was sized correctly.
    /// </summary>
    private static string WriteHeaderOnlyBa2(
        uint version, string tag, uint compressionMethod, uint unknown1 = 1u)
    {
        var extraDwords = version switch { 2 => 2, 3 => 3, _ => 0 };
        var headerSize = 24 + extraDwords * 4;
        var dataStart = (ulong)(headerSize + 36);
        var nameTableOffset = dataStart + (ulong)PlainData.Length;

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, true))
        {
            bw.Write("BTDX"u8.ToArray());
            bw.Write(version);
            bw.Write(Encoding.ASCII.GetBytes(tag));
            bw.Write(1u); // file count
            bw.Write(nameTableOffset);
            if (extraDwords > 0)
            {
                bw.Write(unknown1);
                bw.Write(0u);
            }

            if (extraDwords > 2)
            {
                bw.Write(compressionMethod);
            }

            WriteGnrlRecord(bw, 0x1111, "txt", 0x2222, dataStart, 0, (uint)PlainData.Length);
            bw.Write(PlainData);
            WriteName(bw, "data\\plain.txt");
        }

        var path = Path.Combine(Path.GetTempPath(), $"ba2hdr_{Guid.NewGuid():N}.ba2");
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static string WriteGnrlBa2()
    {
        var packed = ZlibCompress(CompressibleData);

        const int headerSize = 24;
        const int recordSize = 36;
        var dataStart = headerSize + 2 * recordSize;
        var offsetPlain = (ulong)dataStart;
        var offsetPacked = offsetPlain + (ulong)PlainData.Length;
        var nameTableOffset = offsetPacked + (ulong)packed.Length;

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, true))
        {
            // Header
            bw.Write("BTDX"u8.ToArray());
            bw.Write(1u); // version
            bw.Write("GNRL"u8.ToArray());
            bw.Write(2u); // file count
            bw.Write(nameTableOffset); // name table offset

            // Record 0 — uncompressed
            WriteGnrlRecord(bw, 0x1111, "txt", 0x2222,
                offsetPlain, 0, (uint)PlainData.Length);
            // Record 1 — zlib compressed
            WriteGnrlRecord(bw, 0x3333, "txt", 0x2222,
                offsetPacked, (uint)packed.Length, (uint)CompressibleData.Length);

            // Data
            bw.Write(PlainData);
            bw.Write(packed);

            // Name table (u16 length + UTF-8 bytes)
            WriteName(bw, "data\\plain.txt");
            WriteName(bw, "data\\packed.txt");
        }

        var path = Path.Combine(Path.GetTempPath(), $"ba2test_{Guid.NewGuid():N}.ba2");
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static void WriteGnrlRecord(
        BinaryWriter bw, uint nameHash, string ext, uint dirHash, ulong offset, uint packedSize, uint realSize)
    {
        bw.Write(nameHash);
        var extBytes = new byte[4];
        Encoding.ASCII.GetBytes(ext).CopyTo(extBytes, 0);
        bw.Write(extBytes);
        bw.Write(dirHash);
        bw.Write(0u); // flags
        bw.Write(offset);
        bw.Write(packedSize);
        bw.Write(realSize);
        bw.Write(0u); // align
    }

    private static void WriteName(BinaryWriter bw, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        bw.Write((ushort)bytes.Length);
        bw.Write(bytes);
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    ///     Both extraction workers must reach progress before either is allowed to decode. A serial
    ///     implementation times out instead of turning this into a throughput/timing benchmark.
    /// </summary>
    private sealed class RendezvousProgress(int participants) :
        IProgress<(int current, int total, string fileName)>, IDisposable
    {
        private readonly Barrier _barrier = new(participants);

        public List<(int current, int total, string fileName)> Seen { get; } = [];

        public void Dispose()
        {
            _barrier.Dispose();
        }

        public void Report((int current, int total, string fileName) value)
        {
            lock (Seen)
            {
                Seen.Add(value);
            }

            if (!_barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("BA2 extraction workers did not rendezvous.");
            }
        }
    }
}