namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     Fallout Tactics' world-to-screen projection, read off BOS.exe rather than fitted.
///     <para>
///         ⚑ The projection object lives at <c>0x8be040</c> and is built by the static
///         initialiser at <c>0x6e1350</c> → <c>FUN_006e12f0</c> → <c>FUN_006e1360(7.5f, 3)</c>:
///         <c>DAT_008be044</c> = 3 (an INT — the horizontal scale) and <c>DAT_008be040</c> = 7.5f
///         (the vertical scale). Nothing else writes either field (two readers, one writer, over the
///         whole executable). Both <c>FUN_0044de60</c> and the tile's own <c>FUN_00704800</c>
///         compute, for a point (x, y, z) in tile units:
///         <c>sx = 2·3·(x − z)</c> and <c>sy = −3·x − 3·z − 7.5·y</c>, rounded — screen y grows
///         DOWNWARD, so a larger x + z (further from the camera) sits HIGHER on screen, and height
///         lifts a point up the screen.
///     </para>
///     <para>
///         ⚑ What that means in pixels, checked against the art: a floor tile is 6 x 1 x 6 units and
///         its stored image rect is 73 x 37, i.e. the classic 72-wide, 36-tall diamond — 6 units
///         along each ground axis at 6 px per unit of (x − z). A wall is 12 units tall: 90 px.
///         ⚑ And checked against the level: with far-first (descending x + z) painter order the
///         Brahmin Wood compound renders with intact walls, burning barrels and lamp posts; with
///         the order reversed the floors behind each wall overdraw it. The render is what settles
///         the sign, not the arithmetic.
///     </para>
///     <para>
///         ⚠ The vertical scale is 7.5, NOT 6: the two constants are independent (int 3 and float
///         7.5) and the rounding of <c>7.5·y</c> at odd y is the FPU's round-half-even, which
///         <see cref="MidpointRounding.ToEven" /> reproduces.
///     </para>
/// </summary>
internal static class TacticsIsometricProjection
{
    /// <summary><c>DAT_008be044</c>: the horizontal scale, 3.</summary>
    public const int HorizontalScale = 3;

    /// <summary><c>DAT_008be040</c>: the vertical scale, 7.5.</summary>
    public const float VerticalScale = 7.5f;

    /// <summary>Screen x of a world point: <c>2·3·(x − z)</c> (<c>FUN_0044de60</c>, first output).</summary>
    public static int ScreenX(int x, int z)
    {
        return 2 * HorizontalScale * (x - z);
    }

    /// <summary>
    ///     Screen x of a fractional world point (an entity's frame position) — the same formula
    ///     without the integer promotion.
    /// </summary>
    public static int ScreenX(float x, float z)
    {
        return (int)Math.Round(2 * HorizontalScale * ((double)x - z), MidpointRounding.ToEven);
    }

    /// <summary>Screen y of a world point: <c>round(−3x − 3z − 7.5y)</c> (<c>FUN_0044de60</c>, second output).</summary>
    public static int ScreenY(int x, int y, int z)
    {
        return (int)Math.Round(-HorizontalScale * (double)x - HorizontalScale * (double)z - VerticalScale * (double)y,
            MidpointRounding.ToEven);
    }

    /// <summary>Screen y of a fractional world point.</summary>
    public static int ScreenY(float x, float y, float z)
    {
        return (int)Math.Round(-HorizontalScale * (double)x - HorizontalScale * (double)z - VerticalScale * (double)y,
            MidpointRounding.ToEven);
    }

    /// <summary>Both screen coordinates of a world point.</summary>
    public static (int X, int Y) Project(int x, int y, int z)
    {
        return (ScreenX(x, z), ScreenY(x, y, z));
    }

    /// <summary>
    ///     Painter order for a static picture: far-to-near, i.e. descending x + z, then ascending
    ///     height so a tile's roof lands after the tile, then file order. Negative for "draw
    ///     <paramref name="a" /> first".
    /// </summary>
    public static int CompareDrawOrder(in TacticsTileInstance a, in TacticsTileInstance b)
    {
        var depth = (b.X + b.Z).CompareTo(a.X + a.Z);
        if (depth != 0)
        {
            return depth;
        }

        return a.Y.CompareTo(b.Y);
    }
}
