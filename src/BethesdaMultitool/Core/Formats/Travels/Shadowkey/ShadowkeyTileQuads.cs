namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>What a terrain quad is: the plan's five face kinds (cut-2 plan section 4.2).</summary>
internal enum ShadowkeyTileQuadKind
{
    /// <summary>An open cell's floor.</summary>
    Floor,

    /// <summary>An open cell's ceiling.</summary>
    Ceiling,

    /// <summary>A full-height wall where an open cell meets a blocked or off-grid neighbor.</summary>
    Wall,

    /// <summary>A riser where an open neighbor's floor is higher at either shared corner.</summary>
    Riser,

    /// <summary>A downstand where an open neighbor's ceiling is lower at either shared corner.</summary>
    Downstand
}

/// <summary>
///     One corner of a terrain quad on the tile grid: whole tile coordinates and the raw 8.8 height of the
///     <c>.zcp</c> record (256 per tile), so a consumer converts to its own unit without rounding the rule.
/// </summary>
/// <param name="X">Tile-corner column, 0..width.</param>
/// <param name="Y">Tile-corner row, 0..height.</param>
/// <param name="Height">The raw 8.8 fixed-point height.</param>
internal readonly record struct ShadowkeyTileCorner(int X, int Y, short Height);

/// <summary>
///     One terrain quad of the face rule: its kind, the tile face it belongs to (for a material resolver) and its four
///     corners in emission order; the triangles are <c>(A, B, C)</c> and <c>(A, C, D)</c>.
/// </summary>
/// <param name="Kind">Floor, ceiling, wall, riser or downstand.</param>
/// <param name="Face">The cell, its prototype and the tile face kind (floor, ceiling or a wall direction).</param>
/// <param name="A">Corner 0.</param>
/// <param name="B">Corner 1.</param>
/// <param name="C">Corner 2.</param>
/// <param name="D">Corner 3.</param>
internal readonly record struct ShadowkeyTileQuad(
    ShadowkeyTileQuadKind Kind,
    ShadowkeyTileFace Face,
    ShadowkeyTileCorner A,
    ShadowkeyTileCorner B,
    ShadowkeyTileCorner C,
    ShadowkeyTileCorner D);

/// <summary>
///     The one terrain face rule both the viewer (<see cref="ShadowkeyZoneSceneBuilder" />) and the model reader's
///     terrain document call, so the rule lives once (cut-2 plan section 1): every open cell of the <c>.zmp</c> grid
///     emits its floor and its ceiling, then per wall direction a full wall toward a blocked or off-grid neighbor, else
///     a riser where the open neighbor's floor is higher and a downstand where its ceiling is lower.
///     <para>
///         Order is part of the contract: rows y, columns x, and inside a cell floor, ceiling, then the directions
///         +x, -x, +y, -y, each emitting a wall, or a riser then a downstand. The floor walks the corner slots 3, 2, 1, 0
///         (so its normal points up), the ceiling 0, 1, 2, 3. A riser clamps each corner at this cell's own floor
///         (and a downstand at its own ceiling), which keeps a quad that is stepped at one end and level at the other
///         from folding through itself on the 1.9% of shared edges whose corners disagree.
///     </para>
///     <para>
///         ⚑ Corner order, settled by measurement 2026-09-05 (see <see cref="ShadowkeyZoneSceneBuilder" />):
///         <c>.zcp</c> slot 0 is (x, y+1), 1 is (x+1, y+1), 2 is (x+1, y) and 3 is (x, y), 98.13% agreement on shared
///         sloped corners against 58.78% for the runner-up. Blocked cells emit nothing of their own.
///     </para>
/// </summary>
internal static class ShadowkeyTileQuads
{
    /// <summary>
    ///     One entry per wall direction: the neighbor step, this cell's two corner slots along the shared edge, the
    ///     neighbor's two slots for the SAME two world corners, and the tile face kind. The two slots are ordered so that
    ///     the quad A-bottom, B-bottom, B-top, A-top faces INTO the open cell.
    /// </summary>
    private static readonly (int Dx, int Dy, int OurA, int OurB, int TheirA, int TheirB, ShadowkeyTileFaceKind Kind)[]
        WallDirections =
        [
            (1, 0, 1, 2, 0, 3, ShadowkeyTileFaceKind.WallEast),
            (-1, 0, 3, 0, 2, 1, ShadowkeyTileFaceKind.WallWest),
            (0, 1, 0, 1, 3, 2, ShadowkeyTileFaceKind.WallSouth),
            (0, -1, 2, 3, 1, 0, ShadowkeyTileFaceKind.WallNorth)
        ];

    /// <summary>Corner slot to tile-corner offset, two bytes per slot (dx then dy).</summary>
    private static ReadOnlySpan<byte> CornerOffsets => [0, 1, 1, 1, 1, 0, 0, 0];

