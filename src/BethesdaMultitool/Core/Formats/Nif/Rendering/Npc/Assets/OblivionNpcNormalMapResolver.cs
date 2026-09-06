using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     Restores an existing classic equipment or combined-hand normal before race/EGT diffuse substitution.
///     Callers keep the authored shader family and tangent frame; this never selects FaceGen,
///     generates tangents, or derives a normal from an already substituted race texture.
/// </summary>
internal static class OblivionNpcNormalMapResolver
{
    internal static void ApplyMissingCombinedHandNormalMap(
        RenderableSubmesh submesh,
        NpcAppearance appearance,
        string bodyMeshPath,
        NifTextureResolver textureResolver)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(bodyMeshPath);

        // HandNifPath is the distinct combined TES4 hand-part identity. Do not broaden this to
        // all body meshes or infer skin from a shape name: head/ear/hair and split later-game
        // hands have different material rules. Both assembly routes call before diffuse override.
        if (appearance.Game != BethesdaGame.Oblivion ||
            string.IsNullOrWhiteSpace(appearance.HandNifPath) ||
            !string.Equals(NormalizeMeshPath(bodyMeshPath), NormalizeMeshPath(appearance.HandNifPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyMissingNormalMap(submesh, appearance.Game, textureResolver);
    }

    private static string NormalizeMeshPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
            ? normalized[7..]
            : normalized;
    }

    internal static void ApplyMissingNormalMap(
        RenderableSubmesh submesh,
        BethesdaGame game,
        NifTextureResolver textureResolver)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        ArgumentNullException.ThrowIfNull(textureResolver);

        if (game != BethesdaGame.Oblivion || !string.IsNullOrWhiteSpace(submesh.NormalMapTexturePath))
        {
            return;
        }

        // An explicit authored binding wins even when unavailable in this resolver. Do not
        // silently replace a missing authored asset with a different guessed material family.
        var authoredNormalPath = submesh.ShaderMetadata?.NormalMapPath;
        if (!string.IsNullOrWhiteSpace(authoredNormalPath))
        {
            submesh.NormalMapTexturePath = authoredNormalPath;
            return;
        }

        var normalPath = FaceGenHeadShaderFamilyResolver.BuildSiblingPath(submesh.DiffuseTexturePath, "_n");
        if (normalPath == null)
        {
            return;
        }

        var texture = textureResolver.GetTexture(normalPath);
        if (texture is not { MipLevels.Count: > 0 } || texture.Width <= 0 || texture.Height <= 0 ||
            texture.Pixels.Length % 4 != 0 || (long)texture.Width * texture.Height != texture.Pixels.Length / 4)
        {
            return;
        }

        submesh.NormalMapTexturePath = normalPath;
    }
}
