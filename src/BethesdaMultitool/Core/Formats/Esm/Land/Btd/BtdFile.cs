// Ported from fo76utils (MIT, https://github.com/fo76utils/fo76utils) src/btdfile.cpp.
// Decodes Bethesda Terrain Data (.btd) landscape files used by Fallout 76 (Appalachia.btd)
// and — as a simplified variant — Starfield. These store the entire worldspace heightmap,
// land-texture alphas, ground cover and terrain color as a multi-LOD, zlib-compressed tile
// pyramid, instead of per-cell VHGT subrecords inside the ESM.
//
// Appalachia.btd header / layout (little-endian throughout):
//   0x00 "BTDB" magic
//   0x04 int32   format version (5 or 6)
//   0x08 float   world minimum height (e.g. -700.0)
//   0x0C float   world maximum height (e.g. 38209.996)
//   0x10 int32   total resolution X (nCellsX * 128)
//   0x14 int32   total resolution Y (nCellsY * 128)
//   0x18 int32   minimum cell X (west)
//   0x1C int32   minimum cell Y (south)
//   0x20 int32   maximum cell X (east)
//   0x24 int32   maximum cell Y (north)
//   0x28 int32   number of land textures (LTEX form-ID records)
//        ...      LTEX form-ID table, per-cell min/max height map, per-quadrant LTEX map,
//                 GCVR count + form-ID table + per-quadrant GCVR map, LOD4 base maps
//                 (height / land-texture / vertex-color), zlib block-offset tables
//                 (LOD3..LOD0), then the zlib-compressed data blocks.
// In Starfield BTD files the SW/NE cell coordinates are all zero (the bounds are derived from
// the resolution), and terrain color and ground cover are absent. The two height floats are in
// Starfield world units as-is — the reference's x8 multiply is a self-flagged guess and is NOT
// reproduced here; see the note in the IsStarfield branch of the constructor.

using System.Buffers;
using System.IO.Compression;

namespace BethesdaMultitool.Core.Formats.Esm.Land.Btd;

/// <summary>
///     Reader for Bethesda Terrain Data (<c>.btd</c>) files (Fallout 76 / Starfield landscape).
///     Decodes per-cell height maps, land-texture alphas, ground cover and vertex colors from the
///     compressed multi-LOD tile pyramid. Thread-affine: a single instance keeps a small decompressed
///     tile cache and is not safe for concurrent <c>GetCell*</c> calls; open one per worker.
///     <para>
///         Accepts either a loose file (memory-mapped, so Fallout 76's 1.48 GB
///         <c>Appalachia.btd</c> stays off the managed heap) or raw bytes — Starfield ships every
///         terrain file inside <c>Starfield - Terrain*.ba2</c>, and archive entries arrive as bytes.
///         See <see cref="IBtdBytes" />.
///     </para>
/// </summary>
public sealed class BtdFile : IDisposable
{
    // "BTDB" read as a little-endian uint32.
    private const uint MagicBtdb = 'B' | ('T' << 8) | ('D' << 16) | ('B' << 24);

    private readonly IBtdBytes _source;
    private readonly long _fileSize;
    private bool _disposed;

    // Header / layout, mirroring BTDFile member names.
    private readonly long _cellHeightMinMaxMapOffs;
    private readonly long _ltexOffs;
    private readonly long _ltexMapOffs;
    private readonly long _gcvrOffs;
    private readonly long _gcvrMapOffs;
    private readonly long _heightMapLod4;
    private readonly long _landTexturesLod4;
    private readonly long _vertexColorLod4; // 0 for Starfield
    private readonly long _zlibBlkTableOffsLod3;
    private readonly long _zlibBlkTableOffsLod2;
    private readonly long _zlibBlkTableOffsLod1;
    private readonly long _zlibBlkTableOffsLod0;
    private readonly long _zlibBlocksDataOffs;

    // Tile cache (8x8 cells per tile). Ring buffer + map, matching the reference implementation.
    private readonly Dictionary<uint, TileData> _tileCacheMap = new();
    private List<TileData> _tileCache = [];
    private int _tileCacheIndex;

    /// <summary>Open a loose BTD file for reading (memory-mapped).</summary>
    public BtdFile(string fileName)
        : this(new MappedBtdBytes(fileName))
    {
    }