    /// <summary>The tile-corner offset of one <c>.zcp</c> corner slot.</summary>
    public static (int Dx, int Dy) CornerOffset(int slot)
    {
        if ((uint)slot >= ShadowkeyCellPrototype.CornerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slot), slot, $"A corner slot must be 0..{ShadowkeyCellPrototype.CornerCount - 1}.");
        }

        return (CornerOffsets[slot * 2], CornerOffsets[slot * 2 + 1]);
    }

    /// <summary>
    ///     Enumerates the zone's quads in the rule's order (see the type remarks), optionally without floors, ceilings or
    ///     walls (a wall direction's riser and downstand count as walls).
    /// </summary>
    /// <exception cref="InvalidDataException">A cell indexes a prototype the table does not hold.</exception>
    public static IEnumerable<ShadowkeyTileQuad> Enumerate(
        ShadowkeyZoneMap map,
        ShadowkeyCellPrototypes prototypes,
        bool includeFloors = true,
        bool includeCeilings = true,
        bool includeWalls = true)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(prototypes);
        prototypes.ValidateAgainst(map, prototypes.Name);
        return EnumerateCore(map, prototypes, includeFloors, includeCeilings, includeWalls);
    }

    private static IEnumerable<ShadowkeyTileQuad> EnumerateCore(
        ShadowkeyZoneMap map,
        ShadowkeyCellPrototypes prototypes,
        bool includeFloors,
        bool includeCeilings,
        bool includeWalls)
    {
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                var cell = map.Cell(x, y);
                if (cell.IsBlocked)
                {
                    continue;
                }

                var prototype = prototypes.Records[cell.PrototypeIndex];
                var floor = prototype.FloorCorners;
                var ceiling = prototype.CeilingCorners;

                if (includeFloors)
                {
                    yield return new ShadowkeyTileQuad(ShadowkeyTileQuadKind.Floor,
                        new ShadowkeyTileFace(x, y, ShadowkeyTileFaceKind.Floor, cell, prototype),
                        Corner(x, y, 3, floor[3]), Corner(x, y, 2, floor[2]),
                        Corner(x, y, 1, floor[1]), Corner(x, y, 0, floor[0]));
                }

                if (includeCeilings)
                {
                    yield return new ShadowkeyTileQuad(ShadowkeyTileQuadKind.Ceiling,
                        new ShadowkeyTileFace(x, y, ShadowkeyTileFaceKind.Ceiling, cell, prototype),
                        Corner(x, y, 0, ceiling[0]), Corner(x, y, 1, ceiling[1]),
                        Corner(x, y, 2, ceiling[2]), Corner(x, y, 3, ceiling[3]));
                }

                if (!includeWalls)
                {
                    continue;
                }

                foreach (var (dx, dy, ourA, ourB, theirA, theirB, kind) in WallDirections)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    var face = new ShadowkeyTileFace(x, y, kind, cell, prototype);
                    var a = Corner(x, y, ourA, floor[ourA]);
                    var b = Corner(x, y, ourB, floor[ourB]);
                    var solid = nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height || map.Cell(nx, ny).IsBlocked;
                    if (solid)
                    {
                        yield return new ShadowkeyTileQuad(ShadowkeyTileQuadKind.Wall, face,
                            a, b, b with { Height = ceiling[ourB] }, a with { Height = ceiling[ourA] });
                        continue;
                    }

                    var neighbor = prototypes.Records[map.Cell(nx, ny).PrototypeIndex];
                    var neighborFloor = neighbor.FloorCorners;
                    var neighborCeiling = neighbor.CeilingCorners;
                    if (neighborFloor[theirA] > floor[ourA] || neighborFloor[theirB] > floor[ourB])
                    {
                        yield return new ShadowkeyTileQuad(ShadowkeyTileQuadKind.Riser, face,
                            a, b,
                            b with { Height = Math.Max(floor[ourB], neighborFloor[theirB]) },
                            a with { Height = Math.Max(floor[ourA], neighborFloor[theirA]) });
                    }

                    if (neighborCeiling[theirA] < ceiling[ourA] || neighborCeiling[theirB] < ceiling[ourB])
                    {
                        yield return new ShadowkeyTileQuad(ShadowkeyTileQuadKind.Downstand, face,
                            a with { Height = Math.Min(ceiling[ourA], neighborCeiling[theirA]) },
                            b with { Height = Math.Min(ceiling[ourB], neighborCeiling[theirB]) },
                            b with { Height = ceiling[ourB] },
                            a with { Height = ceiling[ourA] });
                    }
                }
            }
        }
    }

    /// <summary>One tile corner at a raw height.</summary>
    private static ShadowkeyTileCorner Corner(int x, int y, int slot, short height)
    {
        var (dx, dy) = CornerOffset(slot);
        return new ShadowkeyTileCorner(x + dx, y + dy, height);
    }
}
