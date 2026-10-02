namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     One immutable value decoded from a NIF block body by <see cref="NifBlockDecoder" />. Values are exact: integers
///     keep their declared width and signedness, floats keep their bits, strings keep their raw bytes. Interpreting a
///     value (enum options, bitfield members, units) is left to typed views over the tree.
/// </summary>
internal abstract class NifValue
{
    private protected NifValue(string typeName)
    {
        TypeName = typeName;
    }

    /// <summary>The nif.xml type the value was decoded as (a basic, enum, bitflags, bitfield or struct name).</summary>
    public string TypeName { get; }

    /// <summary>The shape of the value.</summary>
    public abstract NifValueKind Kind { get; }
}
