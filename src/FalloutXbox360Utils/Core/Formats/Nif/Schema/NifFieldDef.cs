namespace FalloutXbox360Utils.Core.Formats.Nif.Schema;

/// <summary>
///     Represents a field in a NIF type definition.
/// </summary>
public sealed class NifFieldDef
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Template { get; init; }
    public string? Length { get; init; } // Array count expression (e.g., "Num Vertices")
    public string? Width { get; init; } // Second dimension for 2D arrays (e.g., "Num Vertices" for UV Sets)
    public string? Condition { get; init; } // Runtime condition (cond)
    public string? VersionCond { get; init; } // Version condition (vercond)
    public string? Since { get; init; } // Minimum version
    public string? Until { get; init; } // Maximum version
    public string? OnlyT { get; init; } // Only for specific block types (onlyT)
    public string? Arg { get; init; } // Template argument for #ARG# substitution

    public override string ToString()
    {
        return $"{Name}: {Type}" + (Length != null ? $"[{Length}]" : "") + (Width != null ? $"[{Width}]" : "");
    }
}