    /// <summary>
    ///     Open a BTD held in memory — the archive path, where the VFS returns an entry's bytes.
    ///     The array is used in place, not copied.
    /// </summary>
    public BtdFile(byte[] data)
        : this(new ArrayBtdBytes(data ?? throw new ArgumentNullException(nameof(data))))
    {
    }

    private BtdFile(IBtdBytes source)
    {
        _source = source;
        _fileSize = source.Length;

        long pos = 0;
        if (_fileSize < 4 || ReadU32(pos) != MagicBtdb)
        {
            _source.Dispose();
            throw new InvalidDataException("Input file format is not BTD (missing 'BTDB' magic).");
        }

        pos += 4;
        var version = ReadU32(pos);
        pos += 4;
        if (((version - 5u) & ~1u) != 0) // must be 5 or 6
        {
            _source.Dispose();
            throw new InvalidDataException($"Unsupported BTD format version {version} (expected 5 or 6).");
        }

        MinHeight = ReadF32(pos);
        pos += 4;
        MaxHeight = ReadF32(pos);
        pos += 4;
        var heightMapResX = ReadU32(pos);
        pos += 4;
        var heightMapResY = ReadU32(pos);
        pos += 4;
        CellMinX = ReadI32(pos);
        pos += 4;
        CellMinY = ReadI32(pos);
        pos += 4;
        CellMaxX = ReadI32(pos);
        pos += 4;
        CellMaxY = ReadI32(pos);
        pos += 4;

        IsStarfield = (CellMinX | CellMinY | CellMaxX | CellMaxY) == 0;
        if (IsStarfield)
        {
            // NOTE: the fo76utils reference multiplies these two floats by 8 here, and flags the factor
            // in its own source as possibly incorrect (fo76utils/src/btdfile.cpp:408). It is
            // incorrect — the header floats are already Starfield world units. Bethesda states each
            // block's height range itself, in the SFBK (Surface Block) record whose ANAM names the
            // .btd: Starfield.esm gives Data\TERRAIN\NewAtlantis.btd an ENAM of (0, 260.502) and
            // akilacity/NeonCity/CydoniaCity (-500, 1000), each matching the raw header exactly and
            // each an eighth of what the x8 produced. Do not reintroduce the factor.
            //
            // The cell bounds below stay derived from the resolution: SFBK NAM2/NAM3 ("Cell Min"/
            // "Cell Max") look like a better source but are degenerate on many blocks — akilacity
            // reports (0,0)-(0,0) for a grid that is really 9x9, which this derivation gets right.
            CellMinX = -(int)(heightMapResX >> 8);
            CellMinY = -(int)(heightMapResY >> 8);
            CellMaxX = CellMinX + (int)(heightMapResX >> 7) - 1;
            CellMaxY = CellMinY + (int)(heightMapResY >> 7) - 1;
        }

        NCellsX = CellMaxX + 1 - CellMinX;
        NCellsY = CellMaxY + 1 - CellMinY;
        if (NCellsX <= 0 || NCellsY <= 0 || (long)NCellsX * NCellsY > 0x10000000L)
        {
            _source.Dispose();
            throw new InvalidDataException($"BTD cell grid {NCellsX}x{NCellsY} is invalid or unreasonably large.");
        }

        long nCells = (long)NCellsX * NCellsY;

        LandTextureCount = (int)ReadU32(pos);
        pos += 4;
        _ltexOffs = pos;
        pos += (long)LandTextureCount * 4;
        _cellHeightMinMaxMapOffs = pos;
        pos += nCells << 3; // 2 floats (min,max) per cell
        _ltexMapOffs = pos;
        pos += nCells << 5; // 4 quadrants * 8 bytes per cell

        if (IsStarfield)
        {
            GroundCoverCount = 0;
            _gcvrOffs = 0;
            _gcvrMapOffs = 0;
        }
        else
        {
            GroundCoverCount = (int)ReadU32(pos);
            pos += 4;
            _gcvrOffs = pos;
            pos += (long)GroundCoverCount * 4;
            _gcvrMapOffs = pos;
            pos += nCells << 5;
        }

        _heightMapLod4 = pos;
        pos += nCells << 7; // 8x8 samples * 2 bytes per cell
        _landTexturesLod4 = pos;
        pos += nCells << 7;

        if (IsStarfield)
        {
            _vertexColorLod4 = 0;
            _zlibBlkTableOffsLod3 = pos;
            pos += (long)((NCellsY + 7) >> 3) * ((NCellsX + 7) >> 3) * 8;
            _zlibBlkTableOffsLod2 = pos;
            pos += (long)((NCellsY + 3) >> 2) * ((NCellsX + 3) >> 2) * 8;
            _zlibBlkTableOffsLod1 = pos;
            pos += (long)((NCellsY + 1) >> 1) * ((NCellsX + 1) >> 1) * 8;
            _zlibBlkTableOffsLod0 = pos;
            pos += nCells * 8;
        }
        else
        {
            _vertexColorLod4 = pos;
            pos += nCells << 7;
            _zlibBlkTableOffsLod3 = pos;
            pos += (long)((NCellsY + 7) >> 3) * ((NCellsX + 7) >> 3) * 2 * 8;
            _zlibBlkTableOffsLod2 = pos;
            pos += (long)((NCellsY + 3) >> 2) * ((NCellsX + 3) >> 2) * 2 * 8;
            _zlibBlkTableOffsLod1 = pos;
            pos += (long)((NCellsY + 1) >> 1) * ((NCellsX + 1) >> 1) * 8;
            _zlibBlkTableOffsLod0 = pos;
            pos += nCells * 2 * 8;
        }

        _zlibBlocksDataOffs = pos;
        SetTileCacheSize(2);
    }

