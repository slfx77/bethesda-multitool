using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Pure chain walk LTEX FormID → diffuse texture path, used by both the 2D map
///     (<c>LandscapeTexturePalette</c>, CPU-decode path) and the 3D viewer
///     (<c>TerrainTextureResolver</c>, GPU-upload path) so the lookup semantics stay in lock-step.
///     Two forms are supported:
///     <list type="bullet">
///         <item>FO3 / FNV / Skyrim: LTEX → <see cref="LandscapeTextureRecord.TextureSetFormId" /> (TNAM)
///         → the linked <see cref="TextureSetRecord" /> → <see cref="TextureSetRecord.DiffuseTexture" />.</item>
///         <item>Oblivion (TES4): LTEX has no TNAM/TXST — the diffuse path is the LTEX's
///         <see cref="LandscapeTextureRecord.IconPath" /> (ICON), authored relative to
///         <c>textures\landscape\</c>.</item>
///     </list>
///     Returns null when no diffuse path can be derived. Caller owns any <c>.dds → .ddx</c> retry and
///     BSA-source lookup; those live in the loader, not the path walk.
/// </summary>
internal static class LandscapeTexturePathResolver
{
    /// <summary>
    ///     Resolves an LTEX FormID to its diffuse texture path, preferring the TNAM→TXST chain and
    ///     falling back to the Oblivion ICON path. Returns null when the LTEX is unknown and no path can
    ///     be derived from either source.
    /// </summary>
    internal static string? ResolveDiffuse(
        uint ltexFormId,
        IReadOnlyDictionary<uint, LandscapeTextureRecord> ltexByFormId,
        IReadOnlyDictionary<uint, TextureSetRecord> txstByFormId)
    {
        if (!ltexByFormId.TryGetValue(ltexFormId, out var ltex)) return null;

        // FO3 / FNV / Skyrim: LTEX → TNAM → TXST → diffuse path (takes precedence when present).
        if (ltex.TextureSetFormId is uint txstFormId
            && txstByFormId.TryGetValue(txstFormId, out var txst)
            && !string.IsNullOrWhiteSpace(txst.DiffuseTexture))
        {
            return txst.DiffuseTexture;
        }

        // Oblivion (TES4): no TNAM/TXST — the texture is the ICON path, relative to textures\landscape\.
        // Prefix it to the textures\-relative form the loader's Normalize expects (Normalize prepends
        // textures\), unless the path is already landscape\- or textures\-rooted.
        return string.IsNullOrWhiteSpace(ltex.IconPath) ? null : PrefixLandscape(ltex.IconPath);
    }

    private static string PrefixLandscape(string iconPath)
    {
        var path = iconPath.Replace('/', '\\').Trim().TrimStart('\\');
        return path.StartsWith("landscape\\", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase)
            ? path
            : "landscape\\" + path;
    }
}
