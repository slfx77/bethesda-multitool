using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Bsa;

/// <summary>
///     Round-trips a synthesized BA2 (Fallout 4 / Fallout 76) archive through the ported parser +
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
}