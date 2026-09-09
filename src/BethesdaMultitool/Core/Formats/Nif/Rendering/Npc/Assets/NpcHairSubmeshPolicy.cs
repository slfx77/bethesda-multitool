using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     Applies actor hair materials to every shape selected by the NIF extractor. Double-sidedness
///     is render state, not a semantic scalp/hair discriminator, so it must never remove geometry.
/// </summary>
internal static class NpcHairSubmeshPolicy
{
    internal static void Apply(
        NifRenderableModel hairModel,
        BethesdaGame game,
        (float R, float G, float B)? tint,
        NifTextureResolver textureResolver,
        string? diffuseTexturePath)
    {
        ArgumentNullException.ThrowIfNull(hairModel);
        ArgumentNullException.ThrowIfNull(textureResolver);

        foreach (var submesh in hairModel.Submeshes)
        {
            submesh.TintColor = tint;
            submesh.UsesClassicHairMaterial = game == BethesdaGame.Oblivion;
            submesh.OblivionHairLayerDiffusePath = null;
            submesh.OblivionHairLayerTexturePath = null;
            if (game == BethesdaGame.Oblivion)
            {
                _ = OblivionNpcFacePartMaterialResolver.Apply(
                    submesh,
                    textureResolver,
                    diffuseTexturePath,
                    diffuseTexturePath);
                OblivionHairLayerPolicy.Apply(submesh, textureResolver, diffuseTexturePath);
            }
            else if (diffuseTexturePath != null)
            {
                submesh.DiffuseTexturePath = diffuseTexturePath;
            }
        }
    }
}