    /// <summary>World minimum terrain height (game units).</summary>
    public float MinHeight { get; }

    /// <summary>World maximum terrain height (game units).</summary>
    public float MaxHeight { get; }

    /// <summary>X coordinate of the south-west corner cell.</summary>
    public int CellMinX { get; }

    /// <summary>Y coordinate of the south-west corner cell.</summary>
    public int CellMinY { get; }

    /// <summary>X coordinate of the north-east corner cell.</summary>
    public int CellMaxX { get; }

    /// <summary>Y coordinate of the north-east corner cell.</summary>
    public int CellMaxY { get; }

    /// <summary>World map width in cells.</summary>
    public int NCellsX { get; }

    /// <summary>World map height in cells.</summary>
    public int NCellsY { get; }

    /// <summary>Number of land-texture (LTEX) form-ID entries.</summary>
    public int LandTextureCount { get; }

    /// <summary>Number of ground-cover (GCVR) form-ID entries (0 for Starfield).</summary>
    public int GroundCoverCount { get; }

    /// <summary>True if this is a Starfield-style BTD (no vertex color / ground cover).</summary>
    public bool IsStarfield { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _source.Dispose();
        _disposed = true;
    }

    /// <summary>Converts a raw 16-bit height sample (0..65535) to a world height in game units.</summary>
    public float SampleToHeight(ushort value) => MinHeight + (value * ((MaxHeight - MinHeight) / 65535.0f));

    /// <summary>Land-texture form ID at table index <paramref name="n" /> (0 if out of range).</summary>
    public uint GetLandTexture(int n)
    {
        if ((uint)n >= (uint)LandTextureCount)
        {
            return 0u;
        }

        return ReadU32(_ltexOffs + ((long)n << 2));
    }

    /// <summary>Ground-cover form ID at table index <paramref name="n" /> (0 if out of range).</summary>
    public uint GetGroundCover(int n)
    {
        if ((uint)n >= (uint)GroundCoverCount)
        {
            return 0u;
        }

        return ReadU32(_gcvrOffs + ((long)n << 2));
    }

    /// <summary>
    ///     Reads the per-cell (min, max) entry from the header's per-cell height map (game units,
    ///     row-major south-to-north). NOTE: empirically this stored aggregate is NOT a tight bound on the
    ///     decoded per-vertex heights — fo76utils computes its offset but never decodes it, and even the
    ///     provably-correct LOD4 base can fall outside it. Treat it as a coarse hint, not a guaranteed
    ///     cell bound. On Fallout 76 its global extremes equal the world height range; on Starfield they
    ///     do not, because the header range there is a wide authored quantization envelope (Akila City
    ///     stores -500..1000 for content spanning roughly -3..49).
    /// </summary>
    public (float Min, float Max) GetCellHeightRange(int cellX, int cellY)
    {
        long index = ((long)(cellY - CellMinY) * NCellsX) + (cellX - CellMinX);
        long offs = _cellHeightMinMaxMapOffs + (index << 3);
        return (ReadF32(offs), ReadF32(offs + 4));
    }

