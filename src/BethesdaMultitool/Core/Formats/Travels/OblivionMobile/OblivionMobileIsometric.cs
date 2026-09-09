namespace BethesdaMultitool.Core.Formats.Travels.OblivionMobile;

/// <summary>
///     The projection Oblivion Mobile draws a <see cref="OblivionMobileTileMap" /> with — shared by
///     the exporter and any future viewer so both place tiles identically.
///     <para>
///         Cells are 128x128 world units and the engine maps world (wx, wy) to screen
///         <c>sx = (wx - wy) / 8 - 16</c>, <c>sy = (wx + wy) / 16</c>. Substituting
///         <c>wx = i * 128</c>, <c>wy = j * 128</c> gives cell (i, j) the screen origin
///         <c>((i - j) * 16 - 16, (i + j) * 8)</c>: a 2:1 isometric diamond
///         <see cref="TileWidth" /> x <see cref="TileHeight" /> pixels. <c>i</c> runs toward screen
///         right-down and <c>j</c> toward screen left-down, which is the same j-major orientation
///         the map's cells are stored in.
///     </para>
///     <para>
///         <b>Draw order is i outer, j inner, both ascending</b> — see <see cref="DrawOrder" />. The
///         engine uses that single walk for the cached background layers and for the live
///         object/foreground layer (where it interleaves the actors standing in each cell), and it
///         is <i>not</i> a depth sort: reproduce the walk rather than sorting, or tall tiles
///         overlap the wrong way round.
///     </para>
///     <para>
///         A tile draws at its cell origin <b>plus the atlas frame's (dx, dy)</b> — see
///         <see cref="TileOrigin(int, int, sbyte, sbyte)" />. Wall tiles carry a negative dy so
///         they rise out of the diamond, which is why the composite canvas extends above row 0.
///     </para>
/// </summary>
internal static class OblivionMobileIsometric
{
    /// <summary>Screen width of one cell diamond, in pixels.</summary>
    public const int TileWidth = 32;

    /// <summary>Screen height of one cell diamond, in pixels — half the width, a 2:1 isometric.</summary>
    public const int TileHeight = 16;

    /// <summary>Pixels the origin moves along x per step of i (and the negation of it per step of j).</summary>
    public const int HalfTileWidth = TileWidth / 2;

    /// <summary>Pixels the origin moves down per step of i or of j.</summary>
    public const int HalfTileHeight = TileHeight / 2;

    /// <summary>
    ///     Screen origin (top-left of the diamond's bounding box) of cell
    ///     (<paramref name="i" />, <paramref name="j" />): <c>((i - j) * 16 - 16, (i + j) * 8)</c>.
    ///     Cell (0, 0) sits at (-16, 0), so a composite render offsets by the map's own bounds.
    /// </summary>
    public static (int X, int Y) CellOrigin(int i, int j)
    {
        return ((i - j) * HalfTileWidth - HalfTileWidth, (i + j) * HalfTileHeight);
    }

    /// <summary>
    ///     Where an atlas frame blits: the cell origin plus the frame's own draw offset. Wall tiles
    ///     carry a negative <paramref name="offsetY" /> so they rise above the cell they occupy.
    /// </summary>
    public static (int X, int Y) TileOrigin(int i, int j, sbyte offsetX, sbyte offsetY)
    {
        var (x, y) = CellOrigin(i, j);
        return (x + offsetX, y + offsetY);
    }

    /// <summary>
    ///     Where an atlas frame blits, taking the offsets straight off the frame.
    /// </summary>
    public static (int X, int Y) TileOrigin(int i, int j, OblivionMobileFrame frame)
    {
        return TileOrigin(i, j, frame.OffsetX, frame.OffsetY);
    }

    /// <summary>
    ///     The cell walk the engine paints in: <b>i outer, j inner, both ascending</b>. Every layer
    ///     — background and live — is painted in this order, so the overlap of tall tiles falls out
    ///     of the walk rather than from any depth comparison.
    /// </summary>
    public static IEnumerable<(int I, int J)> DrawOrder(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        for (var i = 0; i < width; i++)
        {
            for (var j = 0; j < height; j++)
            {
                yield return (i, j);
            }
        }
    }
}
