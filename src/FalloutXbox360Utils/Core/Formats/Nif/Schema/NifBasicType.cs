namespace FalloutXbox360Utils.Core.Formats.Nif.Schema;

/// <summary>
///     Represents a basic type definition (uint, ushort, float, etc.).
/// </summary>
public sealed class NifBasicType
{
    public required string Name { get; init; }
    public int Size { get; init; }
    public bool IsIntegral { get; init; }
    public bool IsGeneric { get; init; } // Ref, Ptr - need block remapping

    public override string ToString()
    {
        return $"{Name} ({Size} bytes)";
    }
}
