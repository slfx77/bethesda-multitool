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
            if (game == BethesdaGame.Oblivion)
            {
                _ = OblivionNpcFacePartMaterialResolver.Apply(
                    submesh,
                    textureResolver,
                    diffuseTexturePath,
                    diffuseTexturePath);
            }
            else if (diffuseTexturePath != null)
            {
                submesh.DiffuseTexturePath = diffuseTexturePath;
            }
        }
    }
}
