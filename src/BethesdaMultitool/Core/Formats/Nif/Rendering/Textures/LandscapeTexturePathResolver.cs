using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Pure chain walk LTEX FormID → texture-set paths, used by both the 2D map
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

        // Starfield: no TNAM/TXST at all — BNAM names a .mat, whose diffuse lives in the compiled
        // material database (materials\materialsbeta.cdb). Returned as the material path so the
        // caller's material resolver can take it, exactly as FO4/FO76 hand off a .bgsm path. Callers
        // WITHOUT a material resolver get a path that resolves to nothing, which is the same
        // untextured outcome as before — never a wrong texture.
        if (!string.IsNullOrWhiteSpace(ltex.MaterialPath))
        {
            return ltex.MaterialPath;
        }

        // Oblivion (TES4): no TNAM/TXST — the texture is the ICON path, relative to textures\landscape\.
        // Prefix it to the textures\-relative form the loader's Normalize expects (Normalize prepends
        // textures\), unless the path is already landscape\- or textures\-rooted.
        return string.IsNullOrWhiteSpace(ltex.IconPath) ? null : PrefixLandscape(ltex.IconPath);
    }

    /// <summary>
    ///     Resolves an LTEX FormID through TNAM to the linked TXST's slot-1 normal texture. Unlike
    ///     <see cref="ResolveDiffuse" />, there is no TES4 ICON fallback: an absent link or TX01 is a
    ///     genuinely missing authored normal and callers should retain the geometric surface normal.
    /// </summary>
    internal static string? ResolveNormal(
        uint ltexFormId,
        IReadOnlyDictionary<uint, LandscapeTextureRecord> ltexByFormId,
        IReadOnlyDictionary<uint, TextureSetRecord> txstByFormId)
    {
        if (!ltexByFormId.TryGetValue(ltexFormId, out var ltex) ||
            ltex.TextureSetFormId is not uint txstFormId ||
            !txstByFormId.TryGetValue(txstFormId, out var txst) ||
            string.IsNullOrWhiteSpace(txst.NormalTexture))
        {
            return null;
        }

        return txst.NormalTexture;
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
