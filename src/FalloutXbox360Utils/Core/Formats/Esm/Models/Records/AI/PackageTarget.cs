namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Package target from PTDT/PTD2 subrecord (16 bytes).
///     Defines what the AI package targets.
/// </summary>
public record PackageTarget
{
    /// <summary>Target type: 0=Specific Reference, 1=Object ID, 2=Object Type, 3=Linked Reference.</summary>
    public byte Type { get; init; }

    /// <summary>FormID (types 0, 1) or object type enum value (type 2).</summary>
    public uint FormIdOrType { get; init; }

    /// <summary>Count or distance value, meaning depends on package type.</summary>
    public int CountDistance { get; init; }

    /// <summary>Acquire radius for the target.</summary>
    public float AcquireRadius { get; init; }

    /// <summary>Human-readable target type name.</summary>
    public string TypeName => Type switch
    {
        0 => "Specific Reference",
        1 => "Object ID",
        2 => "Object Type",
        3 => "Linked Reference",
        _ => $"Unknown ({Type})"
    };
}
