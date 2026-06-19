using System.IO.Compression;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Land;
using FalloutXbox360Utils.Core.Formats.Esm.Land.Btd;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Land;

/// <summary>
///     Tests for Fallout 76 detection (HEDR version threshold) and the BTD terrain injector's gating.
///     The full inject → render path is verified end-to-end against the real SeventySix.esm +
///     Appalachia.btd (the worldspace heightmap matches the direct BTD render); these lock the
///     detection threshold and the no-op guards that protect every other game.
/// </summary>
public class Fo76TerrainInjectorTests
{
    [Fact]
    public void Detect_Fallout76HedrVersion_ReturnsFallout76()
    {
        // SeventySix.esm's real HEDR version is 263.0 — far above every other TES4 game (max 1.7).
        var header = BuildTes4Header(263.0f);
        Assert.Equal(BethesdaGame.Fallout76, PluginFormat.Detect(header).Game);
    }

    [Theory]
    [InlineData(1.34f, BethesdaGame.FalloutNewVegas)] // FNV must NOT be swept into FO76
    [InlineData(1.32f, BethesdaGame.FalloutNewVegas)]
    public void Detect_BelowThreshold_StaysPreviousGame(float version, BethesdaGame expected)
    {
        var header = BuildTes4Header(version);
        Assert.Equal(expected, PluginFormat.Detect(header).Game);
    }

