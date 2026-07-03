using BethesdaMultitool.Core.Formats.Nif.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Resolves a Fallout 4 / Fallout 76 material file (<c>.bgsm</c> lighting / <c>.bgem</c> effect)
///     to its diffuse texture path. FO4/FO76 shapes don't carry an inline texture set — the shader's
///     Name points at a material under <c>materials\</c> whose texture-path table drives rendering.
///     Shared by both the CPU (<see cref="NifTextureResolver" />) and GPU
///     (<c>NifGpuTextureResolver</c>) texture resolvers so the worldspace viewer and the standalone
///     render/export pipelines resolve materials identically.
/// </summary>
internal static class MaterialTexturePathResolver
{
    /// <summary>
    ///     Loads <paramref name="materialPath" /> from the first source that has it, parses the
    ///     BGSM/BGEM, and returns its diffuse texture path normalized for archive lookup. Returns
    ///     <c>null</c> when the material is absent, unparseable, or carries no diffuse slot.
    /// </summary>
    internal static string? ResolveDiffuseTexturePath(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        var diffuse = ResolveMaterial(materialPath, sources)?.Diffuse;
        return string.IsNullOrEmpty(diffuse) ? null : NifTexturePathUtility.Normalize(diffuse);
    }

    /// <summary>
    ///     Loads <paramref name="materialPath" /> from the first source that has it and parses it.
    ///     Returns <c>null</c> when the material is absent or unparseable. The path is normalized
    ///     first: FO4 shaders commonly bake absolute developer build paths into the material Name
    ///     (e.g. <c>C:\Projects\Fallout4\Build\PC\Data\materials\…\SDMart1Letters.BGSM</c>) — without
    ///     peeling to the Data-relative form the archive lookup misses and the material's render
    ///     state (alpha cutout, two-sided, specular) silently never applies.
    /// </summary>
    internal static BgsmMaterial? ResolveMaterial(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        var normalized = NifTexturePathUtility.Normalize(materialPath);
        foreach (var source in sources)
        {
            var raw = source.TryLoadRaw(normalized);
            if (raw is not null)
            {
                return BgsmMaterial.Parse(raw);
            }
        }

        return null;
    }
}
