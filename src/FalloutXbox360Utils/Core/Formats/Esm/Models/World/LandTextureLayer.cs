namespace FalloutXbox360Utils.Core.Formats.Esm.Models.World;

/// <summary>
///     LAND texture layer kind from BTXT/ATXT subrecords.
/// </summary>
public enum LandTextureLayerKind
{
    /// <summary>Base texture layer (BTXT).</summary>
    Base,

    /// <summary>Alpha/blended texture layer (ATXT).</summary>
    Alpha
}

/// <summary>
///     LAND VTXT blend entry associated with the preceding ATXT layer.
/// </summary>
public record LandTextureBlendEntry(
    ushort Position,
    byte Unused0,
    byte Unused1,
    float Opacity);

/// <summary>
///     Texture layer information from ATXT/BTXT subrecords.
/// </summary>
public record LandTextureLayer
{
    public required LandTextureLayerKind Kind { get; init; }

    public uint TextureFormId { get; init; }

    public byte Quadrant { get; init; }

    public byte PlatformFlag { get; init; }

    public ushort Layer { get; init; }

    public List<LandTextureBlendEntry> BlendEntries { get; init; } = [];

    public long Offset { get; init; }

    public string SubrecordSignature => Kind == LandTextureLayerKind.Base ? "BTXT" : "ATXT";
}
