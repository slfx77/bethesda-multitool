using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The materials of an XnGine <c>.3D</c> document. Slice 5 of the cut-1c plan reads geometry only: every primitive
///     gets a PLACEHOLDER material named as the legacy export names an unresolved texture (<c>TEXTURE.aaa#r</c>,
///     <see cref="XnGineTextureKey.MaterialName" />), white, opaque, double-sided and lit with the metallic-roughness
///     model at metallic 0 and roughness 1, exactly the material the legacy export writes, so both writers accept the
///     document. Texture and palette resolution (images, the solid-color swatches, the Battlespire BSI stems as names)
///     belongs to slice 7, which replaces these.
/// </summary>
/// <remarks>
///     Cull mode and the lighting row for XnGine materials are open questions (plan section 11); the placeholder copies
///     the legacy output's choices (double-sided, metallic 0, roughness 1) rather than deciding them.
/// </remarks>
internal static class XnGineModelMaterials
{
    /// <summary>One placeholder material per primitive key, in primitive order (material i serves primitive i).</summary>
    public static IReadOnlyList<SceneMaterial> Placeholders(IReadOnlyList<XnGineTextureKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var materials = new SceneMaterial[keys.Count];
        for (var i = 0; i < materials.Length; i++)
        {
            materials[i] = new SceneMaterial(keys[i].MaterialName, Vector4.One, texture: null,
                alphaMode: SceneAlphaMode.Opaque, alphaCutoff: 0.5f, doubleSided: true, unlit: false)
            {
                LightingModel = SceneLightingModel.MetallicRoughness,
                MetallicFactor = 0f,
                RoughnessFactor = 1f
            };
        }

        return materials;
    }
}
