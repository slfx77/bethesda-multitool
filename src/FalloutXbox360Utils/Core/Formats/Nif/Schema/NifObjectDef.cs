namespace FalloutXbox360Utils.Core.Formats.Nif.Schema;

/// <summary>
///     Represents a NiObject (block type) definition.
/// </summary>
public sealed class NifObjectDef
{
    public required string Name { get; init; }
    public string? Inherit { get; init; }
    public bool IsAbstract { get; init; }
    public List<NifFieldDef> Fields { get; init; } = [];

    // Resolved at runtime - includes inherited fields
    public List<NifFieldDef> AllFields { get; set; } = [];

    public override string ToString()
    {
        return $"niobject {Name}" + (Inherit != null ? $" : {Inherit}" : "") +
               $" ({Fields.Count} own, {AllFields.Count} total)";
    }
}
