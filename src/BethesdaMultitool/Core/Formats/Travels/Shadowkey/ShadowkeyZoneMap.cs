using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     A Shadowkey (N-Gage) <c>.zmp</c> zone map: the walkable grid of one level. Little-endian,
///     wrapped in the <see cref="ShadowkeyCompressedFile" /> envelope; <see cref="Parse" /> takes
///     the INFLATED payload so zlib is touched in exactly one place.
///     <code>
///     +0    char[32]  zone name        (== the file stem, case-insensitively, 21/21)
///     +32   char[32]  author           ("No Auth" x20, "Level Pimp" in crypt2)
///     +64   char[64]  description      ("Nondescript" x19, "Shadowkey Level Two"/"...Three")
///     +128  u16       width  in cells
///     +130  u16       height in cells
///     +132  cell[width * height]       6 bytes each, ROW-MAJOR, x fastest
///     </code>
///     <para>
///         Measured on all 21 retail zones 2026-09-05: <c>132 + width * height * 6</c> equals the
///         inflated length every time, and the embedded name matches the file stem every time.
///         Twenty zones are 128x128 (98,436 bytes inflated); <c>ffarena</c> alone is 64x64 (24,708)
///         — that pair is what pins the header size, since 98,436 - H = 4 * (24,708 - H) has the
///         single solution H = 132. Open/blocked splits run from 3,299/13,085 (Crypt3, a dungeon)
///         to 15,659/725 (azra, a town).
///     </para>
///     <para>
///         Trap: the payload is an MSVC debug-heap struct dump. Each fixed-width string is followed
///         by 0xCD fill and by the tail of some earlier, longer string, so every string is cut at
///         its first NUL and nothing after it is content.
///     </para>
/// </summary>
internal sealed class ShadowkeyZoneMap
{
    /// <summary>Bytes of header before the first cell.</summary>
    public const int HeaderLength = 132;

    /// <summary>Bytes per grid cell.</summary>
    public const int CellLength = 6;

    /// <summary>Characters in the zone-name field at +0.</summary>
    public const int NameFieldLength = 32;

    /// <summary>Characters in the author field at +32.</summary>
    public const int AuthorFieldLength = 32;

    /// <summary>Characters in the description field at +64.</summary>
    public const int DescriptionFieldLength = 64;

    private readonly ShadowkeyMapCell[] _cells;

    private ShadowkeyZoneMap(
        string name,
        string zoneName,
        string author,
        string description,
        int width,
        int height,
        ShadowkeyMapCell[] cells)
    {
        Name = name;
        ZoneName = zoneName;
        Author = author;
        Description = description;
        Width = width;
        Height = height;
        _cells = cells;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The zone name the header carries (the file stem on every retail zone).</summary>
    public string ZoneName { get; }

    /// <summary>The author string. "No Auth" on 20 of 21 retail zones.</summary>
    public string Author { get; }

    /// <summary>The description string. "Nondescript" on 19 of 21 retail zones.</summary>
    public string Description { get; }

    /// <summary>Grid width in cells (128 everywhere but ffarena, which is 64).</summary>
    public int Width { get; }

    /// <summary>Grid height in cells.</summary>
    public int Height { get; }

    /// <summary>The grid, row-major with x varying fastest.</summary>
    public IReadOnlyList<ShadowkeyMapCell> Cells => _cells;

    /// <summary>Cells whose <see cref="ShadowkeyMapCell.IsBlocked" /> is set.</summary>
    public int BlockedCellCount { get; private init; }

    /// <summary>Largest <see cref="ShadowkeyMapCell.PrototypeIndex" /> in the grid, or -1 when empty.</summary>
    public int MaxPrototypeIndex { get; private init; }

    /// <summary>
    ///     True when the low 6 bits of every cell's u16 at +2 are clear, as on all 331,776 retail
    ///     cells. A shape check the two rival readings of that field rest on (see
    ///     <see cref="ShadowkeyMapCell" />), reported rather than enforced: a file that broke it
    ///     would still be readable, just not interpretable that way.
    /// </summary>
    public bool RawLowBitsClear { get; private init; }

    /// <summary>
    ///     Parses an inflated <c>.zmp</c> payload. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> and the byte position when the header is truncated, the
    ///     dimensions are not positive, or the grid does not tile the payload exactly.
    /// </summary>
    public static ShadowkeyZoneMap Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the header ends at byte {HeaderLength}, past the {bytes.Length}-byte payload.");
        }

        var zoneName = ReadString(bytes.Slice(0, NameFieldLength));
        var author = ReadString(bytes.Slice(NameFieldLength, AuthorFieldLength));
        var description = ReadString(
            bytes.Slice(NameFieldLength + AuthorFieldLength, DescriptionFieldLength));

        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[128..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[130..]);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException(
                $"'{name}': grid is {width}x{height} at byte 128; both dimensions must be positive.");
        }

        var expected = HeaderLength + ((long)width * height * CellLength);
        if (expected != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': a {width}x{height} grid needs {expected} bytes ({HeaderLength} + {width}*{height}*{CellLength}) but the payload is {bytes.Length}.");
        }

        var cells = new ShadowkeyMapCell[width * height];
        var blocked = 0;
        var maxIndex = -1;
        var lowBitsClear = true;
        for (var i = 0; i < cells.Length; i++)
        {
            var offset = HeaderLength + (i * CellLength);
            var raw = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]);
            var prototype = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 4)..]);
            var cell = new ShadowkeyMapCell(bytes[offset], bytes[offset + 1], raw, prototype);
            cells[i] = cell;

            if (cell.IsBlocked)
            {
                blocked++;
            }

            if (prototype > maxIndex)
            {
                maxIndex = prototype;
            }

            lowBitsClear &= cell.RawLowBitsClear;
        }

        return new ShadowkeyZoneMap(name, zoneName, author, description, width, height, cells)
        {
            BlockedCellCount = blocked,
            MaxPrototypeIndex = maxIndex,
            RawLowBitsClear = lowBitsClear,
        };
    }

    /// <summary>Returns the cell at (<paramref name="x" />, <paramref name="y" />).</summary>
    public ShadowkeyMapCell Cell(int x, int y)
    {
        if (x < 0 || x >= Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, $"'{Name}': x must be 0..{Width - 1}.");
        }

        if (y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, $"'{Name}': y must be 0..{Height - 1}.");
        }

        return _cells[(y * Width) + x];
    }

    /// <summary>True when the cell at (<paramref name="x" />, <paramref name="y" />) is solid.</summary>
    public bool IsBlocked(int x, int y)
    {
        return Cell(x, y).IsBlocked;
    }

    /// <summary>
    ///     Reads a fixed-width string field, cutting at the first NUL. Everything after it is
    ///     0xCD heap fill or a stale tail from an earlier record and must not be returned.
    /// </summary>
    private static string ReadString(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }
}
