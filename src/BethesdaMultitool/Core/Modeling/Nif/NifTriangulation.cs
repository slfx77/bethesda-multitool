namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The triangles <see cref="NifStripTriangulator" /> keeps from a NIF triangle list or strip set, plus what it
///     dropped. Ordinals count stored triangles in file order: for a list, the triangle's position in the list; for
///     strips, <c>i</c> of strip 0, then <c>i</c> of strip 1 offset by strip 0's <c>L - 2</c>, and so on.
/// </summary>
internal sealed class NifTriangulation
{
    /// <summary>Creates a triangulation result.</summary>
    /// <param name="indices">The kept triangles, three vertex indices each, in stored order.</param>
    /// <param name="storedTriangles">Every triangle the source stores, kept or dropped.</param>
    /// <param name="droppedOrdinals">The ordinals of the dropped triangles, ascending.</param>
    /// <param name="droppedPerStrip">For strips, the dropped count per strip in strip order; empty for a list.</param>
    public NifTriangulation(int[] indices, int storedTriangles, IReadOnlyList<int> droppedOrdinals,
        IReadOnlyList<int> droppedPerStrip)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(droppedOrdinals);
        ArgumentNullException.ThrowIfNull(droppedPerStrip);
        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException("Triangle indices must come in triples.", nameof(indices));
        }

        Indices = indices;
        StoredTriangles = storedTriangles;
        DroppedOrdinals = droppedOrdinals;
        DroppedPerStrip = droppedPerStrip;
    }

    /// <summary>The kept triangles, three vertex indices each, in stored order (strip winding already applied).</summary>
    public int[] Indices { get; }

    /// <summary>The number of triangles the source stores (for strips, the sum of <c>max(0, L - 2)</c>).</summary>
    public int StoredTriangles { get; }

    /// <summary>The number of kept triangles.</summary>
    public int KeptTriangles => Indices.Length / 3;

    /// <summary>The number of dropped triangles (each repeated a vertex index).</summary>
    public int DroppedTriangles => DroppedOrdinals.Count;

    /// <summary>The stored-triangle ordinals of the dropped triangles, ascending.</summary>
    public IReadOnlyList<int> DroppedOrdinals { get; }

    /// <summary>For strips, the dropped count of each strip in strip order; empty for a triangle list.</summary>
    public IReadOnlyList<int> DroppedPerStrip { get; }
}
