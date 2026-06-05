namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Canonical paths for FNV's engine-default landscape textures, sourced from
///     <c>SDefaultLandDiffuseTexture</c> / <c>SDefaultLandNormalTexture</c> in <c>Fallout.ini</c>.
///     Same value across every shipped FNV build (360 final, July 21 2010, Aug 22 2010, PC final)
///     and Fallout 3 PC final. Xbox 360 BSAs hold the <c>.ddx</c> variant; the loader chain
///     retries with that extension when <c>.dds</c> isn't present.
///
///     Both the 2D world map's <c>LandscapeTexturePalette</c> and the 3D viewer's
///     <c>TerrainTextureResolver</c> consume these paths, each in its own idiom (CPU pixel
///     decode vs GPU upload). Path-string is the right sharing altitude — the loading code
///     diverges from there.
/// </summary>
internal static class EngineDefaultLandscapeTexture
{
    internal const string DiffusePath = @"textures\landscape\DirtWasteland01.dds";
    internal const string NormalPath = @"textures\landscape\DirtWasteland01_N.dds";
}