    /// <summary>
    ///     Decodes one cell's height map into <paramref name="buf" /> as 16-bit samples
    ///     (0..65535 maps to <see cref="MinHeight" />..<see cref="MaxHeight" />). The buffer must hold
    ///     N*N entries where N = 128 &gt;&gt; <paramref name="lod" />, row-major south-to-north.
    /// </summary>
    public void GetCellHeightMap(ushort[] buf, int cellX, int cellY, int lod = 0)
    {
        var tile = LoadTile(cellX, cellY, (~0u << (lod + lod)) & 0x0155u);
        int x0 = ((cellX - CellMinX) & 7) << 7;
        int y0 = ((cellY - CellMinY) & 7) << 7;
        int n = 128 >> lod;
        int m = 7 - lod;
        for (int yc = 0; yc < n; yc++)
        {
            for (int xc = 0; xc < n; xc++)
            {
                buf[(yc << m) | xc] = tile.HmapData![((y0 + (yc << lod)) << 10) + x0 + (xc << lod)];
            }
        }
    }

    /// <summary>
    ///     Convenience wrapper around <see cref="GetCellHeightMap(ushort[],int,int,int)" /> returning a
    ///     fresh N*N array of world heights (game units) for the cell.
    /// </summary>
    public float[] GetCellHeightGrid(int cellX, int cellY, int lod = 0)
    {
        int n = 128 >> lod;
        var raw = new ushort[n * n];
        GetCellHeightMap(raw, cellX, cellY, lod);
        var heights = new float[n * n];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = SampleToHeight(raw[i]);
        }

