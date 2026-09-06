namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One 6-byte cell of a Shadowkey zone grid (see <see cref="ShadowkeyZoneMap" />):
///     <c>u8 flags, u8 flags2, u16 raw, u16 prototypeIndex</c>, little-endian.
///     <para>
///         Only <see cref="Flags" /> bit <see cref="BlockedFlag" /> and
///         <see cref="PrototypeIndex" /> are settled. Bit 0x02 means blocked/solid: over the 21
///         retail zones every one of the 83,775 cells carrying it references a
///         <see cref="ShadowkeyCellPrototype" /> whose ceiling meets its floor, and entities sit on
///         such a cell only 12 times in 8,255. The other <see cref="Flags" /> bits (0x01, 0x04,
///         0x08, 0x10, 0x20, 0x40, 0x80) and the four bits ever seen in <see cref="Flags2" />
///         (0x02, 0x04, 0x20, 0x40) are unresolved, so both bytes are exposed whole.
///     </para>
///     <para>
///         <b>The u16 at +2 has two rival readings and this type commits to neither.</b> Its low 6
///         bits are zero in all 331,776 retail cells, so byte +2 only ever holds 0x00/0x40/0x80/0xC0
///         and byte +3 is at most 63. That admits either (a) a single 8-bit value, <c>Raw &gt;&gt; 6</c>,
///         or (b) a 2-bit field in bits 6-7 plus a 6-bit field in bits 8-13. Reading (b) is the one
///         the spatial evidence favours — byte +3 alone is smooth across the grid (mean |dx| 1.1 vs
///         5.1 for a shuffle, in Crypt3) and is exactly 0 around the whole perimeter ring in 19 of
///         21 zones, while bits 6-7 are locally noisy — which is why it is offered as
///         <see cref="Shade" /> plus <see cref="Orientation" />, with reading (a) as
///         <see cref="PackedValue" />. Both are derived views:
///         <see cref="Raw" /> is the byte-exact u16 and is what a consumer should persist.
///     </para>
/// </summary>
/// <param name="Flags">Byte +0. Bit <see cref="BlockedFlag" /> is the only settled meaning.</param>
/// <param name="Flags2">Byte +1. Unresolved; never holds a bit outside 0x02/0x04/0x20/0x40 on retail.</param>
/// <param name="Raw">The un-interpreted u16 at +2. Low 6 bits are zero on 331,776/331,776 retail cells.</param>
/// <param name="PrototypeIndex">u16 at +4: an index into the zone's <c>.zcp</c> table.</param>
internal readonly record struct ShadowkeyMapCell(byte Flags, byte Flags2, ushort Raw, ushort PrototypeIndex)
{
    /// <summary>Bit of <see cref="Flags" /> that marks a cell blocked/solid.</summary>
    public const byte BlockedFlag = 0x02;

    /// <summary>True when the cell is blocked/solid (<see cref="Flags" /> bit 0x02).</summary>
    public bool IsBlocked => (Flags & BlockedFlag) != 0;

    /// <summary>
    ///     Reading (b): bits 8-13 of <see cref="Raw" />, i.e. byte +3, 0..63. HYPOTHESIS — a
    ///     per-cell light/shade level, in the same unit as <see cref="ShadowkeyCellPrototype.Shade" />
    ///     (the two agree on 12,083 of Crypt3's 16,384 cells and 16,300 of stouttp's).
    /// </summary>
    public byte Shade => (byte)(Raw >> 8);

    /// <summary>
    ///     Reading (b): bits 6-7 of <see cref="Raw" />, 0..3. HYPOTHESIS — an orientation/rotation
    ///     of the prototype. Set on 164,721 open and 15,488 blocked retail cells.
    /// </summary>
    public byte Orientation => (byte)((Raw >> 6) & 0x03);

    /// <summary>
    ///     Reading (a): the whole field as one 8-bit value, <c>Raw &gt;&gt; 6</c>. Equal to
    ///     <c>(Shade &lt;&lt; 2) | Orientation</c> — a rival split, not extra data.
    /// </summary>
    public byte PackedValue => (byte)(Raw >> 6);

    /// <summary>True when the low 6 bits of <see cref="Raw" /> are clear, as on every retail cell.</summary>
    public bool RawLowBitsClear => (Raw & 0x3F) == 0;
}
