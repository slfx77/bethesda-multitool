using System.Buffers.Binary;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Redguard's <c>maps\*.WLD</c> exterior terrain ("scape" in the engine's own vocabulary):
///     the four outdoor worlds (ISLAND, NECRISLE, EXTPALAC, HIDEOUT), each a fixed 263,432-byte
///     file. Original RE — the layout, the layer meanings, the height table and the
///     world-to-cell mapping were all read off <c>RG.EXE</c>'s scape loader and renderer
///     (2026-09-08; the exporter reference has no reader for this format).
///     <para>
///         <b>Layout, as the game reads it.</b> A 144-byte header (the loader reads exactly
///         0x90 bytes: dword 0 = tile-table length 16, dwords 1-2 = tiles per axis 2 x 2,
///         dword 4 = 160 = offset of the 1,024-byte level table, dword 6 = 22 = tile record
///         header length, dword 7 = 263,416 = length of everything before the trailer), then the
///         tile table (four LE u32 file offsets at 144), the level table (1,024 bytes at 160,
///         only its first four bytes 25/30/80/127 non-zero), then <b>four tile records</b> of a
///         22-byte header + 65,536 bytes, and the 16-byte <c>"TULO"</c> trailer. Every tile
///         record header reads <c>u16 textureSet = 302</c> at +6, after an uninterpreted word at
///         +4, a level index at +8 and a flag
///         byte at +9 whose low five bits, when non-zero, mean the 65,536-byte stored payload
///         (zero would mean a 25-byte procedural seed the game expands itself; retail never
///         uses it, and this reader refuses it rather than guessing).
///     </para>
///     <para>
///         <b>A tile is 128 x 128 cells, stored as FOUR 16,384-byte quarters</b>, and the loader
///         copies quarter <c>k</c> of tile <c>i</c> into row-stride-256 buffer <c>k</c> at
///         (128·(i mod 2), 128·(i div 2)). So the file holds four 256 x 256 attribute layers,
///         each cell 256 world units square (the whole map is 65,536 units); the old reading of
///         "eight 128 x 256 layers from offset 32" was nine rows and 22 bytes off and had every
///         layer straddling two quarters, which is what made the old smoothness split look like
///         four attribute pairs.
///     </para>
///     <para>
///         <b>Layer meanings, each with the code path that uses it:</b>
///         <list type="bullet">
///             <item>
///                 <b>Layer 0 — height.</b> Bits 0-6 index a 128-entry table of world-unit
///                 heights (<see cref="HeightTable" />, read off RG.EXE at linear 0x1286A8 /
///                 file 0x180EA8); the renderer looks the vertex height up through it and
///                 negates it (world Y is negative-up). Bit 7 is the quad-split flag: the loader
///                 recomputes it after every tile load (a quad whose diagonals disagree in table
///                 height is flagged), so the stored bit is stale editor state — on ISLAND the
///                 rule predicts 7,195 flagged quads, all stored, but 3,006 more are stored that
///                 the rule does not set. Oracle: the AI nav-graph nodes in the sibling
///                 <c>.RGM</c>, which stand on the ground: on ISLAND 1,337 of 1,424 lie within
///                 64 units of the bilinear height (median 2.0, r = 0.996) against 89 for a
///                 shuffled height layer; NECRISLE 529/561; EXTPALAC's 95 nodes over
///                 ISLAND.WLD 70/95.
///             </item>
///             <item>
///                 <b>Layer 2 — surface texture.</b> Bits 0-5 are a record index into the
///                 tile's texture set (<c>3dart\TEXTURE.302</c>, "Small landscape", exactly 64
///                 records, the set <c>surface.ini</c> calls the scape surfaces), bits 6-7 a
///                 quarter-turn rotation. The surface sampler forms the texture word as
///                 <c>(302 &lt;&lt; 7) | (v &amp; 0x3F)</c>, which is exactly the
///                 <c>set, index</c> pair surface.ini keys on. Cross-check with layer 0: on
///                 ISLAND the cells surface.ini classes DEEPWATER have median height 0, WATER
///                 0 (mean 41), SAND 720, ROCK 920.
///             </item>
///             <item>
///                 <b>Layer 1 — scatter flats.</b> Bits 2-7 (1..33) name a billboard the renderer
///                 draws at the cell from the same texture set; bits 0-1 are masked separately.
///                 <b>All four retail files leave it entirely zero.</b>
///             </item>
///             <item>
///                 <b>Layer 3 — unused.</b> Only the procedural-seed expander writes it (sea
///                 floor under land) and nothing reads it at runtime.
///                 <b>
///                     Zero in all four retail
///                     files.
///                 </b>
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>World to cell:</b> <c>cellX = x &gt;&gt; 8</c>, <c>cellZ = (65536 − z) &gt;&gt; 8</c>
///         — the game's own tile arithmetic (<c>x &gt;&gt; 15</c> and <c>(65536 − z) &gt;&gt; 15</c>
///         pick the tile, the next seven bits the cell), so rows run from high z to low z.
///         Nav nodes and <c>MPSO</c> statics are in these units; <c>MPOB</c> placements are
///         256 times finer.
///     </para>
/// </summary>
internal sealed class RedguardWldFile
{
    /// <summary>Every retail file is exactly this long.</summary>
    public const int FileLength = 263432;

