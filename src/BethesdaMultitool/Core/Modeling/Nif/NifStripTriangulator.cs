namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Turns NIF 20.2.0.7 triangle strips and triangle lists into the reader's triangle list (plan section 3, "Strip
///     rule"; decisions header). Pure: it knows nothing about blocks, and it deliberately does not call
///     <c>NifTriStripExtractor</c> or <c>NifSubmeshExtractor</c>, which are the independent oracle for hop A3.
/// </summary>
/// <remarks>
///     <para>
///         Strips: for each strip of length L, for i in 0..L-3, take (a, b, c) = (p[i], p[i+1], p[i+2]). When any two
///         are equal the triangle is dropped and counted; otherwise (a, b, c) is emitted when i is even and (a, c, b)
///         when i is odd. Parity counts from the strip start, skipped triangles included, so a stitched strip keeps each
///         original strip's winding.
///     </para>
///     <para>
///         Lists: a triangle that repeats an index is dropped and counted; every other triangle is kept in order.
///         Measured 2026-09-24, these are landscape-LOD stitching artifacts (about half of each LOD list) plus a handful
///         in 13 other FNV files, and the Blender writer reports any face repeating a vertex as Dropped.
///     </para>
///     <para>
///         In both forms a triangle with three distinct indices is kept even when its positions have zero area: the
///         rule looks at indices only, so it removes no authored area.
///     </para>
/// </remarks>
internal static class NifStripTriangulator
{
    /// <summary>Triangulates strips with the parity rule, dropping and counting repeated-index triangles.</summary>
    /// <param name="strips">Each strip's points in stored order.</param>
    /// <returns>The kept triangles and the drop counts.</returns>
    public static NifTriangulation Triangulate(IReadOnlyList<ushort[]> strips)
    {
        ArgumentNullException.ThrowIfNull(strips);
        long stored = 0;
        foreach (var strip in strips)
        {
            ArgumentNullException.ThrowIfNull(strip, nameof(strips));
            stored += Math.Max(0, strip.Length - 2);
        }

        if (stored > int.MaxValue / 3)
        {
            throw new ArgumentOutOfRangeException(nameof(strips), stored, "The strips store too many triangles.");
        }

        var indices = new List<int>((int)stored * 3);
        var dropped = new List<int>();
        var droppedPerStrip = new int[strips.Count];
        var ordinal = 0;
        for (var s = 0; s < strips.Count; s++)
        {
            var points = strips[s];
            for (var i = 0; i + 2 < points.Length; i++, ordinal++)
            {
                int a = points[i], b = points[i + 1], c = points[i + 2];
                if (a == b || b == c || a == c)
                {
                    dropped.Add(ordinal);
                    droppedPerStrip[s]++;
                    continue;
                }

                indices.Add(a);
                if ((i & 1) == 0)
                {
                    indices.Add(b);
                    indices.Add(c);
                }
                else
                {
                    indices.Add(c);
                    indices.Add(b);
                }
            }
        }

        return new NifTriangulation([.. indices], (int)stored, dropped.AsReadOnly(), droppedPerStrip);
    }

    /// <summary>Filters a stored triangle list, dropping and counting every triangle that repeats an index.</summary>
    /// <param name="components">The stored triangles, three indices each, in file order.</param>
    /// <returns>The kept triangles and the drop count.</returns>
    public static NifTriangulation FilterList(ReadOnlySpan<ushort> components)
    {
        if (components.Length % 3 != 0)
        {
            throw new ArgumentException("A triangle list stores three indices per triangle.", nameof(components));
        }

        var stored = components.Length / 3;
        var indices = new List<int>(components.Length);
        var dropped = new List<int>();
        for (var t = 0; t < stored; t++)
        {
            int a = components[t * 3], b = components[t * 3 + 1], c = components[t * 3 + 2];
            if (a == b || b == c || a == c)
            {
                dropped.Add(t);
                continue;
            }

            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
        }

        return new NifTriangulation([.. indices], stored, dropped.AsReadOnly(), Array.Empty<int>());
    }
}
