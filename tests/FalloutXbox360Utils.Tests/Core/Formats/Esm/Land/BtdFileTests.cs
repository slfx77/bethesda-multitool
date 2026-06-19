using System.IO.Compression;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Land.Btd;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Land;

/// <summary>
///     Tests for the Bethesda Terrain Data (<c>.btd</c>) reader (Fallout 76 / Starfield landscape).
///     The header / offset-chain parsing and the simpler Starfield block path (direct 128x128, no LOD
///     interleave) are synthesizable and exercised here; the more intricate Fallout 76 multi-LOD
///     interleave is verified against the real <c>Appalachia.btd</c> via <c>btd info</c>.
/// </summary>
public class BtdFileTests
{
    [Fact]
    public void Header_ParsesFallout76Fields()
    {
        var path = WriteTemp(BuildFallout76Header(3, 2));
        try
        {
            using var btd = new BtdFile(path);

            Assert.False(btd.IsStarfield);
            Assert.Equal(-1, btd.CellMinX);
            Assert.Equal(-1, btd.CellMinY);
            Assert.Equal(0, btd.CellMaxX);
            Assert.Equal(0, btd.CellMaxY);
            Assert.Equal(2, btd.NCellsX);
            Assert.Equal(2, btd.NCellsY);
            Assert.Equal(-700.0f, btd.MinHeight, 0.01f);
            Assert.Equal(38210.0f, btd.MaxHeight, 0.01f);
            Assert.Equal(3, btd.LandTextureCount);
            Assert.Equal(2, btd.GroundCoverCount);
            Assert.Equal(0x1000u, btd.GetLandTexture(0));
            Assert.Equal(0x1002u, btd.GetLandTexture(2));
            Assert.Equal(0u, btd.GetLandTexture(3)); // out of range
            Assert.Equal(0x2001u, btd.GetGroundCover(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Header_DetectsStarfieldAndDerivesBounds()
    {
        // All-zero cell bounds with a 256x256-sample (2x2 cell) resolution => Starfield variant.
        var path = WriteTemp(BuildStarfieldBtd(256, 256, null));
        try
        {
            using var btd = new BtdFile(path);

            Assert.True(btd.IsStarfield);
            Assert.Equal(-1, btd.CellMinX); // -(256 >> 8)
            Assert.Equal(0, btd.CellMaxX); // -1 + (256 >> 7) - 1
            Assert.Equal(2, btd.NCellsX);
            Assert.Equal(2, btd.NCellsY);
            Assert.Equal(0, btd.GroundCoverCount); // Starfield has no ground cover
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Starfield_DecodesCellHeightMap_RoundTrip()
    {
        // 1x1 Starfield grid (128x128 samples). Encode a unique height per vertex and read it back,
        // exercising the header, offset chain, zlib decompression, tile cache and the SF block load.
        var heights = new ushort[128 * 128];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = (ushort)i;
        }

        var path = WriteTemp(BuildStarfieldBtd(128, 128, heights));
        try
        {
            using var btd = new BtdFile(path);
            Assert.True(btd.IsStarfield);
            Assert.Equal(1, btd.NCellsX);
            Assert.Equal(1, btd.NCellsY);

            var buf = new ushort[128 * 128];
            btd.GetCellHeightMap(buf, btd.CellMinX, btd.CellMinY);

            for (var i = 0; i < buf.Length; i++)
            {
                Assert.Equal(heights[i], buf[i]);
            }

            // SampleToHeight is a linear remap of 0..65535 onto MinHeight..MaxHeight.
            Assert.Equal(btd.MinHeight, btd.SampleToHeight(0), 0.01f);
            Assert.Equal(btd.MaxHeight, btd.SampleToHeight(65535), 0.01f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("XXXX", 6u)] // bad magic
    [InlineData("BTDB", 4u)] // unsupported version
    [InlineData("BTDB", 7u)] // unsupported version
    public void InvalidHeader_Throws(string magic, uint version)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, true))
        {
            bw.Write(Encoding.ASCII.GetBytes(magic));
            bw.Write(version);
            bw.Write(new byte[64]); // padding so the reader has bytes to (attempt to) read
        }

        var path = WriteTemp(ms.ToArray());
        try
        {
            Assert.Throws<InvalidDataException>(() => new BtdFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"btd_{Guid.NewGuid():N}.btd");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Minimal Fallout 76 header (2x2 cells), valid up to and including the GCVR form-ID table.</summary>
    private static byte[] BuildFallout76Header(int ltexCnt, int gcvrCnt)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("BTDB"u8.ToArray());
        bw.Write(6u); // version
        bw.Write(-700.0f); // min height
        bw.Write(38210.0f); // max height
        bw.Write(256); // resX (unused for bounds in FO76)
        bw.Write(256); // resY
        bw.Write(-1); // cellMinX  (non-zero bounds => Fallout 76, not Starfield)
        bw.Write(-1); // cellMinY
        bw.Write(0); // cellMaxX
        bw.Write(0); // cellMaxY
        bw.Write((uint)ltexCnt);
        for (var i = 0; i < ltexCnt; i++)
        {
            bw.Write((uint)(0x1000 + i));
        }

        // In the real layout the per-cell min/max map (nCells*8) and the LTEX quadrant map (nCells*32)
        // sit BETWEEN the LTEX form-ID table and the GCVR count, so the GCVR count is read after them.
        const long nCells = 2 * 2;
        bw.Write(new byte[nCells * 8 + nCells * 32]);

        bw.Write((uint)gcvrCnt);
        for (var i = 0; i < gcvrCnt; i++)
        {
            bw.Write((uint)(0x2000 + i));
        }

        // The constructor advances past the remaining per-cell maps and block tables arithmetically
        // (no reads), so the file may end right after the GCVR table for header-field assertions.
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    ///     Builds a Starfield-variant BTD: all-zero cell bounds (bounds derived from resolution), no
    ///     ground cover, and — when <paramref name="heights" /> is supplied for a 1x1 grid — a single
    ///     LOD0 zlib block holding a 128x128 height map followed by a 128x128 land-texture map.
    /// </summary>
    private static byte[] BuildStarfieldBtd(int resX, int resY, ushort[]? heights)
    {
        long nCellsX = resX >> 7;
        long nCellsY = resY >> 7;
        var nCells = nCellsX * nCellsY;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("BTDB"u8.ToArray());
        bw.Write(6u); // version
        bw.Write(0.0f); // min height (Starfield scales by 8 => MinHeight 0)
        bw.Write(100.0f); // max height (=> MaxHeight 800)
        bw.Write(resX);
        bw.Write(resY);
        bw.Write(0); // all-zero cell bounds => Starfield
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0u); // ltexCnt

        // cellHeightMinMax (nCells*8) + ltexMap (nCells*32) + heightMapLOD4 (nCells*128)
        // + landTexturesLOD4 (nCells*128) — all zero (overwritten by the LOD0 block for the SF path).
        bw.Write(new byte[nCells * 8 + nCells * 32 + nCells * 128 + nCells * 128]);

        // Block offset tables (Starfield: one set, no terrain-color pairs).
        var lod3 = ((nCellsY + 7) >> 3) * ((nCellsX + 7) >> 3) * 8;
        var lod2 = ((nCellsY + 3) >> 2) * ((nCellsX + 3) >> 2) * 8;
        var lod1 = ((nCellsY + 1) >> 1) * ((nCellsX + 1) >> 1) * 8;
        var lod0 = nCells * 8;
        bw.Write(new byte[lod3 + lod2 + lod1]); // LOD3/LOD2/LOD1 tables (unused for LOD0 reads)

        if (heights == null)
        {
            bw.Write(new byte[lod0]); // header-only file: zero LOD0 table
            bw.Flush();
            return ms.ToArray();
        }

        // Single 1x1-grid LOD0 block: 65536 bytes = 128x128 height (u16 LE) + 128x128 land texture.
        var payload = new byte[65536];
        for (var i = 0; i < 16384; i++)
        {
            payload[i * 2] = (byte)(heights[i] & 0xFF);
            payload[i * 2 + 1] = (byte)(heights[i] >> 8);
        }

        byte[] block;
        using (var blockMs = new MemoryStream())
        {
            using (var z = new ZLibStream(blockMs, CompressionLevel.Optimal, true))
            {
                z.Write(payload, 0, payload.Length);
            }

            block = blockMs.ToArray();
        }

        // LOD0 table: a single entry { relOffset = 0, compressedSize }, then the block data.
        bw.Write(0u);
        bw.Write((uint)block.Length);
        bw.Write(block);
        bw.Flush();
        return ms.ToArray();
    }
}