        return heights;
    }

    /// <summary>
    ///     Reads a single height sample (world units) at intra-cell sample coordinates
    ///     (<paramref name="sampleX" />, <paramref name="sampleY" />), each in 0..(128&gt;&gt;<paramref name="lod" />)-1,
    ///     where (0,0) is the cell's south-west corner. Used to pull a neighboring cell's shared edge
    ///     row/column without decoding its whole grid (Fallout 76 stores 128 disjoint samples per cell,
    ///     so a watertight per-cell mesh needs its east/north edge taken from the next cell's sample 0).
    /// </summary>
    public float GetCellHeightSample(int cellX, int cellY, int sampleX, int sampleY, int lod = 0)
    {
        var tile = LoadTile(cellX, cellY, (~0u << (lod + lod)) & 0x0155u);
        int x0 = ((cellX - CellMinX) & 7) << 7;
        int y0 = ((cellY - CellMinY) & 7) << 7;
        return SampleToHeight(tile.HmapData![((y0 + (sampleY << lod)) << 10) + x0 + (sampleX << lod)]);
    }

    /// <summary>Decodes one cell's land-texture alpha map (pixelFormatA16 layout).</summary>
    public void GetCellLandTexture(ushort[] buf, int cellX, int cellY, int lod = 0)
    {
        var tile = LoadTile(cellX, cellY, (~0u << (lod + lod)) & 0x0155u);
        int x0 = ((cellX - CellMinX) & 7) << 7;
        int y0 = ((cellY - CellMinY) & 7) << 7;
        int n = 128 >> lod;
        int m = 7 - lod;
        for (int yc = 0; yc < n; yc++)
        {
            for (int xc = 0; xc < n; xc++)
            {
                uint tmp = tile.LtexData![((y0 + (yc << lod)) << 10) + x0 + (xc << lod)];
                tmp = ((tmp & 0x7E00) >> 9) | (tmp & 0x01C0) | ((tmp & 0x003F) << 9);
                tmp = ((tmp & 0x7038) >> 3) | (tmp & 0x01C0) | ((tmp & 0x0E07) << 3);
                buf[(yc << m) | xc] = (ushort)tmp;
            }
        }
    }

    /// <summary>Decodes one cell's ground-cover bit mask (pixelFormatA8 layout; Fallout 76 only).</summary>
    public void GetCellGroundCover(byte[] buf, int cellX, int cellY, int lod = 0)
    {
        var tile = LoadTile(cellX, cellY, 0x0002);
        int x = cellX - CellMinX;
        int y = cellY - CellMinY;
        int x0 = (x & 7) << 7;
        int y0 = (y & 7) << 7;
        int n = 128 >> lod;
        int m = 7 - lod;
        uint gcvrMask = 0;
        for (int yc = 0; yc < n; yc++)
        {
            for (int xc = 0; xc < n; xc++)
            {
                if ((xc & ((n >> 1) - 1)) == 0)
                {
                    // gcvrMask is intentionally NOT reset here: like the reference, it accumulates the
                    // union of ground-cover presence across the cell's quadrants as they are scanned.
                    long inner = (((long)(y << 1) | (uint)(yc >> (m - 1))) * (NCellsX << 1))
                                 + ((x << 1) | (xc >> (m - 1)));
                    long offs = (inner << 3) + _gcvrMapOffs;
                    for (int i = 7; i >= 0; i--, offs++)
                    {
                        gcvrMask |= (uint)(ReadU8(offs) < GroundCoverCount ? 1 : 0) << i;
                    }
                }

                uint tmp = tile.GcvrData![((y0 + (yc << lod)) << 10) + x0 + (xc << lod)];
                tmp = (byte)((tmp << 4) | (tmp >> 4)); // rotl 4
                tmp = ((tmp & 0xCC) >> 2) | ((tmp & 0x33) << 2);
                tmp = (((tmp & 0xAA) >> 1) | ((tmp & 0x55) << 1)) & gcvrMask;
                buf[(yc << m) | xc] = (byte)tmp;
            }
        }
    }

    /// <summary>Decodes one cell's vertex (terrain) color in packed RGBA16 (5 bits/channel); lod &gt;= 2.</summary>
    public void GetCellTerrainColor(ushort[] buf, int cellX, int cellY, int lod = 2)
    {
        var tile = LoadTile(cellX, cellY, (~0u << (lod + lod)) & 0x02A0u);
        int x0 = ((cellX - CellMinX) & 7) << 5;
        int y0 = ((cellY - CellMinY) & 7) << 5;
        int n = 128 >> lod;
        int m = 7 - lod;
        for (int yc = 0; yc < n; yc++)
        {
            int yy = lod >= 2 ? (yc << (lod - 2)) : (yc >> (2 - lod));
            for (int xc = 0; xc < n; xc++)
            {
                int xx = lod >= 2 ? (xc << (lod - 2)) : (xc >> (2 - lod));
                buf[(yc << m) | xc] = tile.VclrData![((y0 + yy) << 8) + x0 + xx];
            }
        }
    }

    /// <summary>
    ///     Fills the 64-byte per-cell texture set: bytes 0..15 = SW quadrant (8 land-texture IDs then
    ///     8 ground-cover IDs), 16..31 = SE, 32..47 = NW, 48..63 = NE. 0xFF means "none"; index 0 of each
    ///     quadrant is the default land texture. IDs map through <see cref="GetLandTexture" /> /
    ///     <see cref="GetGroundCover" />.
    /// </summary>
    public void GetCellTextureSet(byte[] buf, int cellX, int cellY)
    {
        int x = cellX - CellMinX;
        int y = cellY - CellMinY;
        for (int q = 0; q < 4; q++)
        {
            long offs = ((((long)(y << 1) | (uint)(q >> 1)) * (NCellsX << 1)) + ((x << 1) | (q & 1))) << 3;
            int t = q << 4;
            for (int i = 0; i < 8; i++)
            {
                byte tmp = ReadU8(_ltexMapOffs + offs + i);
                if (tmp == 0 || tmp > LandTextureCount)
                {
                    tmp = 0xFF;
                }
                else
                {
                    tmp = (byte)(LandTextureCount - tmp);
                }

                if (i < 5)
                {
                    buf[t + (5 - i)] = tmp;
                }

                if (tmp != 0xFF)
                {
                    buf[t] = tmp; // default texture
                }
            }

            buf[t + 6] = 0xFF;
            buf[t + 7] = 0xFF;
            int g = (q << 4) + 8;
            for (int i = 0; i < 8; i++)
            {
                byte tmp = GroundCoverCount > 0 ? ReadU8(_gcvrMapOffs + offs + i) : (byte)0xFF;
                if (tmp >= GroundCoverCount)
                {
                    tmp = 0xFF;
                }

                buf[g + (7 - i)] = tmp;
            }
        }
    }

    /// <summary>Sets the number of decompressed 8x8-cell tiles kept resident (default 2).</summary>
    public void SetTileCacheSize(int n)
    {
        int prevSize = _tileCache.Count;
        if (n <= prevSize)
        {
            return;
        }

        var newCache = new List<TileData>(n);
        newCache.AddRange(_tileCache);
        for (int i = prevSize; i < n; i++)
        {
            newCache.Add(new TileData { X0 = 0x8000, Y0 = 0x8000, BlockMask = 0 });
        }

        _tileCache = newCache;
        // _tileCacheMap entries still reference the same TileData objects, which are preserved.
    }

    // ---- tile loading ------------------------------------------------------

    private TileData LoadTile(int cellX, int cellY, uint blockMask)
    {
        uint x0 = (uint)(cellX - CellMinX) & 0xFFF8u;
        uint y0 = (uint)(cellY - CellMinY) & 0xFFF8u;
        uint cacheKey = (y0 << 16) | x0;
        if (!_tileCacheMap.TryGetValue(cacheKey, out var tileData))
        {
            tileData = _tileCache[_tileCacheIndex];
            uint oldKey = ((uint)tileData.Y0 << 16) | tileData.X0;
            _tileCacheMap.Remove(oldKey);
            tileData.X0 = (ushort)x0;
            tileData.Y0 = (ushort)y0;
            tileData.BlockMask = 0;
            _tileCacheMap[cacheKey] = tileData;
            _tileCacheIndex++;
            if (_tileCacheIndex >= _tileCache.Count)
            {
                _tileCacheIndex = 0;
            }
        }

        blockMask = (blockMask & ~tileData.BlockMask) & 0x03F7u;
        if (blockMask == 0)
        {
            return tileData;
        }

        if ((blockMask & 0x0155) != 0)
        {
            tileData.HmapData ??= new ushort[0x00100000];
            tileData.LtexData ??= new ushort[0x00100000];
        }

        if ((blockMask & 0x0002) != 0)
        {
            tileData.GcvrData ??= new byte[0x00100000];
        }

        if ((blockMask & 0x02A0) != 0)
        {
            tileData.VclrData ??= new ushort[0x00010000];
        }

        if (_vertexColorLod4 == 0)
        {
            if ((blockMask & 0x02A0) != 0)
            {
                Array.Fill(tileData.VclrData!, (ushort)0xFFFF);
            }

            blockMask &= 0x0155u;
        }

        // LOD4 base layer (read directly from the uncompressed header maps).
        if ((blockMask & 0x0300) != 0)
        {
            for (int yy = 0; yy < 64; yy++)
            {
                if (CellMinY + (int)((yy >> 3) + y0) > CellMaxY)
                {
                    break;
                }

                for (int xx = 0; xx < 64; xx++)
                {
                    if (CellMinX + (int)((xx >> 3) + x0) > CellMaxX)
                    {
                        break;
                    }

                    long offs = ((yy + (y0 << 3)) * (NCellsX << 3)) + (xx + (x0 << 3));
                    if ((blockMask & 0x0155) != 0)
                    {
                        tileData.HmapData![((yy << 10) + xx) << 4] = ReadU16(_heightMapLod4 + (offs << 1));
                        tileData.LtexData![((yy << 10) + xx) << 4] = ReadU16(_landTexturesLod4 + (offs << 1));
                    }

                    if ((blockMask & 0x02A0) != 0)
                    {
                        tileData.VclrData![((yy << 8) + xx) << 2] = ReadU16(_vertexColorLod4 + (offs << 1));
                    }
                }
            }
        }

        // LOD3..LOD0 detail layers (single-threaded; the reference fans these across cores).
        LoadBlocks(tileData, x0, y0, blockMask);
        tileData.BlockMask |= blockMask;
        return tileData;
    }

    private void LoadBlocks(TileData tileData, uint x, uint y, uint blockMask)
    {
        if (_vertexColorLod4 == 0)
        {
            LoadBlocksStarfield(tileData, x, y, blockMask);
            return;
        }

        var zlibBuf = ArrayPool<byte>.Shared.Rent(0xC000); // 0x6000 uint16
        try
        {
            for (int l = 4; l-- > 0;) // LOD3..LOD0
            {
                for (int yy = 0; yy < (8 >> l); yy++)
                {
                    long yc = (y >> l) + (uint)yy;
                    if (yc >= ((NCellsY + (1 << l) - 1) >> l))
                    {
                        break;
                    }

                    for (int xx = 0; xx < (8 >> l); xx++)
                    {
                        long xc = (x >> l) + (uint)xx;
                        if (xc >= ((NCellsX + (1 << l) - 1) >> l))
                        {
                            break;
                        }

                        long n = (yc * ((NCellsX + (1 << l) - 1) >> l)) + xc;
                        if ((blockMask & 0x55 & (1 << (l + l))) != 0)
                        {
                            LoadBlock(tileData, ((yy << 10) + xx) << (l + 7), n, l, 0, zlibBuf);
                        }

                        if ((blockMask & 0xA0 & (1 << (l + l + 1))) != 0)
                        {
                            LoadBlock(tileData, ((yy << 8) + xx) << (l + 5), n, l, 1, zlibBuf);
                        }

                        if ((blockMask & 0x02 & (1 << (l + l + 1))) != 0)
                        {
                            LoadBlock(tileData, ((yy << 10) + xx) << (l + 7), n, l, 1, zlibBuf);
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zlibBuf);
        }
    }

    private void LoadBlocksStarfield(TileData tileData, uint x, uint y, uint blockMask)
    {
        var zlibBuf = ArrayPool<byte>.Shared.Rent(0x10000); // 0x8000 uint16
        try
        {
            for (int l = 0; l < 4; l++) // LOD0..LOD3, first present level only
            {
                if ((blockMask & (1u << (l + l))) == 0)
                {
                    continue;
                }

                for (int yy = 0; yy < (8 >> l); yy++)
                {
                    long yc = (y >> l) + (uint)yy;
                    if (yc >= ((NCellsY + (1 << l) - 1) >> l))
                    {
                        break;
                    }

                    for (int xx = 0; xx < (8 >> l); xx++)
                    {
                        long xc = (x >> l) + (uint)xx;
                        if (xc >= ((NCellsX + (1 << l) - 1) >> l))
                        {
                            break;
                        }

                        long n = (yc * ((NCellsX + (1 << l) - 1) >> l)) + xc;
                        LoadBlockStarfield(tileData, ((yy << 10) + xx) << (l + 7), n, l, zlibBuf);
                    }
                }

                break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zlibBuf);
        }
    }

    private void LoadBlock(TileData tileData, int dataOffs, long n, int l, int b, byte[] zlibBuf)
    {
        if (l >= 2)
        {
            n = (n << 1) + b;
        }
        else if (l == 0 && b != 0)
        {
            n += (long)NCellsY * NCellsX;
        }

        long tableOffs = l switch
        {
            1 => _zlibBlkTableOffsLod1,
            2 => _zlibBlkTableOffsLod2,
            3 => _zlibBlkTableOffsLod3,
            _ => _zlibBlkTableOffsLod0
        } + (n << 3);

        long offs = ReadU32(tableOffs) + _zlibBlocksDataOffs;
        int compressedSize = (int)ReadU32(tableOffs + 4);
        if (offs + compressedSize > _fileSize)
        {
            throw new InvalidDataException("BTD: compressed block extends past end of file.");
        }

        int expected;
        if (l == 0 && b != 0)
        {
            expected = 16384;
        }
        else if (l >= 2 && b != 0)
        {
            expected = 32768;
        }
        else
        {
            expected = 49152;
        }

        DecompressBlock(offs, compressedSize, zlibBuf, expected);

        int xd = 1 << (b == 0 || l == 0 ? l : l - 2);
        int yd = (b == 0 || l == 0 ? 1024 - 128 : (256 - 128) >> 2) << l;
        int p = 0;
        for (int yRow = 0; yRow < 128; yRow += 2)
        {
            if (b == 0) // vertex height
            {
                LoadBlockLines16(tileData.HmapData!, dataOffs + (yRow << (l + 10)), zlibBuf, p, xd, yd);
                p += 384;
            }
            else if (l == 0) // ground cover
            {
                LoadBlockLines8(tileData.GcvrData!, dataOffs + (yRow << 10), zlibBuf, p);
                p += 256;
            }
            else // vertex color
            {
                LoadBlockLines16(tileData.VclrData!, dataOffs + (yRow << (l + 6)), zlibBuf, p, xd, yd);
                p += 384;
            }
        }

        if (b != 0)
        {
            return;
        }

        for (int yRow = 0; yRow < 128; yRow += 2) // land textures (second half of the height block)
        {
            LoadBlockLines16(tileData.LtexData!, dataOffs + (yRow << (l + 10)), zlibBuf, p, xd, yd);
            p += 384;
        }
    }

    private void LoadBlockStarfield(TileData tileData, int dataOffs, long n, int l, byte[] zlibBuf)
    {
        long tableOffs = l switch
        {
            1 => _zlibBlkTableOffsLod1,
            2 => _zlibBlkTableOffsLod2,
            3 => _zlibBlkTableOffsLod3,
            _ => _zlibBlkTableOffsLod0
        } + (n << 3);

        long offs = ReadU32(tableOffs) + _zlibBlocksDataOffs;
        int compressedSize = (int)ReadU32(tableOffs + 4);
        if (offs + compressedSize > _fileSize)
        {
            throw new InvalidDataException("BTD: compressed block extends past end of file.");
        }

        DecompressBlock(offs, compressedSize, zlibBuf, 65536);

        int xd = 1 << l;
        int p = 0;
        for (int yRow = 0; yRow < 128; yRow++) // vertex height
        {
            int dst = dataOffs + (yRow << (l + 10));
            for (int i = 0; i < 128; i++, dst += xd, p += 2)
            {
                tileData.HmapData![dst] = (ushort)(zlibBuf[p] | (zlibBuf[p + 1] << 8));
            }
        }

        for (int yRow = 0; yRow < 128; yRow++) // land textures
        {
            int dst = dataOffs + (yRow << (l + 10));
            for (int i = 0; i < 128; i++, dst += xd, p += 2)
            {
                tileData.LtexData![dst] = (ushort)(zlibBuf[p] | (zlibBuf[p + 1] << 8));
            }
        }
    }

    private static void LoadBlockLines16(ushort[] dst, int dstStart, byte[] src, int srcStart, int xd, int yd)
    {
        int d = dstStart;
        int s = srcStart;
        for (int i = 0; i < 64; i++)
        {
            d += xd;
            dst[d] = (ushort)(src[s] | (src[s + 1] << 8));
            d += xd;
            s += 2;
        }

        d += yd;
        for (int i = 0; i < 128; i++)
        {
            dst[d] = (ushort)(src[s] | (src[s + 1] << 8));
            d += xd;
            s += 2;
        }
    }

    private static void LoadBlockLines8(byte[] dst, int dstStart, byte[] src, int srcStart)
    {
        int d = dstStart;
        int s = srcStart;
        for (int i = 0; i < 128; i++)
        {
            dst[d++] = src[s++];
        }

        d += 1024 - 128;
        for (int i = 0; i < 128; i++)
        {
            dst[d++] = src[s++];
        }
    }

    // ---- compression / raw reads ------------------------------------------

    private void DecompressBlock(long fileOffset, int compressedSize, byte[] dst, int expectedSize)
    {
        if (compressedSize <= 0)
        {
            throw new InvalidDataException("BTD: empty compressed block.");
        }

        var packed = ArrayPool<byte>.Shared.Rent(compressedSize);
        try
        {
            _source.ReadArray(fileOffset, packed, 0, compressedSize);

            // The reference auto-detects an LZ4 frame (CMF/FLG == 0x0422 read big-endian) before
            // falling through to zlib. Retail Fallout 76 and Starfield use zlib; flag LZ4 clearly.
            if (compressedSize >= 2 && packed[0] == 0x04 && packed[1] == 0x22)
            {
                throw new NotSupportedException(
                    "LZ4-frame compressed BTD blocks are not supported (retail Fallout 76 uses zlib).");
            }

            using var ms = new MemoryStream(packed, 0, compressedSize);
            try
            {
                using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
                zlib.ReadExactly(dst, 0, expectedSize);
            }
            catch (InvalidDataException)
            {
                ms.Position = 0;
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                deflate.ReadExactly(dst, 0, expectedSize);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packed);
        }
    }

    private byte ReadU8(long offset) => _source.ReadU8(offset);

    private ushort ReadU16(long offset) => _source.ReadU16(offset);

    private uint ReadU32(long offset) => _source.ReadU32(offset);

    private int ReadI32(long offset) => _source.ReadI32(offset);

    private float ReadF32(long offset) => _source.ReadF32(offset);

    /// <summary>One decompressed 8x8-cell tile (1024x1024 height/ltex, 256x256 vertex color).</summary>
    private sealed class TileData
    {
        public ushort X0;
        public ushort Y0;
        public uint BlockMask;
        public ushort[]? HmapData;
        public ushort[]? LtexData;
        public byte[]? GcvrData;
        public ushort[]? VclrData;
    }
}