    /// <summary>The loader reads exactly 0x90 header bytes.</summary>
    public const int HeaderLength = 144;

    /// <summary>The tile table follows the header; header dword 0 is its length in bytes.</summary>
    public const int TileTableOffset = HeaderLength;

    /// <summary>Bytes in the level table the loader reads at header dword 4.</summary>
    public const int LevelTableLength = 1024;

    /// <summary>Header dword 6: bytes in each tile record header.</summary>
    public const int TileRecordHeaderLength = 22;

    /// <summary>Cells along one edge of a tile.</summary>
    public const int TileSize = 128;

    /// <summary>Bytes in one layer quarter of a tile record.</summary>
    public const int TileLayerLength = TileSize * TileSize;

    /// <summary>Attribute layers per cell — the four quarters of a tile record.</summary>
    public const int LayerCount = 4;

    /// <summary>Bytes in a stored tile payload.</summary>
    public const int TilePayloadLength = LayerCount * TileLayerLength;

    /// <summary>Bytes in the procedural seed a tile record may carry instead of a payload.</summary>
    public const int ProceduralSeedLength = 25;

    /// <summary>Cells along one edge of the assembled map (2 x 2 tiles).</summary>
    public const int MapSize = 256;

    /// <summary>World units per cell — the loader shifts world coordinates right by 8.</summary>
    public const int WorldUnitsPerCell = 256;

    /// <summary>World units across the whole map.</summary>
    public const int WorldExtent = MapSize * WorldUnitsPerCell;

    public const int TrailerLength = 16;

    /// <summary>Mask for the height index in layer 0; bit 7 is the quad-split flag.</summary>
    public const byte HeightIndexMask = 0x7F;

    /// <summary>Mask for the texture record index in layer 2; bits 6-7 are the rotation.</summary>
    public const byte SurfaceTextureMask = 0x3F;

    /// <summary>
    ///     The 128-entry height table RG.EXE indexes with a cell's 7-bit height (linear
    ///     0x1286A8, file 0x180EA8): world units, negated by the renderer because world Y is
    ///     negative-up. Non-linear — 40-unit steps low down, 1,080 at the top.
    /// </summary>
    public static readonly IReadOnlyList<int> HeightTable =
    [
        0, 40, 40, 40, 80, 80, 80, 120, 120, 120, 160, 160, 160, 200, 200, 200,
        240, 240, 240, 280, 280, 320, 320, 320, 360, 360, 400, 400, 400, 440, 440, 480,
        480, 480, 520, 520, 560, 560, 600, 600, 600, 640, 640, 680, 680, 720, 720, 760,
        760, 800, 800, 840, 840, 880, 880, 920, 920, 960, 1000, 1000, 1040, 1040, 1080, 1120,
        1120, 1160, 1160, 1200, 1240, 1240, 1280, 1320, 1320, 1360, 1400, 1440, 1440, 1480, 1520, 1560,
        1600, 1600, 1640, 1680, 1720, 1760, 1800, 1840, 1880, 1920, 1960, 2000, 2040, 2080, 2120, 2200,
        2240, 2280, 2320, 2400, 2440, 2520, 2560, 2640, 2680, 2760, 2840, 2920, 3000, 3080, 3160, 3240,
        3360, 3440, 3560, 3680, 3800, 3960, 4080, 4280, 4440, 4680, 4920, 5200, 5560, 6040, 6680, 7760
    ];

    /// <summary>Names for the four layers, in quarter order.</summary>
    public static readonly IReadOnlyList<string> LayerNames = ["height", "scatter", "surface", "unused"];

    private RedguardWldFile(
        string name,
        IReadOnlyList<uint> header,
        IReadOnlyList<RedguardWldTile> tiles,
        byte[] levelTable,
        IReadOnlyList<IndexedBitmap> layers,
        IReadOnlyList<uint> trailer)
    {
        Name = name;
        Header = header;
        Tiles = tiles;
        LevelTable = levelTable;
        Layers = layers;
        Trailer = trailer;
    }

