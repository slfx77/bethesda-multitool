namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One 36-byte record of a Shadowkey <c>.zcp</c> cell-prototype table
///     (<see cref="ShadowkeyCellPrototypes" />): the floor/ceiling geometry and surface assignment
///     a grid cell points at. The table is de-duplicated, so a prototype is shared by every cell
///     that looks the same.
///     <code>
///     +0   u8       shade         small signed value, same unit as the cell's byte +3 (HYPOTHESIS)
///     +1   u8       padding       0xCD on 66,829/66,829 retail records: MSVC heap fill after a lone u8
///     +2   i16      reference floor    8.8 fixed point (HYPOTHESIS)
///     +4   i16      reference ceiling  8.8 fixed point (HYPOTHESIS)
///     +6   i16[4]   floor corner heights   8.8 fixed point
///     +14  i16[4]   ceiling corner heights 8.8 fixed point
///     +22  u8[8]    surface slots: 0xFF = none, else an index into the zone's .sur table
///     +30  u8[4]    edge/wall bytes, unresolved
///     +34  u16      unresolved (0..2302, 938 distinct values)
///     </code>
///     <para>
///         Measured over the 66,829 records of the 21 retail zones 2026-09-05: the heights are
///         multiples of 1/256 with 0x0400 (= 4.0, the standard room height) dominant — 43,082
///         records carry it as the ceiling; the +4 word equals the ceiling whenever the four
///         ceiling corners agree (55,676 records); and no surface slot in 534,632 slot bytes is
///         outside <c>{0xFF} u [0, .sur count)</c>. The slots are NOT <c>.ztx</c> texture indices:
///         ELEVEN zones use a slot value at or above their texture count (snowline 26 vs 19;
///         re-measured 2026-09-07 — this said "eight", which undercounted azra, broken1, crypt2,
///         delfhide, dstar_w, fearfrst, GhstPass, GlacierCrawl, lakvan, LothCav and snowline).
///     </para>
///     <para>
///         ⚑ The eight slots are FOUR EDGE DIRECTIONS x TWO BANDS, not six faces (measured
///         2026-09-07 over all 21 zones): <c>s0/s4</c> = -x, <c>s1/s5</c> = +x, <c>s2/s6</c> = -y,
///         <c>s3/s7</c> = +y, with 0-3 the ceiling-side band and 4-7 the floor-side band. Scored
///         0.9425 and 0.9419 against 0.0152 for a rotated assignment on cells with exactly one
///         non-flush edge, and reproduced independently.
///         ⛔ Whether the floor and ceiling themselves have a slot is still OPEN: the oracle above
///         is drawn only from step-bearing cells, where "wall risers only" and "the band's common
///         value is the horizontal face, overridden per edge" are indistinguishable.
///     </para>
///     <para>
///         Solidity is encoded as "floor meets ceiling": every one of the 83,775 retail grid cells
///         flagged blocked references a record whose lowest ceiling corner is at or below its
///         highest floor corner, which is what <see cref="IsSolid" /> tests. The converse is nearly
///         but not quite true — 253 of 248,001 open cells reference a solid-looking record, all in
///         five outdoor/cave zones — so blocking is read from the cell flag, never from here.
///     </para>
/// </summary>
internal sealed class ShadowkeyCellPrototype
{
    /// <summary>Bytes in one record.</summary>
    public const int RecordLength = 36;

    /// <summary>Floor and ceiling corners per record.</summary>
    public const int CornerCount = 4;

    /// <summary>Surface slots per record.</summary>
    public const int SurfaceSlotCount = 8;

    /// <summary>Edge bytes per record.</summary>
    public const int EdgeByteCount = 4;

    /// <summary>Surface-slot value meaning "no surface".</summary>
    public const byte NoSurface = 0xFF;

    private readonly short[] _ceilingCorners;
    private readonly byte[] _edge;

    private readonly short[] _floorCorners;
    private readonly byte[] _surfaceSlots;

    internal ShadowkeyCellPrototype(
        sbyte shade,
        byte padding,
        short referenceFloor,
        short referenceCeiling,
        short[] floorCorners,
        short[] ceilingCorners,
        byte[] surfaceSlots,
        byte[] edge,
        ushort extra)
    {
        Shade = shade;
        Padding = padding;
        ReferenceFloor = referenceFloor;
        ReferenceCeiling = referenceCeiling;
        _floorCorners = floorCorners;
        _ceilingCorners = ceilingCorners;
        _surfaceSlots = surfaceSlots;
        _edge = edge;
        Extra = extra;
    }

    /// <summary>
    ///     Byte +0 read as signed. HYPOTHESIS: the prototype's default shade — it is in the same
    ///     unit as <see cref="ShadowkeyMapCell.Shade" />, which it equals on 12,083 of Crypt3's
    ///     16,384 cells and 16,300 of stouttp's.
    /// </summary>
    public sbyte Shade { get; }

    /// <summary>
    ///     Byte +1, exposed for diagnostics only. It is 0xCD heap fill on every retail record, so
    ///     it is not treated as a contract and never rejected.
    /// </summary>
    public byte Padding { get; }

    /// <summary>i16 at +2, 8.8 fixed point. HYPOTHESIS: floor height at a reference point.</summary>
    public short ReferenceFloor { get; }

    /// <summary>i16 at +4, 8.8 fixed point. HYPOTHESIS: ceiling height at a reference point.</summary>
    public short ReferenceCeiling { get; }

    /// <summary>The four floor corner heights, 8.8 fixed point.</summary>
    public IReadOnlyList<short> FloorCorners => _floorCorners;

    /// <summary>The four ceiling corner heights, 8.8 fixed point.</summary>
    public IReadOnlyList<short> CeilingCorners => _ceilingCorners;

    /// <summary>
    ///     The eight surface slots: <see cref="NoSurface" /> or an index into the zone's
    ///     <c>.sur</c> table. Which slot is which face is unresolved.
    /// </summary>
    public IReadOnlyList<byte> SurfaceSlots => _surfaceSlots;

    /// <summary>The four bytes at +30. Wall/edge related; unresolved.</summary>
    public IReadOnlyList<byte> Edge => _edge;

    /// <summary>The u16 at +34. Unresolved.</summary>
    public ushort Extra { get; }

    /// <summary>
    ///     True when the lowest ceiling corner is at or below the highest floor corner — the
    ///     "floor meets ceiling" encoding of a solid cell.
    /// </summary>
    public bool IsSolid
    {
        get
        {
            var lowestCeiling = _ceilingCorners[0];
            var highestFloor = _floorCorners[0];
            for (var i = 1; i < CornerCount; i++)
            {
                if (_ceilingCorners[i] < lowestCeiling)
                {
                    lowestCeiling = _ceilingCorners[i];
                }

                if (_floorCorners[i] > highestFloor)
                {
                    highestFloor = _floorCorners[i];
                }
            }

            return lowestCeiling <= highestFloor;
        }
    }

    /// <summary>Converts one of this record's 8.8 fixed-point heights to world units.</summary>
    public static float ToUnits(short fixed88)
    {
        return fixed88 / 256f;
    }
}
