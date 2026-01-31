namespace FalloutXbox360Utils.Core.Formats.Nif.Schema;

/// <summary>
///     Represents an enum/bitflags definition.
/// </summary>
public sealed class NifEnumDef
{
    public required string Name { get; init; }
    public required string Storage { get; init; } // Underlying type (uint, ushort, byte)

    public override string ToString()
    {
        return $"enum {Name} : {Storage}";
    }
}
