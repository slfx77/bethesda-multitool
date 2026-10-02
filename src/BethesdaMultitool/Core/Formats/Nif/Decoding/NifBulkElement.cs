namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     An element type whose arrays decode into one typed buffer instead of a list of values: every element is the
///     same number of same-typed components with no conditions, so each component is read in file order.
/// </summary>
/// <param name="TypeName">The nif.xml element type.</param>
/// <param name="Component">The component storage.</param>
/// <param name="ComponentsPerElement">Components per element.</param>
internal readonly record struct NifBulkElement(string TypeName, NifBulkComponent Component, int ComponentsPerElement)
{
    /// <summary>The bytes one component occupies.</summary>
    public int ComponentSize => Component switch
    {
        NifBulkComponent.Float32 => 4,
        NifBulkComponent.UInt16 => 2,
        _ => 1
    };

    /// <summary>The bytes one element occupies.</summary>
    public int ElementSize => ComponentSize * ComponentsPerElement;
}
