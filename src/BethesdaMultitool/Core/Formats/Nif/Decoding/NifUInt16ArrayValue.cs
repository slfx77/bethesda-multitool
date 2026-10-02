namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     A bulk array of elements made of ushort components (<c>ushort</c>, <c>Triangle</c>), element-major, each
///     component read in the file's byte order.
/// </summary>
internal sealed class NifUInt16ArrayValue : NifValue
{
    private readonly ushort[] _values;

    /// <summary>Creates a bulk ushort array.</summary>
    /// <param name="typeName">The declared element type.</param>
    /// <param name="componentsPerElement">Components per element (1 for ushort, 3 for Triangle).</param>
    /// <param name="values">The components, element-major, already in host order.</param>
    public NifUInt16ArrayValue(string typeName, int componentsPerElement, ushort[] values)
        : base(typeName)
    {
        if (componentsPerElement <= 0 || values.Length % componentsPerElement != 0)
        {
            throw new ArgumentException(
                $"{values.Length} components do not divide into elements of {componentsPerElement}.", nameof(values));
        }

        ComponentsPerElement = componentsPerElement;
        _values = values;
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.UInt16Array;

    /// <summary>Components per element.</summary>
    public int ComponentsPerElement { get; }

    /// <summary>The number of elements.</summary>
    public int Count => _values.Length / ComponentsPerElement;

    /// <summary>All components, element-major.</summary>
    public ReadOnlySpan<ushort> Values => _values;

    /// <summary>One component.</summary>
    public ushort Get(int element, int component = 0)
    {
        if ((uint)component >= (uint)ComponentsPerElement)
        {
            throw new ArgumentOutOfRangeException(nameof(component), component,
                $"{TypeName} has {ComponentsPerElement} components.");
        }

        return _values[element * ComponentsPerElement + component];
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{TypeName}[{Count}]";
    }
}
