using System.Numerics;

namespace BethesdaMultitool;

/// <summary>
///     How the rendered "Terrain textures" layer is modulated. Mirrors how the engine shades terrain:
///     the diffuse texture is multiplied either by the per-vertex VCLR colors or by a hillshade derived
///     from the height field. <see cref="None" /> leaves the raw blended diffuse.
/// </summary>
internal enum TerrainShadingMode
{
    /// <summary>Raw blended diffuse, no modulation.</summary>
    None,

    /// <summary>Multiply by the bilinearly-interpolated per-vertex VCLR color (engine-accurate).</summary>
    VertexColors,

    /// <summary>Multiply by a Lambertian hillshade computed from the cell's height field.</summary>
    HillShade
}

/// <summary>Shading selection plus the (optional) hillshade light direction, threaded through the
/// terrain-texture render call chain. <c>default</c> is <see cref="TerrainShadingMode.None" />.</summary>
internal readonly record struct TerrainShadingOptions(TerrainShadingMode Mode, Vector3? LightDir = null)
{
    internal static readonly TerrainShadingOptions None = new(TerrainShadingMode.None);

    internal bool IsActive => Mode != TerrainShadingMode.None;
}
