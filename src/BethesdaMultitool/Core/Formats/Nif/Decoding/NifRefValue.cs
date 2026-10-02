namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     A block link: <c>Ref</c> (points down the hierarchy) or <c>Ptr</c> (points back up), stored as a signed 32-bit
///     block index where -1 means none. <see cref="Template" /> is the block type nif.xml says the target must be.
/// </summary>
internal sealed class NifRefValue : NifValue
{
    /// <summary>Creates a link value.</summary>
    public NifRefValue(string typeName, int index, string? template)
        : base(typeName)
    {
        Index = index;
        Template = template;
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.Reference;

    /// <summary>The stored block index (-1 for none).</summary>
    public int Index { get; }

    /// <summary>The block type the target must be or inherit from, when nif.xml declares one.</summary>
    public string? Template { get; }

    /// <summary>True for <c>Ptr</c>, false for <c>Ref</c>.</summary>
    public bool IsPointer => string.Equals(TypeName, "Ptr", StringComparison.Ordinal);

    /// <summary>True when the index is -1.</summary>
    public bool IsNone => Index == -1;

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{TypeName}<{Template ?? "?"}> {Index}";
    }
}
