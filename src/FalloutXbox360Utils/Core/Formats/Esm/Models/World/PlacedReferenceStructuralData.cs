namespace FalloutXbox360Utils.Core.Formats.Esm.Models.World;

/// <summary>
///     Raw structural/culling subrecords carried by room, portal, multibound, and occlusion marker references.
///     These bytes are already normalized to PC little-endian form when parsed from Xbox data.
/// </summary>
public record PlacedReferenceStructuralData
{
    public IReadOnlyList<PlacedReferenceStructuralSubrecord> Subrecords { get; init; } = [];

    public bool HasAny => Subrecords.Count > 0;
}

public record PlacedReferenceStructuralSubrecord(string Signature, byte[] Data);
