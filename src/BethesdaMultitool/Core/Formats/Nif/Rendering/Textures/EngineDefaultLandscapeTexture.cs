using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     The engine-default landscape texture — bound for a quadrant's BASE layer when the cell's LAND has
///     no BTXT for that quadrant, and as the fallback when an LTEX → TXST chain resolves no diffuse.
///     Each engine ships its OWN default (the <c>SDefaultLandDiffuseTexture</c> ini value), so the path
///     is GAME-KEYED: binding Fallout NV's <c>DirtWasteland01</c> for a different game loads nothing
///     (the file isn't in that game's archives) and the base renders white — the "missing terrain
///     textures" symptom on Fallout 4 / Oblivion / Skyrim / Fallout 76 worldspaces.
///     <para>
///         The per-game paths are the single source of truth on <see cref="GameProfile" /> (Core.Games);
///         this is a thin lookup over the registry. Both the 2D world map's
///         <c>LandscapeTexturePalette</c> and the 3D viewer's <c>TerrainTextureResolver</c> consume these
///         paths, each in its own idiom (CPU pixel decode vs GPU upload). Xbox 360 BSAs hold the
///         <c>.ddx</c> variant; the loader chain retries with that extension when <c>.dds</c> isn't present.
///     </para>
/// </summary>
internal static class EngineDefaultLandscapeTexture
{
    /// <summary>Engine-default landscape DIFFUSE for <paramref name="game" /> (from its <see cref="GameProfile" />).</summary>
    internal static string DiffuseFor(BethesdaGame game) => GameProfiles.For(game).DefaultLandscapeDiffuse;

    /// <inheritdoc cref="DiffuseFor" />
    internal static string NormalFor(BethesdaGame game) => GameProfiles.For(game).DefaultLandscapeNormal;
}
