// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/WoodsFile.cs. License
//   texts are collected centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     Daggerfall's <c>WOODS.WLD</c> — the whole-province heightmap behind the world map: one
///     byte of elevation for each of the 1,000 x 500 map pixels, plus a 5x5 sub-grid of finer
///     elevations behind every pixel for close-up terrain.
///     <para>
///         Layout, verified to tile the retail file exactly (26,001,168 bytes, 2026-09-02): a
///         144-byte header (eight u32 fields then 28 zero words), 500,000 u32 offsets to the
///         per-pixel cell records, the 500,000-byte heightmap at the header's heightmap offset
///         (2,001,168), then the 500,000 cell records of 47 bytes each. A cell's 5x5 sub-grid sits
///         22 bytes into its record — the header's second "unknown" field carries that 22, which
///         is how it was pinned. Offsets are absolute file positions.
///     </para>
/// </summary>
internal sealed class DaggerfallWoodsFile
{
    /// <summary>Map width in pixels.</summary>
    public const int Width = 1000;

    /// <summary>Map height in pixels.</summary>
    public const int Height = 500;

    /// <summary>Pixels in the map.</summary>
    public const int PixelCount = Width * Height;

    /// <summary>Edge length of a pixel's elevation sub-grid.</summary>
    public const int CellGridSize = 5;

    /// <summary>Bytes in the fixed header: 8 u32 fields plus 28 zero words.</summary>
    public const int HeaderLength = (8 + 28) * 4;

    /// <summary>Bytes from a cell record's start to its 5x5 sub-grid.</summary>
    public const int CellDataSkip = 22;

    private readonly byte[] _file;
    private readonly int _offsetTableStart;

    private DaggerfallWoodsFile(string name, byte[] file, int heightMapOffset, int offsetTableStart)
    {
        Name = name;
        _file = file;
        _offsetTableStart = offsetTableStart;
        HeightMapOffset = heightMapOffset;
        HeightMap = file.AsMemory(heightMapOffset, PixelCount);
    }

    /// <summary>Logical file name.</summary>
    public string Name { get; }

    /// <summary>Byte offset of the heightmap, from the header.</summary>
    public int HeightMapOffset { get; }

    /// <summary>The 1,000 x 500 elevation bytes, row-major.</summary>
    public ReadOnlyMemory<byte> HeightMap { get; }

    /// <summary>Elevation at a map pixel, clamped to the map like the reference's accessor.</summary>
    public byte GetHeight(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        return HeightMap.Span[(y * Width) + x];
    }

    /// <summary>
    ///     The 5x5 elevation sub-grid behind a map pixel, row-major (index = gy * 5 + gx).
    ///     Coordinates clamp to the map, as the reference's accessor does.
    /// </summary>
    public byte[] GetCellGrid(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);

        var entry = _offsetTableStart + (((y * Width) + x) * 4);
        var cellOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(_file.AsSpan(entry)) + CellDataSkip;
        var gridLength = CellGridSize * CellGridSize;
        if (cellOffset < 0 || cellOffset + gridLength > _file.Length)
        {
            throw new InvalidDataException(
                $"'{Name}' cell ({x}, {y}) points outside the file ({cellOffset}).");
        }

        return _file.AsSpan((int)cellOffset, gridLength).ToArray();
    }

    /// <summary>Whether a name is the world heightmap.</summary>
    public static bool IsWoodsFileName(string fileName)
    {
        return fileName.Equals("WOODS.WLD", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Parses the heightmap. The whole file is retained, because the per-pixel cell grids
    ///     are read on demand through the offset table rather than copied up front.
    /// </summary>
    public static DaggerfallWoodsFile Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length < HeaderLength + (PixelCount * 4))
        {
            throw new InvalidDataException(
                $"'{name}' is too small for the WOODS header and offset table ({bytes.Length} bytes).");
        }

        var span = bytes.AsSpan();
        var offsetSize = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        var heightMapOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);

        if (width != Width || height != Height)
        {
            throw new InvalidDataException(
                $"'{name}' declares a {width}x{height} map; WOODS.WLD is always {Width}x{Height}.");
        }

        if (offsetSize != PixelCount * 4)
        {
            throw new InvalidDataException(
                $"'{name}' declares an offset table of {offsetSize} bytes; {PixelCount * 4} expected.");
        }

        if (heightMapOffset > int.MaxValue || heightMapOffset + PixelCount > (uint)bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}' places the heightmap at {heightMapOffset}, past the end of a {bytes.Length}-byte file.");
        }

        return new DaggerfallWoodsFile(name, bytes, (int)heightMapOffset, HeaderLength);
    }
}
