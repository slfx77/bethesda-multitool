namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Placeable Water (PWAT) record. References a <see cref="WaterRecord" /> via the DNAM
///     subrecord's water FormID; the engine treats PWAT placements as instances of the
///     referenced water type with the embedded flags as per-placement overrides.
/// </summary>
public record PlaceableWaterRecord
{
    public uint FormId { get; init; }

    public string? EditorId { get; init; }

    public ObjectBounds? Bounds { get; init; }

    /// <summary>Model file path (MODL subrecord). Optional — engine falls back to default.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Model texture data (MODT subrecord, opaque binary blob — unparsed).</summary>
    public byte[]? ModelTextureData { get; init; }

    /// <summary>FormID of the parent WATR record. DNAM bytes 0..3.</summary>
    public uint WaterFormId { get; init; }

    /// <summary>Placement-override flags. DNAM bytes 4..7.</summary>
    public uint Flags { get; init; }

    public long Offset { get; init; }

    public bool IsBigEndian { get; init; }
}
