namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     AI Package (PACK) record controlling NPC behavior — where they sleep, eat, patrol,
///     wander, and what they do throughout the day.
/// </summary>
public record PackageRecord
{
    /// <summary>FormID of the PACK record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Package data from PKDT subrecord (type, flags).</summary>
    public PackageData? Data { get; init; }

    /// <summary>Package schedule from PSDT subrecord.</summary>
    public PackageSchedule? Schedule { get; init; }

    /// <summary>Primary package location (PLDT subrecord).</summary>
    public PackageLocation? Location { get; init; }

    /// <summary>Secondary package location (PLD2 subrecord).</summary>
    public PackageLocation? Location2 { get; init; }

    /// <summary>Primary package target (PTDT subrecord).</summary>
    public PackageTarget? Target { get; init; }

    /// <summary>Secondary package target (PTD2 subrecord).</summary>
    public PackageTarget? Target2 { get; init; }

    /// <summary>Whether this patrol package is repeatable (from PKPT byte[0]).</summary>
    public bool IsRepeatable { get; init; }

    /// <summary>Whether patrol starting location uses linked ref (from PKPT byte[1]).</summary>
    public bool IsStartingLocationLinkedRef { get; init; }

    /// <summary>Human-readable package type name (from PKDT data).</summary>
    public string TypeName => Data?.TypeName ?? "AI Package";

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
