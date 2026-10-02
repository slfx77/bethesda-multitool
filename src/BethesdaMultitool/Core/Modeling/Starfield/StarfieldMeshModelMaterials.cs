using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     The one material of a Starfield <c>.mesh</c> document (cut-2 plan decision D10). A lone <c>.mesh</c> carries no
///     material: the referencing NIF's <c>BSGeometry</c> names one through its shader property, resolved through
///     Starfield's material database. The reader therefore binds a neutral placeholder, white, opaque, single-sided and
///     lit with the metallic-roughness model at metallic 0 and roughness 1 (the neutral surface cut 1c's XnGine export
///     uses), and says so with <see cref="StarfieldMeshModelDiagnostics.MaterialInNif" />.
/// </summary>
internal static class StarfieldMeshModelMaterials
{
    /// <summary>The placeholder material's name.</summary>
    public const string PlaceholderName = "starfield.mesh.placeholder";

    /// <summary>The placeholder material (see the type summary).</summary>
    public static SceneMaterial Placeholder()
    {
        return new SceneMaterial(PlaceholderName, Vector4.One, texture: null, alphaMode: SceneAlphaMode.Opaque,
            alphaCutoff: 0.5f, doubleSided: false, unlit: false)
        {
            LightingModel = SceneLightingModel.MetallicRoughness,
            MetallicFactor = 0f,
            RoughnessFactor = 1f
        };
    }
}
