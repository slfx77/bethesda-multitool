namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Texture layer information from ATXT/BTXT subrecords.
/// </summary>
public record LandTextureLayer(
    uint TextureFormId,
    byte Quadrant,
    short Layer,
    long Offset);
