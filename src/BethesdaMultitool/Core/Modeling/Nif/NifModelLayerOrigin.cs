using System.Globalization;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Where one layer of a cut-1a material came from: the property or texture-set block that contributed it and the
///     slot it filled there (cut-1b slice 14: the map a property track needs from a texture slot to the ordinal in
///     <see cref="Slfx77.Multitool.Core.Models.SceneMaterialSource.Layers" />). Recorded by
///     <see cref="NifModelMaterialReader" /> beside each layer, in layer order.
/// </summary>
/// <param name="SourceBlock">The block that authored the layer: an NiTexturingProperty, a BSShaderTextureSet or a shader property.</param>
/// <param name="Slot">
///     The slot within that block: an NiTexturingProperty map's slot name (<c>Base</c>, <c>Dark</c>, <c>Detail</c>,
///     <c>Gloss</c>, <c>Glow</c>, <c>Normal</c>, <c>Parallax</c>, <c>Decal 0</c> to <c>Decal 3</c>, the names
///     <see cref="NifTexturingPropertyView" /> gives its maps), or one of the shader-side keys below.
/// </param>
internal readonly record struct NifModelLayerOrigin(int SourceBlock, string Slot)
{
    /// <summary>The slot key of a BSShaderNoLightingProperty, Sky, Tile or TallGrass shader File Name layer.</summary>
    public const string ShaderFileName = "shader:fileName";

    /// <summary>The slot key of the specular layer read from texture set slot 1's alpha.</summary>
    public const string NormalMapGloss = "shader:normalMapGloss";

    /// <summary>The slot key of the specular layer bound from an Xbox normal map's _s companion.</summary>
    public const string XboxSpecularCompanion = "shader:xboxSpecularCompanion";

    /// <summary>The slot key of a BSShaderTextureSet slot.</summary>
    /// <param name="slot">The texture set slot (0 to 5).</param>
    /// <returns><c>set:N</c>.</returns>
    public static string TextureSetSlot(int slot)
    {
        return string.Create(CultureInfo.InvariantCulture, $"set:{slot}");
    }
}