    private static ReadOnlySpan<byte> TrailerTag => "TULO"u8;

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The eight leading header dwords: 16, 2, 2, 0, 160, 1, 22, then <c>fileLength − 16</c> on retail.</summary>
    public IReadOnlyList<uint> Header { get; }

    /// <summary>Tiles per axis, from header dwords 1 and 2.</summary>
    public int TilesX => (int)Header[1];

    public int TilesZ => (int)Header[2];

    /// <summary>
    ///     The tile records in table order (index <c>i</c> sits at tile column <c>i mod TilesX</c>, row
    ///     <c>i div TilesX</c>).
    /// </summary>
    public IReadOnlyList<RedguardWldTile> Tiles { get; }

    /// <summary>
    ///     The 1,024-byte level table at header dword 4. Only the editor's scatter generator reads
    ///     it (a 4-byte height band per level index: bytes 0 and 3 are the low and high bound);
    ///     retail files carry 25, 30, 80, 127 and zeros.
    /// </summary>
    public byte[] LevelTable { get; }

    /// <summary>The four assembled 256 x 256 layers, quarter order: height, scatter, surface, unused.</summary>
    public IReadOnlyList<IndexedBitmap> Layers { get; }

    /// <summary>Layer 0: bits 0-6 height index, bit 7 the stored quad-split flag.</summary>
    public IndexedBitmap HeightLayer => Layers[0];

    /// <summary>Layer 1: bits 2-7 scatter flat kind, bits 0-1 separate. Zero on every retail file.</summary>
    public IndexedBitmap ScatterLayer => Layers[1];

    /// <summary>Layer 2: bits 0-5 texture record, bits 6-7 quarter-turn rotation.</summary>
    public IndexedBitmap SurfaceLayer => Layers[2];

    /// <summary>Layer 3: unread at runtime. Zero on every retail file.</summary>
    public IndexedBitmap UnusedLayer => Layers[3];

    /// <summary>The three trailer dwords after <c>"TULO"</c>.</summary>
    public IReadOnlyList<uint> Trailer { get; }

    /// <summary>The texture set every tile record names (302 on retail).</summary>
    public int TextureSet => Tiles[0].TextureSet;

    /// <summary>Content probe: the fixed length and the trailer tag.</summary>
    public static bool IsWldFile(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length == FileLength && bytes[(FileLength - TrailerLength)..(FileLength - TrailerLength + 4)]
            .SequenceEqual(TrailerTag);
    }

