using System.Numerics;

namespace BethesdaMultitool;

/// <summary>
///     Compatibility selection for UI paths that still expose terrain shading as a single choice.
///     Newer render code uses independent <see cref="TerrainShadingOptions.VertexColors" /> and
///     <see cref="TerrainShadingOptions.HillShade" /> flags.
/// </summary>
internal enum TerrainShadingMode
{
    /// <summary>Raw blended diffuse, no modulation.</summary>
    None,

    /// <summary>Multiply by the bilinearly-interpolated per-vertex VCLR color.</summary>
    VertexColors,

    /// <summary>Multiply by a Lambertian hillshade computed from the cell's height field.</summary>
    HillShade
}

/// <summary>
///     Modulation applied to the rendered "Terrain textures" layer. Vertex colors and hillshade are
///     INDEPENDENT and combine multiplicatively (engine-accurate VCLR tint × Lambertian relief), so
///     either, both, or neither can be active. The optional <see cref="LightDir" /> drives the hillshade
///     (null = the renderer's NW default). <c>default</c> is neither (raw diffuse).
/// </summary>
internal readonly record struct TerrainShadingOptions(
    bool VertexColors, bool HillShade, Vector3? LightDir = null,
    float ZScale = WorldMapHillshadeRenderer.DefaultZScale)
{
    internal static readonly TerrainShadingOptions None = new(false, false);

    internal TerrainShadingOptions(TerrainShadingMode mode, Vector3? lightDir = null)
        : this(mode == TerrainShadingMode.VertexColors, mode == TerrainShadingMode.HillShade, lightDir)
    {
    }

    internal bool IsActive => VertexColors || HillShade;
}
