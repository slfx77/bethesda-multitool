using FalloutXbox360Utils.Core.Formats.Esm;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     The engine-default landscape texture — bound for a quadrant's BASE layer when the cell's LAND has
///     no BTXT for that quadrant, and as the fallback when an LTEX → TXST chain resolves no diffuse.
///     Each engine ships its OWN default (the <c>SDefaultLandDiffuseTexture</c> ini value), so the path
///     is GAME-KEYED: binding Fallout NV's <c>DirtWasteland01</c> for a different game loads nothing
///     (the file isn't in that game's archives) and the base renders white — the "missing terrain
///     textures" symptom on Fallout 4 / Oblivion / Skyrim / Fallout 76 worldspaces.
///     <para>
///         Both the 2D world map's <c>LandscapeTexturePalette</c> and the 3D viewer's
///         <c>TerrainTextureResolver</c> consume these paths, each in its own idiom (CPU pixel decode vs
///         GPU upload). Xbox 360 BSAs hold the <c>.ddx</c> variant; the loader chain retries with that
///         extension when <c>.dds</c> isn't present.
///     </para>
/// </summary>
internal static class EngineDefaultLandscapeTexture
{
    // Fallout 3 / New Vegas — Fallout.ini SDefaultLandDiffuseTexture/SDefaultLandNormalTexture. Same
    // value across every shipped FNV build and FO3. Also the fallback for not-yet-mapped games.
    internal const string DiffusePath = @"textures\landscape\DirtWasteland01.dds";
    internal const string NormalPath = @"textures\landscape\DirtWasteland01_N.dds";

    // Fallout 4 + Fallout 76 — "CommonwealthDefault01" (the literally-named engine default; verified
    // present in Fallout4 - Textures1.ba2 AND SeventySix - Textures01.ba2, since FO76 reuses FO4 assets).
    // FO4/76 diffuse maps use the _d suffix, normals _N.
    private const string CommonwealthDiffuse = @"textures\landscape\ground\CommonwealthDefault01_d.dds";
    private const string CommonwealthNormal = @"textures\landscape\ground\CommonwealthDefault01_N.dds";

    // Skyrim (LE + SE) — a verified-loading generic ground base (Skyrim ships no literal "Default"
    // landscape texture; this is the closest neutral dirt, confirmed present via ltex-audit).
    private const string SkyrimDiffuse = @"textures\landscape\Dirt01.dds";
    private const string SkyrimNormal = @"textures\landscape\Dirt01_n.dds";

    // Oblivion — generic dirt base (verified present in Oblivion - Textures - Compressed.bsa). Oblivion
    // landscape textures use the Terrain<region>* naming; this is its neutral dirt.
    private const string OblivionDiffuse = @"textures\landscape\TerrainHDDirt01.dds";
    private const string OblivionNormal = @"textures\landscape\TerrainHDDirt01_n.dds";

    /// <summary>
    ///     Engine-default landscape DIFFUSE for <paramref name="game" />. Each engine ships its own
    ///     default ground texture; binding another game's path loads nothing (→ white base). Games not
    ///     mapped (Morrowind/Starfield) fall back to the FNV path — unchanged behavior, no regression.
    ///     The non-FNV values are verified-present generic ground textures (not necessarily the exact ini
    ///     default, which several games don't expose), so a no-BTXT quadrant shows neutral ground.
    /// </summary>
    internal static string DiffuseFor(BethesdaGame game) => game switch
    {
        BethesdaGame.Fallout4 or BethesdaGame.Fallout76 => CommonwealthDiffuse,
        BethesdaGame.Skyrim => SkyrimDiffuse,
        BethesdaGame.Oblivion => OblivionDiffuse,
        _ => DiffusePath,
    };

    /// <inheritdoc cref="DiffuseFor" />
    internal static string NormalFor(BethesdaGame game) => game switch
    {
        BethesdaGame.Fallout4 or BethesdaGame.Fallout76 => CommonwealthNormal,
        BethesdaGame.Skyrim => SkyrimNormal,
        BethesdaGame.Oblivion => OblivionNormal,
        _ => NormalPath,
    };
}
