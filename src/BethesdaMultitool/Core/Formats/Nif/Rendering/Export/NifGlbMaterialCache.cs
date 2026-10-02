using SharpGLTF.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Retains preparation and legacy output objects for one export using the same material identity.</summary>
internal sealed class NifGlbMaterialCache
{
    /// <summary>Completed game-policy results; a repeated identity does not repack its textures.</summary>
    internal Dictionary<NifMaterialCacheKey, NifPreparedMaterial> Prepared { get; } = [];
    /// <summary>Legacy channel objects corresponding to completed preparation results.</summary>
    internal Dictionary<NifMaterialCacheKey, MaterialBuilder> Materials { get; } = [];
}
