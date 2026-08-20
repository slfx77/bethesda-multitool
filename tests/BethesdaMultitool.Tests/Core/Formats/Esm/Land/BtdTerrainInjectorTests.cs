using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Land;
using BethesdaMultitool.Core.Formats.Esm.Land.Btd;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Land;

/// <summary>
///     Tests for detection (HEDR version threshold) and the BTD terrain injector's two lookup routes.
///     The full inject → render path is verified end-to-end against the real SeventySix.esm +
///     Appalachia.btd (the worldspace heightmap matches the direct BTD render); these lock the
///     detection threshold, the loose-file route, the archive route Starfield depends on, and the
///     no-op guards that protect every other game.
/// </summary>
public class BtdTerrainInjectorTests
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
    public void Inject_GameWithoutExternalBtdTerrain_ReturnsZero()
    {
        var path = WriteTempEsm(BuildTes4Header(1.34f)); // New Vegas version
        try
        {
            using var injection = BtdTerrainInjector.Inject(new RecordCollection(), path);
            Assert.Equal(0, injection.PopulatedCells);
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
            using var injection = BtdTerrainInjector.Inject(new RecordCollection(), path);
            Assert.Equal(0, injection.PopulatedCells);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Inject_BuildsFullResGridWithSharedNeighbourEdges()
    {
        // Two adjacent BTD cells with deliberately distinct height ranges. Fallout 76 packs 128
        // *disjoint* samples per cell, so a watertight mesh needs each cell's east/north edge taken
        // from the next cell's sample 0 — this verifies the injector pulls the neighbor's edge
        // (closing the seam) instead of reusing its own sample 127, and decodes at full 129×129.
        var west = new ushort[128 * 128];
        var east = new ushort[128 * 128];
        for (var sy = 0; sy < 128; sy++)
        {
            for (var sx = 0; sx < 128; sx++)
            {
                west[sy * 128 + sx] = (ushort)(1000 + sx + sy); // ~14 game-units
                east[sy * 128 + sx] = (ushort)(40000 + sx + sy); // ~489 game-units (far from west)
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
            // The loose (memory-mapped) route decodes heights LAZILY over a kept-open source, so the
            // grids below materialize on first property read and the injection must stay alive for
            // them — it is disposed (closing the mapped .btd) before the directory delete.
            using var injection = BtdTerrainInjector.Inject(records, esmPath);
            Assert.Equal(2, injection.PopulatedCells);
            Assert.True(injection.HasOpenSources, "loose-file route should keep a lazy height source open");

            var wh = westCell.Heightmap!.ExactHeights!;
            var eh = eastCell.Heightmap!.ExactHeights!;
            Assert.Equal(129, wh.GetLength(0));
            Assert.Equal(129, wh.GetLength(1));

            using var btd = new BtdFile(btdPath);

            // Interior matches this cell's own samples (exact[row, col] == sample (col, row)).
            Assert.Equal(btd.GetCellHeightSample(-1, 0, 5, 7), wh[7, 5], 0.5f);
            Assert.Equal(btd.GetCellHeightSample(-1, 0, 127, 100), wh[100, 127], 0.5f);

            // Seam: west's east edge (col 128) == east neighbor's west column == east's own col 0,
            // and is far from west's own sample 127 (proving the neighbor edge was pulled, not reused).
            var ownSample = btd.GetCellHeightSample(-1, 0, 127, 50);
            Assert.Equal(btd.GetCellHeightSample(0, 0, 0, 50), wh[50, 128], 0.5f);
            Assert.Equal(eh[50, 0], wh[50, 128], 0.5f);
            Assert.True(Math.Abs(wh[50, 128] - ownSample) > 100f,
                "east edge must come from the neighbor, not this cell's sample 127");

            // East cell is the easternmost -> east edge clamps to its own sample 127.
            Assert.Equal(btd.GetCellHeightSample(0, 0, 127, 50), eh[50, 128], 0.5f);

            // No north neighbor anywhere -> north edge clamps to own sample 127.
            Assert.Equal(btd.GetCellHeightSample(-1, 0, 60, 127), wh[128, 60], 0.5f);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Inject_Starfield_ResolvesBtdFromArchive()
    {
        // Starfield ships NO loose assets: every terrain\<worldspaceEditorId>.btd lives inside
        // Starfield - Terrain01..04.ba2. This is the route that makes Starfield terrain render at all.
        var west = FillSamples(1000);
        var east = FillSamples(40000);

        var dir = Path.Combine(Path.GetTempPath(), $"sfinj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var esmPath = Path.Combine(dir, "Starfield.esm");
        File.WriteAllBytes(esmPath, BuildTes4Header(0.96f));
        File.WriteAllBytes(
            Path.Combine(dir, "Starfield - Terrain01.ba2"),
            BuildGnrlBa2(@"terrain\TestLand.btd", BuildStarfield2x1Btd(west, east)));

        var westCell = new CellRecord { GridX = -1, GridY = 0, Flags = 0 };
        var eastCell = new CellRecord { GridX = 0, GridY = 0, Flags = 0 };
        var records = new RecordCollection
        {
            Worldspaces = [new WorldspaceRecord { EditorId = "TestLand", Cells = [westCell, eastCell] }]
        };

        try
        {
            using var injection = BtdTerrainInjector.Inject(records, esmPath);
            Assert.Equal(2, injection.PopulatedCells);
            Assert.Equal(129, westCell.Heightmap!.ExactHeights!.GetLength(0));

            // Same seam contract as the loose path: west's east edge comes from the east neighbor.
            Assert.Equal(eastCell.Heightmap!.ExactHeights![50, 0], westCell.Heightmap.ExactHeights![50, 128], 0.5f);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Inject_WorldspaceWithNoGriddedCells_NeverOpensTheArchive()
    {
        // The pre-check matters at Starfield's scale: 753 worldspace BTDs ship, but only the ones with
        // gridded exterior cells in this plugin are worth resolving. A deliberately corrupt payload
        // stands in for "any read at all" — reaching it would throw InvalidDataException out of BtdFile.
        var dir = Path.Combine(Path.GetTempPath(), $"sfinj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var esmPath = Path.Combine(dir, "Starfield.esm");
        File.WriteAllBytes(esmPath, BuildTes4Header(0.96f));
        File.WriteAllBytes(
            Path.Combine(dir, "Starfield - Terrain01.ba2"),
            BuildGnrlBa2(@"terrain\TestLand.btd", "NOT A BTD AT ALL"u8.ToArray()));

        var records = new RecordCollection
        {
            Worldspaces =
            [
                new WorldspaceRecord
                {
                    EditorId = "TestLand",
                    // Interior + ungridded only: nothing the injector could populate.
                    Cells =
                    [
                        new CellRecord { Flags = 0x01 },
                        new CellRecord { Flags = 0 }
                    ]
                }
            ]
        };

        try
        {
            using var injection = BtdTerrainInjector.Inject(records, esmPath);
            Assert.Equal(0, injection.PopulatedCells);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static ushort[] FillSamples(int baseValue)
    {
        var samples = new ushort[128 * 128];
        for (var sy = 0; sy < 128; sy++)
        {
            for (var sx = 0; sx < 128; sx++)
            {
                samples[sy * 128 + sx] = (ushort)(baseValue + sx + sy);
            }
        }

        return samples;
    }

    /// <summary>
    ///     Minimal BA2 v2 / GNRL archive holding one uncompressed entry — the exact shape Starfield's
    ///     terrain archives use (header 32 bytes, 36-byte records, <c>packedSize == 0</c>).
    /// </summary>
    private static byte[] BuildGnrlBa2(string virtualPath, byte[] payload)
    {
        var name = virtualPath.Replace('\\', '/');
        var extension = Path.GetExtension(name).TrimStart('.');
        const int headerSize = 32, recordSize = 36;
        var dataOffset = headerSize + recordSize;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write("BTDX"u8.ToArray());
        bw.Write(2u); // version
        bw.Write("GNRL"u8.ToArray());
        bw.Write(1u); // file count
        bw.Write((ulong)(dataOffset + payload.Length)); // name-table offset
        bw.Write(1u); // v2 extra dword 1
        bw.Write(0u); // v2 extra dword 2

        bw.Write(0u); // name hash (unused: the name table supplies the path)
        var ext = Encoding.ASCII.GetBytes(extension.PadRight(4, '\0'));
        bw.Write(ext, 0, 4);
        bw.Write(0u); // directory hash
        bw.Write(0u); // flags
        bw.Write((ulong)dataOffset);
        bw.Write(0u); // packed size: 0 => stored uncompressed
        bw.Write((uint)payload.Length);
        bw.Write(0xBAADF00Du);

        bw.Write(payload);

        var nameBytes = Encoding.ASCII.GetBytes(name);
        bw.Write((ushort)nameBytes.Length);
        bw.Write(nameBytes);
        bw.Flush();
        return ms.ToArray();
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
        bw.Write(6u); // version
        bw.Write(0.0f); // min height, reported verbatim (Starfield heights are NOT rescaled)
        bw.Write(800.0f); // max height — wide enough that the seam assertions below stay well separated
        bw.Write(256); // resX
        bw.Write(128); // resY
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0); // all-zero bounds => Starfield variant
        bw.Write(0u); // ltexCnt

        // cellHeightMinMax + ltexMap + heightMapLOD4 + landTexturesLOD4 (all zero; overwritten by LOD0).
        bw.Write(new byte[nCells * 8 + nCells * 32 + nCells * 128 + nCells * 128]);

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
            payload[i * 2 + 1] = (byte)(heights[i] >> 8);
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
        bw.Write(0u); // 4  data size
        bw.Write(0u); // 8  flags
        bw.Write(0u); // 12 form ID
        bw.Write(0u); // 16 VCS info
        bw.Write((ushort)0); // 20 form version
        bw.Write((ushort)0); // 22 unknown -> record header ends at 24
        bw.Write("HEDR"u8.ToArray()); // 24 first subrecord signature
        bw.Write((ushort)12); // 28 HEDR data length
        bw.Write(version); // 30 version float
        bw.Write(0u); // 34 record count
        bw.Write(0u); // 38 next object ID
        bw.Flush();
        return ms.ToArray();
    }
}