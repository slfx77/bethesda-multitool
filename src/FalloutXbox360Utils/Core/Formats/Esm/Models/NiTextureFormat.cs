namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     NiPixelFormat::Format enum values from Gamebryo engine (PDB-verified via nif.xml).
/// </summary>
public enum NiTextureFormat
{
    RGB = 0,
    RGBA = 1,
    PAL = 2,
    PALA = 3,
    DXT1 = 4,
    DXT3 = 5,
    DXT5 = 6,
    RGB24NonInt = 7,
    Bump = 8,
    BumpLuma = 9,
    RenderSpec = 10,
    OneCh = 11,
    TwoCh = 12,
    ThreeCh = 13
}
