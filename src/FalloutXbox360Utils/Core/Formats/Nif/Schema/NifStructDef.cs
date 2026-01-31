namespace FalloutXbox360Utils.Core.Formats.Nif.Schema;

/// <summary>
///     Represents a struct (compound type) definition.
/// </summary>
public sealed class NifStructDef
{
    public required string Name { get; init; }
    public int? FixedSize { get; init; } // Some structs have known fixed size
    public List<NifFieldDef> Fields { get; init; } = [];

    public override string ToString()
    {
        return $"struct {Name} ({Fields.Count} fields)";
    }
}
