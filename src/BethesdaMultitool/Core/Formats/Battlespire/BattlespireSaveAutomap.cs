using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     The body of a SAVETREE Automap record (type 51): the explored-tile grid the in-game map
///     draws. The grid's width sits in the header's pitch word and its height in the yaw word —
///     UESP's reading, and on the fixture 40 x 30 = 1,200 tiles + a u32 + a u8 tiles the 1,205-byte
///     body exactly (record total 1,270).
///     <para>
///         ⚑ Measured on SAVE0 (2026-09-07): 45 tiles visited, every tile 0 or 1, the trailing u32
///         and u8 both zero. ⚠ UESP suspects the grid varies on interior levels (L5+); this fixture
///         is level 1, so a differently-sized automap is untested — the parser takes the size from
///         the header and reports whether the body tiles.
///     </para>
/// </summary>
internal sealed class BattlespireSaveAutomap
{
    /// <summary>Bytes after the tile grid: a u32 and a u8.</summary>
    public const int TailLength = 5;

    private BattlespireSaveAutomap(int width, int height, byte[] tiles, uint tailWord, byte tailByte, bool tilesExactly)
    {
        Width = width;
        Height = height;
        Tiles = tiles;
        TailWord = tailWord;
        TailByte = tailByte;
        TilesExactly = tilesExactly;
    }

    /// <summary>Grid width, from the header's pitch word.</summary>
    public int Width { get; }

    /// <summary>Grid height, from the header's yaw word.</summary>
    public int Height { get; }

    /// <summary>Row-major explored flags, <c>Width * Height</c> of them.</summary>
    public IReadOnlyList<byte> Tiles { get; }

    /// <summary>The u32 after the grid (0 on the fixture).</summary>
    public uint TailWord { get; }

    /// <summary>The u8 after that (0 on the fixture; UESP has also seen 150).</summary>
    public byte TailByte { get; }

    /// <summary>True when <c>Width * Height + 5</c> equals the body length exactly.</summary>
    public bool TilesExactly { get; }

    /// <summary>How many tiles are non-zero.</summary>
    public int VisitedCount => Tiles.Count(t => t != 0);

    /// <summary>The flag at a grid cell.</summary>
    public byte TileAt(int column, int row)
    {
        return Tiles[row * Width + column];
    }

    /// <summary>
    ///     Parses the body for a grid of the given size. Fails when the body cannot hold the grid
    ///     plus its 5-byte tail; a longer body is accepted and flagged via <see cref="TilesExactly" />.
    /// </summary>
    public static BattlespireSaveAutomap Parse(ReadOnlySpan<byte> body, int width, int height, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"{name}: automap grid {width} x {height} is empty.");
        }

        var tileCount = width * height;
        if (body.Length < tileCount + TailLength)
        {
            throw new InvalidDataException(
                $"{name}: automap body of {body.Length} bytes cannot hold a {width} x {height} grid plus its {TailLength}-byte tail.");
        }

        return new BattlespireSaveAutomap(
            width,
            height,
            body[..tileCount].ToArray(),
            BinaryPrimitives.ReadUInt32LittleEndian(body[tileCount..]),
            body[tileCount + 4],
            body.Length == tileCount + TailLength);
    }
}

/// <summary>
///     The 5-byte body of a SAVETREE Options record (type 23, 70 bytes in all): a fullscreen toggle
///     and four bytes UESP could not attribute. All zero on the fixture. Kept as raw bytes.
/// </summary>
internal sealed class BattlespireSaveOptions
{
    /// <summary>Bytes in the body (record total 70 minus the 65-byte header).</summary>
    public const int BodyLength = 5;

    private BattlespireSaveOptions(byte[] raw)
    {
        Raw = raw;
    }

    /// <summary>+65: the full-screen HUD toggle.</summary>
    public bool IsFullscreen => Raw[0] != 0;

    /// <summary>All five body bytes.</summary>
    public IReadOnlyList<byte> Raw { get; }

    /// <summary>Parses a body of at least <see cref="BodyLength" /> bytes.</summary>
    public static BattlespireSaveOptions Parse(ReadOnlySpan<byte> body, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (body.Length < BodyLength)
        {
            throw new InvalidDataException($"{name}: an options body needs {BodyLength} bytes, got {body.Length}.");
        }

        return new BattlespireSaveOptions(body[..BodyLength].ToArray());
    }
}
