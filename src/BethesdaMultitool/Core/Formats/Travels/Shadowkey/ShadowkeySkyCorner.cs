namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One texture corner of a Shadowkey <c>.zsk</c> sky mesh: a UV pair in 8.8 fixed point.
///     Measured on all 21 retail files, every component has a zero fraction and lies in 0.0..255.0,
///     i.e. the values are whole texels of a 256-unit space rather than normalised coordinates.
///     Corners are indexed by <see cref="ShadowkeySkyFace" /> independently of the vertices, which
///     is why a 30-vertex dome carries 168 of them.
/// </summary>
internal readonly record struct ShadowkeySkyCorner(ushort U, ushort V)
{
    /// <summary>The U component in texels (the 8.8 value divided by 256).</summary>
    public float UnitsU => U / 256f;

    /// <summary>The V component in texels (the 8.8 value divided by 256).</summary>
    public float UnitsV => V / 256f;
}
