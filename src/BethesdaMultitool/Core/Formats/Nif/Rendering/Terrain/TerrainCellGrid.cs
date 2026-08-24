using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     The four numbers a terrain cell's vertex grid is laid out from: its world origin, the spacing
///     between adjacent grid vertices, and the grid's edge length. Every vertex's X and Y is a pure
///     function of these plus its index, which is why <see cref="TerrainVertex" /> stores only a
///     height — the shader rebuilds the horizontal position from <c>SV_VertexID</c> and four root
///     constants instead of reading 8 bytes per vertex it could have computed.
///     <para>
///         <b>The reconstruction is exact, not approximate</b>, for every grid the games actually
///         use. Cell sizes are 4096 (Fallout/Oblivion/Skyrim) or 8192 (Morrowind) and grids are
///         33/65/129, so <c>spacing</c> is 128 or 32 — a power of two. Origin (<c>gx × cellSize</c>),
///         <c>index × spacing</c> and their sum are all integer-valued floats far below 2²⁴, so each
///         is exactly representable and the result is bit-identical whether the hardware evaluates
///         it as a multiply-then-add or fuses it. <see cref="IsExactlyReconstructible" /> states that
///         precondition so it can be asserted rather than assumed.
///     </para>
///     <para>
///         ⚠ This matters at <b>cell seams</b>, not just in the abstract. Cell A's east column and
///         cell B's west column are the same world position computed from different origins; if the
///         two disagreed by one ULP the terrain would show a hairline crack along every cell
///         boundary in the worldspace.
///     </para>
///     <para>
///         Carried on the built cell rather than re-derived at draw time. The builder already
///         resolves the cell size with a fallback (<c>CellWorldSize</c> when positive, else the
///         engine default), and a second copy of that rule at the draw site is exactly how the
///         renderer and the geometry drift apart.
///     </para>
/// </summary>
internal readonly record struct TerrainCellGrid(float OriginX, float OriginY, float Spacing, int GridSize)
{
    /// <summary>Vertices in the cell: <see cref="GridSize" />².</summary>
    public int VertexCount => GridSize * GridSize;

    /// <summary>
    ///     The world position of vertex <paramref name="vertexIndex" /> at
    ///     <paramref name="height" />, in the same arithmetic and the same order the vertex shader
    ///     uses. Row-major, matching both the builder's fill order and the shared index buffer:
    ///     <c>index = j × gridSize + i</c>.
    /// </summary>
    public Vector3 PositionOf(int vertexIndex, float height)
    {
        var i = vertexIndex % GridSize;
        var j = vertexIndex / GridSize;
        return new Vector3(OriginX + i * Spacing, OriginY + j * Spacing, height);
    }

    /// <summary>
    ///     Whether every X/Y this grid produces is an exactly-representable float, which is what
    ///     makes the GPU-side reconstruction bit-identical to the CPU's regardless of how the driver
    ///     schedules the multiply and add. True when the origin and the full extent are
    ///     integer-valued and stay inside float32's exact-integer range (2²⁴).
    /// </summary>
    public bool IsExactlyReconstructible()
    {
        if (GridSize <= 1 || !IsExactInteger(OriginX) || !IsExactInteger(OriginY) || !IsExactInteger(Spacing))
        {
            return false;
        }

        var extent = (GridSize - 1) * Spacing;
        return IsExactInteger(extent)
               && IsExactInteger(OriginX + extent)
               && IsExactInteger(OriginY + extent);
    }

    private static bool IsExactInteger(float value) =>
#pragma warning disable S1244 // exact comparison is the question being asked: is this float integer-valued? a tolerance would answer a different one
        float.IsFinite(value) && value == MathF.Truncate(value) && MathF.Abs(value) <= 1 << 24;
#pragma warning restore S1244
}
