namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One rectangle of a zone's <c>.zon</c> trigger table: an axis-aligned span of whole map
///     tiles plus the script label the zone's Simkin <c>.s</c> file reacts to (<c>azra.s</c> uses
///     the names <c>ghasts</c>, <c>temple</c>, <c>queue</c>). Nothing here indexes a text table —
///     the join to script logic is by <see cref="Name" />.
///     <para>
///         Coordinates are WHOLE tiles, unlike <see cref="ShadowkeyEntity" /> and
///         <see cref="ShadowkeyPathPoint" />, which are 24.8 fixed point. Measured over all 256
///         retail rectangles: X0 is never past X1, Y0 never past Y1, and every rectangle stays
///         inside its zone's <c>.zmp</c> map (max 127 on the 128-tile zones, max column 53 and row
///         37 on ffarena's 64-tile map). The reader does not enforce that ordering — a degenerate
///         rectangle is data worth surfacing, not a reason to refuse the file.
///     </para>
/// </summary>
/// <param name="X0">Left tile column, inclusive.</param>
/// <param name="Y0">Top tile row, inclusive.</param>
/// <param name="X1">Right tile column, inclusive.</param>
/// <param name="Y1">Bottom tile row, inclusive.</param>
/// <param name="Name">
///     The script label, cut at the first NUL of the 64-byte field. Bytes after that NUL are
///     0xCC stack fill or stale text left by an earlier record of the same file — of the 256
///     retail rectangles, 72 have a tail of pure 0xCC, 184 have something else in it and 142 of
///     those carry printable stale text — and are discarded.
/// </param>
internal sealed record ShadowkeyTriggerZone(ushort X0, ushort Y0, ushort X1, ushort Y1, string Name)
{
    /// <summary>Tiles spanned horizontally, counting both edges.</summary>
    public int Width => X1 - X0 + 1;

    /// <summary>Tiles spanned vertically, counting both edges.</summary>
    public int Height => Y1 - Y0 + 1;
}
