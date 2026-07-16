namespace BethesdaMultitool.Core.Formats.Nif.Schema;

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
    public string? ExcludeT { get; init; } // Excluded for specific block types (excludeT)
    public string? Arg { get; init; } // Template argument for #ARG# substitution

    /// <summary>
    ///     True if this field's value must be captured during conversion for later use within the
    ///     same block (array-length references, runtime conditions, <c>#ARG#</c> propagation).
    ///     Precomputed once at schema-load time from <see cref="Name" /> via
    ///     <see cref="ComputeStoresValueForLaterUse" /> so the per-field name scan runs once per field
    ///     for the application lifetime instead of once per field per converted block.
    /// </summary>
    public required bool StoresValueForLaterUse { get; init; }

    /// <summary>
    ///     Pure predicate over a field name — the single source of truth for
    ///     <see cref="StoresValueForLaterUse" />. Kept byte-for-byte equivalent to the inline scan it
    ///     replaced: "Num X" / "X Count" / "Has X" / anything containing "Flags" or "Type", plus the
    ///     explicit names "Compressed", "Interpolation" (#ARG# for Key structs) and "Block Size"
    ///     (2D-array width for NiAGDDataBlock.Data).
    /// </summary>
    public static bool ComputeStoresValueForLaterUse(string name) =>
        name.StartsWith("Num ", StringComparison.Ordinal) ||
        name.EndsWith(" Count", StringComparison.Ordinal) ||
        name.StartsWith("Has ", StringComparison.Ordinal) ||
        name.Contains("Flags", StringComparison.Ordinal) ||
        name.Contains("Type", StringComparison.Ordinal) ||
        name == "Compressed" ||
        name == "Interpolation" ||
        name == "Block Size";

    public override string ToString()
    {
        return $"{Name}: {Type}" + (Length != null ? $"[{Length}]" : "") + (Width != null ? $"[{Width}]" : "");
    }
}
