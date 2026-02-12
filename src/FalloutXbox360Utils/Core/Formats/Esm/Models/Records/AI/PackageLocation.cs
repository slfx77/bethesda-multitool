namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Package location from PLDT/PLD2 subrecord (12 bytes).
/// </summary>
public record PackageLocation
{
    /// <summary>Location type: 0=NearRef, 1=InCell, 2=NearCurrent, 3=NearEditor, 4=ObjectID, 5=ObjectType, 12=NearLinkedRef.</summary>
    public byte Type { get; init; }

    /// <summary>FormID or enum value, meaning depends on Type.</summary>
    public uint Union { get; init; }

    /// <summary>Search radius.</summary>
    public int Radius { get; init; }
}
