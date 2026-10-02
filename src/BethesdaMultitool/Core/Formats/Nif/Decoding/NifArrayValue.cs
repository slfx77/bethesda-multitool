namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     An array that is not bulk-typed: a list of element values (structs, refs, strings, integers of types without a
///     bulk form), or, when <see cref="IsRows" /> is true, the rows of a two-dimensional (<c>length</c> x <c>width</c>)
///     or jagged (<c>width</c> naming an earlier array) array, each row itself an array value.
/// </summary>
internal sealed class NifArrayValue : NifValue
{
    /// <summary>Creates an array value.</summary>
    /// <param name="typeName">The declared element type.</param>
    /// <param name="items">The elements, or the rows when <paramref name="isRows" /> is true.</param>
    /// <param name="isRows">True for a two-dimensional or jagged array.</param>
    public NifArrayValue(string typeName, IReadOnlyList<NifValue> items, bool isRows)
        : base(typeName)
    {
        Items = items;
        IsRows = isRows;
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.Array;

    /// <summary>The elements, or the rows for a two-dimensional or jagged array.</summary>
    public IReadOnlyList<NifValue> Items { get; }

    /// <summary>True when <see cref="Items" /> are rows (each an array value).</summary>
    public bool IsRows { get; }

    /// <summary>The number of elements (or rows).</summary>
    public int Count => Items.Count;

    /// <inheritdoc />
    public override string ToString()
    {
        return IsRows ? $"{TypeName}[{Count}][...]" : $"{TypeName}[{Count}]";
    }
}
