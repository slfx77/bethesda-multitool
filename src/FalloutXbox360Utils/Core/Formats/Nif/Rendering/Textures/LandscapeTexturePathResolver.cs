using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Pure chain walk: LTEX FormID → its <see cref="LandscapeTextureRecord.TextureSetFormId" />
///     → the linked <see cref="TextureSetRecord" /> → its <see cref="TextureSetRecord.DiffuseTexture" />
///     path. Returns null if any link is missing. No D3D dependencies — both the 2D map
///     (<c>LandscapeTexturePalette</c>, CPU-decode path) and the 3D viewer
///     (<c>TerrainTextureResolver</c>, GPU-upload path) share this single resolver to keep
///     the lookup semantics in lock-step.
/// </summary>
internal static class LandscapeTexturePathResolver
{
    /// <summary>
    ///     Resolves an LTEX FormID to its diffuse texture path. Returns null when the LTEX is
    ///     unknown, its TXST FormID is unset, the TXST is unknown, or the TXST has no
    ///     <c>DiffuseTexture</c> set. Caller is responsible for any <c>.dds → .ddx</c> retry
    ///     and BSA-source lookup; those live in the loader, not the path walk.
    /// </summary>
    internal static string? ResolveDiffuse(
        uint ltexFormId,
        IReadOnlyDictionary<uint, LandscapeTextureRecord> ltexByFormId,
        IReadOnlyDictionary<uint, TextureSetRecord> txstByFormId)
    {
        if (!ltexByFormId.TryGetValue(ltexFormId, out var ltex)) return null;
        if (ltex.TextureSetFormId is not uint txstFormId) return null;
        if (!txstByFormId.TryGetValue(txstFormId, out var txst)) return null;
        var path = txst.DiffuseTexture;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