    /// <summary>
    ///     Parses a terrain file, throwing <see cref="InvalidDataException" /> when it is not the
    ///     retail shape and <see cref="NotSupportedException" /> when a tile carries a procedural
    ///     seed instead of a stored payload.
    /// </summary>
    public static RedguardWldFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length != FileLength)
        {
            throw new InvalidDataException($"{name}: {bytes.Length} bytes; a Redguard WLD is always {FileLength}.");
        }

        var header = new uint[8];
        for (var i = 0; i < header.Length; i++)
        {
            header[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 * i)..]);
        }

        var tilesX = (int)header[1];
        var tilesZ = (int)header[2];
        var tileCount = tilesX * tilesZ;
        if (tilesX != MapSize / TileSize || tilesZ != MapSize / TileSize)
        {
            throw new InvalidDataException(
                $"{name}: header declares a {tilesX} x {tilesZ} tile grid; only 2 x 2 is known.");
        }

        if (header[0] != 4u * tileCount)
        {
            throw new InvalidDataException(
                $"{name}: header dword 0 is {header[0]}, expected a {tileCount}-entry tile table of {4 * tileCount} bytes.");
        }

        var levelTableAt = (int)header[4];
        if (levelTableAt != HeaderLength + header[0])
        {
            throw new InvalidDataException(
                $"{name}: header dword 4 is {header[4]}, expected the level table at {HeaderLength + header[0]}.");
        }

        if (header[6] != TileRecordHeaderLength)
        {
            throw new InvalidDataException(
                $"{name}: header dword 6 is {header[6]}, expected a {TileRecordHeaderLength}-byte tile header.");
        }

        var dataEnd = (int)header[7];
        if (dataEnd != FileLength - TrailerLength)
        {
            throw new InvalidDataException(
                $"{name}: header dword 7 is {header[7]}, expected {FileLength - TrailerLength}.");
        }

        if (!bytes.Slice(dataEnd, 4).SequenceEqual(TrailerTag))
        {
            throw new InvalidDataException($"{name}: no \"TULO\" trailer at {dataEnd}.");
        }

        var levelTable = bytes.Slice(levelTableAt, LevelTableLength).ToArray();

        var offsets = new int[tileCount];
        for (var i = 0; i < tileCount; i++)
        {
            offsets[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(TileTableOffset + 4 * i)..]);
        }

        var layers = new byte[LayerCount][];
        for (var k = 0; k < LayerCount; k++)
        {
            layers[k] = new byte[MapSize * MapSize];
        }

        var tiles = new RedguardWldTile[tileCount];
        var expectedAt = levelTableAt + LevelTableLength;
        for (var i = 0; i < tileCount; i++)
        {
            var at = offsets[i];
            if (at != expectedAt)
            {
                throw new InvalidDataException(
                    $"{name}: tile {i} is at {at} but the previous record ends at {expectedAt}.");
            }

            var recordHeader = bytes.Slice(at, TileRecordHeaderLength).ToArray();
            var flags = recordHeader[9];
            if ((flags & 0x1F) == 0)
            {
                throw new NotSupportedException(
                    $"{name}: tile {i} carries a {ProceduralSeedLength}-byte procedural seed instead of a stored payload; the game expands those itself and no retail file does it.");
            }

            var payloadAt = at + TileRecordHeaderLength;
            if (payloadAt + TilePayloadLength > dataEnd)
            {
                throw new InvalidDataException($"{name}: tile {i} payload runs past the trailer.");
            }

            var tileX = i % tilesX;
            var tileZ = i / tilesX;
            for (var k = 0; k < LayerCount; k++)
            {
                var quarter = bytes.Slice(payloadAt + k * TileLayerLength, TileLayerLength);
                for (var row = 0; row < TileSize; row++)
                {
                    quarter.Slice(row * TileSize, TileSize)
                        .CopyTo(layers[k].AsSpan((tileZ * TileSize + row) * MapSize + tileX * TileSize, TileSize));
                }
            }

            tiles[i] = new RedguardWldTile(
                i,
                tileX,
                tileZ,
                at,
                BinaryPrimitives.ReadUInt32LittleEndian(recordHeader),
                BinaryPrimitives.ReadUInt16LittleEndian(recordHeader.AsSpan(6)),
                recordHeader[8],
                flags,
                BinaryPrimitives.ReadUInt16LittleEndian(recordHeader.AsSpan(10)),
                recordHeader);
            expectedAt = payloadAt + TilePayloadLength;
        }

        if (expectedAt != dataEnd)
        {
            throw new InvalidDataException(
                $"{name}: the tile records end at {expectedAt}, not at the trailer ({dataEnd}).");
        }

        var trailer = new uint[3];
        for (var i = 0; i < 3; i++)
        {
            trailer[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(dataEnd + 4 + 4 * i)..]);
        }

        var bitmaps = new IndexedBitmap[LayerCount];
        for (var k = 0; k < LayerCount; k++)
        {
            bitmaps[k] = new IndexedBitmap(MapSize, MapSize, layers[k]);
        }

        return new RedguardWldFile(name, header, tiles, levelTable, bitmaps, trailer);
    }

    /// <summary>
    ///     The game's world-to-cell mapping (<c>x &gt;&gt; 8</c>, <c>(65536 − z) &gt;&gt; 8</c>):
    ///     true when the point lands on the map. Cell rows run from high z to low z.
    /// </summary>
    public static bool TryMapWorldToCell(int worldX, int worldZ, out int cellX, out int cellZ)
    {
        cellX = worldX >> 8;
        cellZ = (WorldExtent - worldZ) >> 8;
        return worldX >= 0 && cellX < MapSize && cellZ >= 0 && cellZ < MapSize;
    }

    /// <summary>The 7-bit height index of a cell.</summary>
    public int HeightIndexAt(int cellX, int cellZ)
    {
        return HeightLayer.Indices[cellZ * MapSize + cellX] & HeightIndexMask;
    }

    /// <summary>The cell's height in world units, negative-up as the renderer uses it.</summary>
    public int WorldHeightAt(int cellX, int cellZ)
    {
        return -HeightTable[HeightIndexAt(cellX, cellZ)];
    }

    /// <summary>The stored quad-split flag (bit 7 of layer 0) — stale editor state the loader overwrites.</summary>
    public bool StoredQuadSplitAt(int cellX, int cellZ)
    {
        return (HeightLayer.Indices[cellZ * MapSize + cellX] & 0x80) != 0;
    }

    /// <summary>
    ///     The loader's own quad-split rule, recomputed after every tile load: the quad whose
    ///     top-left corner is this cell is flagged when its two diagonals disagree in table height,
    ///     i.e. <c>h(x,z) − h(x+1,z) ≠ h(x,z+1) − h(x+1,z+1)</c>. Defined for interior cells only
    ///     (the game reads past its buffer on the last row and column).
    /// </summary>
    public bool ComputedQuadSplitAt(int cellX, int cellZ)
    {
        if (cellX >= MapSize - 1 || cellZ >= MapSize - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(cellX),
                "The quad-split rule needs the cell's right and lower neighbours.");
        }

        var h00 = HeightTable[HeightIndexAt(cellX, cellZ)];
        var h10 = HeightTable[HeightIndexAt(cellX + 1, cellZ)];
        var h01 = HeightTable[HeightIndexAt(cellX, cellZ + 1)];
        var h11 = HeightTable[HeightIndexAt(cellX + 1, cellZ + 1)];
        return h00 - h10 != h01 - h11;
    }

    /// <summary>
    ///     Bilinear world height at a world position, negative-up, or <see cref="double.NaN" />
    ///     when the point is off the map. Corners are cell origins, as the renderer places them.
    /// </summary>
    public double SampleWorldHeight(int worldX, int worldZ)
    {
        if (!TryMapWorldToCell(worldX, worldZ, out var cellX, out var cellZ))
        {
            return double.NaN;
        }

        var tx = (worldX & (WorldUnitsPerCell - 1)) / (double)WorldUnitsPerCell;
        var tz = ((WorldExtent - worldZ) & (WorldUnitsPerCell - 1)) / (double)WorldUnitsPerCell;
        var x1 = Math.Min(cellX + 1, MapSize - 1);
        var z1 = Math.Min(cellZ + 1, MapSize - 1);
        double h00 = HeightTable[HeightIndexAt(cellX, cellZ)];
        double h10 = HeightTable[HeightIndexAt(x1, cellZ)];
        double h01 = HeightTable[HeightIndexAt(cellX, z1)];
        double h11 = HeightTable[HeightIndexAt(x1, z1)];
        return -((1 - tx) * (1 - tz) * h00 + tx * (1 - tz) * h10 + (1 - tx) * tz * h01 + tx * tz * h11);
    }

    /// <summary>The texture record index (0..63) of a cell within <see cref="TextureSet" />.</summary>
    public int SurfaceTextureAt(int cellX, int cellZ)
    {
        return SurfaceLayer.Indices[cellZ * MapSize + cellX] & SurfaceTextureMask;
    }

    /// <summary>The cell texture's rotation in quarter turns (0..3).</summary>
    public int SurfaceRotationAt(int cellX, int cellZ)
    {
        return SurfaceLayer.Indices[cellZ * MapSize + cellX] >> 6;
    }

    /// <summary>The scatter flat kind the renderer draws at a cell (bits 2-7 of layer 1; 0 = none).</summary>
    public int ScatterKindAt(int cellX, int cellZ)
    {
        return ScatterLayer.Indices[cellZ * MapSize + cellX] >> 2;
    }

    /// <summary>
    ///     Applies a cell's quarter-turn rotation to texel coordinates the way the surface sampler
    ///     does (0: as stored; 1: <c>(max − v, u)</c>; 2: <c>(max − u, max − v)</c>; 3:
    ///     <c>(v, max − u)</c>), for a square texture of <paramref name="size" /> texels.
    /// </summary>
    public static (int U, int V) RotateTexel(int u, int v, int rotation, int size)
    {
        var max = size - 1;
        return (rotation & 3) switch
        {
            1 => (max - v, u),
            2 => (max - u, max - v),
            3 => (v, max - u),
            _ => (u, v)
        };
    }
}

/// <summary>
///     One tile record: its table index, grid position, file offset and decoded 22-byte header.
///     <see cref="Word0" /> varies per record (2152, 568, 1308, 10 on retail) and is read only by the
///     procedural expander; <see cref="TextureSet" /> is the set the renderer's scatter flats and the
///     surface sampler use; <see cref="LevelIndex" /> selects a level-table band; <see cref="Flags" />
///     low five bits non-zero marks a stored payload.
/// </summary>
internal sealed record RedguardWldTile(
    int Index,
    int TileX,
    int TileZ,
    int Offset,
    uint Word0,
    ushort TextureSet,
    byte LevelIndex,
    byte Flags,
    ushort Word10,
    byte[] RawHeader)
{
    /// <summary>True when the record carries the 65,536-byte payload rather than a procedural seed.</summary>
    public bool IsStored => (Flags & 0x1F) != 0;
}