    [Fact]
    public void Inject_NonFallout76Plugin_ReturnsZero()
    {
        var path = WriteTempEsm(BuildTes4Header(1.34f)); // New Vegas version
        try
        {
            Assert.Equal(0, Fo76TerrainInjector.Inject(new RecordCollection(), path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Inject_Fallout76WithoutTerrainFolder_ReturnsZero()
    {
        // Detected as FO76, but no Data\Terrain\*.btd alongside it -> nothing to inject.
        var dir = Path.Combine(Path.GetTempPath(), $"fo76inj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "SeventySix.esm");
        File.WriteAllBytes(path, BuildTes4Header(263.0f));
        try
        {
            Assert.Equal(0, Fo76TerrainInjector.Inject(new RecordCollection(), path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Inject_BuildsFullResGridWithSharedNeighbourEdges()
    {
        // Two adjacent BTD cells with deliberately distinct height ranges. Fallout 76 packs 128
        // *disjoint* samples per cell, so a watertight mesh needs each cell's east/north edge taken
        // from the next cell's sample 0 — this verifies the injector pulls the neighbour's edge
        // (closing the seam) instead of reusing its own sample 127, and decodes at full 129×129.
        var west = new ushort[128 * 128];
        var east = new ushort[128 * 128];
        for (var sy = 0; sy < 128; sy++)
        {
            for (var sx = 0; sx < 128; sx++)
            {
                west[(sy * 128) + sx] = (ushort)(1000 + sx + sy);   // ~14 game-units
                east[(sy * 128) + sx] = (ushort)(40000 + sx + sy);  // ~489 game-units (far from west)
            }
        }

        var dir = Path.Combine(Path.GetTempPath(), $"fo76inj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "Terrain"));
        var esmPath = Path.Combine(dir, "SeventySix.esm");
        var btdPath = Path.Combine(dir, "Terrain", "TestLand.btd");
        File.WriteAllBytes(esmPath, BuildTes4Header(263.0f));
        File.WriteAllBytes(btdPath, BuildStarfield2x1Btd(west, east));

        // Worldspace "TestLand" -> Terrain\TestLand.btd; cells (-1,0)=west and (0,0)=east (exterior).
        var westCell = new CellRecord { GridX = -1, GridY = 0, Flags = 0 };
        var eastCell = new CellRecord { GridX = 0, GridY = 0, Flags = 0 };
        var records = new RecordCollection
        {
            Worldspaces = [new WorldspaceRecord { EditorId = "TestLand", Cells = [westCell, eastCell] }]
        };

        try
        {
            Assert.Equal(2, Fo76TerrainInjector.Inject(records, esmPath));

            var wh = westCell.Heightmap!.ExactHeights!;
            var eh = eastCell.Heightmap!.ExactHeights!;
            Assert.Equal(129, wh.GetLength(0));
            Assert.Equal(129, wh.GetLength(1));

            using var btd = new BtdFile(btdPath);

            // Interior matches this cell's own samples (exact[row, col] == sample (col, row)).
            Assert.Equal(btd.GetCellHeightSample(-1, 0, 5, 7), wh[7, 5], 0.5f);
            Assert.Equal(btd.GetCellHeightSample(-1, 0, 127, 100), wh[100, 127], 0.5f);

            // Seam: west's east edge (col 128) == east neighbour's west column == east's own col 0,
            // and is far from west's own sample 127 (proving the neighbour edge was pulled, not reused).
            var ownSample = btd.GetCellHeightSample(-1, 0, 127, 50);
            Assert.Equal(btd.GetCellHeightSample(0, 0, 0, 50), wh[50, 128], 0.5f);
            Assert.Equal(eh[50, 0], wh[50, 128], 0.5f);
            Assert.True(Math.Abs(wh[50, 128] - ownSample) > 100f,
                "east edge must come from the neighbour, not this cell's sample 127");

            // East cell is the easternmost -> east edge clamps to its own sample 127.
            Assert.Equal(btd.GetCellHeightSample(0, 0, 127, 50), eh[50, 128], 0.5f);

            // No north neighbour anywhere -> north edge clamps to own sample 127.
            Assert.Equal(btd.GetCellHeightSample(-1, 0, 60, 127), wh[128, 60], 0.5f);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    ///     Builds a 2×1-cell Starfield-variant BTD (256×128 samples, all-zero bounds => cells (-1,0) and
    ///     (0,0)). The injector decodes any BTD variant the same way, and the Starfield single-LOD0
    ///     block path is synthesizable here; <paramref name="west" />/<paramref name="east" /> are each
    ///     128×128 row-major (index sy*128+sx) raw height samples.
    /// </summary>
    private static byte[] BuildStarfield2x1Btd(ushort[] west, ushort[] east)
    {
        const int nCellsX = 2, nCellsY = 1, nCells = nCellsX * nCellsY;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("BTDB"u8.ToArray());
        bw.Write(6u);       // version
        bw.Write(0.0f);     // min height (Starfield scales by 8 => 0)
        bw.Write(100.0f);   // max height (=> 800)
        bw.Write(256);      // resX
        bw.Write(128);      // resY
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); // all-zero bounds => Starfield variant
        bw.Write(0u);       // ltexCnt

        // cellHeightMinMax + ltexMap + heightMapLOD4 + landTexturesLOD4 (all zero; overwritten by LOD0).
        bw.Write(new byte[(nCells * 8) + (nCells * 32) + (nCells * 128) + (nCells * 128)]);

        // LOD3/LOD2/LOD1 block tables (unused for LOD0 reads).
        long lod3 = ((nCellsY + 7) >> 3) * ((nCellsX + 7) >> 3) * 8;
        long lod2 = ((nCellsY + 3) >> 2) * ((nCellsX + 3) >> 2) * 8;
        long lod1 = ((nCellsY + 1) >> 1) * ((nCellsX + 1) >> 1) * 8;
        bw.Write(new byte[lod3 + lod2 + lod1]);

        // LOD0 table: { relOffset, compressedSize } per cell, n = localY*nCellsX + localX.
        var block0 = ZlibCompress(BuildCellPayload(west)); // n=0 -> local (0,0) -> world (-1,0)
        var block1 = ZlibCompress(BuildCellPayload(east)); // n=1 -> local (1,0) -> world (0,0)
        bw.Write(0u);
        bw.Write((uint)block0.Length);
        bw.Write((uint)block0.Length);
        bw.Write((uint)block1.Length);
        bw.Write(block0);
        bw.Write(block1);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>65536-byte LOD0 payload: 128×128 height (u16 LE) followed by a zeroed 128×128 land-texture map.</summary>
    private static byte[] BuildCellPayload(ushort[] heights)
    {
        var payload = new byte[65536];
        for (var i = 0; i < 16384; i++)
        {
            payload[i * 2] = (byte)(heights[i] & 0xFF);
            payload[(i * 2) + 1] = (byte)(heights[i] >> 8);
        }

        return payload;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
        {
            z.Write(data, 0, data.Length);
        }

        return ms.ToArray();
    }

    private static string WriteTempEsm(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"esm_{Guid.NewGuid():N}.esm");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Minimal little-endian TES4 file header with a HEDR subrecord carrying <paramref name="version" />.</summary>
    private static byte[] BuildTes4Header(float version)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write("TES4"u8.ToArray()); // 0
        bw.Write(0u);                 // 4  data size
        bw.Write(0u);                 // 8  flags
        bw.Write(0u);                 // 12 form ID
        bw.Write(0u);                 // 16 VCS info
        bw.Write((ushort)0);          // 20 form version
        bw.Write((ushort)0);          // 22 unknown -> record header ends at 24
        bw.Write("HEDR"u8.ToArray()); // 24 first subrecord signature
        bw.Write((ushort)12);         // 28 HEDR data length
        bw.Write(version);            // 30 version float
        bw.Write(0u);                 // 34 record count
        bw.Write(0u);                 // 38 next object ID
        bw.Flush();
        return ms.ToArray();
    }
}
