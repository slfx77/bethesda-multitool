using System.Numerics;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     Selects the supported opaque TES4 body-skin permutation during actor composition only.
///     PC 0047AC20 binds the race atlas/normal and FaceGen maps after normal-bearing model
///     preparation; the followed no-glow passes 79/7F bind SKIN2000. Unknown source variants keep
///     their existing material. This does not classify raw meshes, heads, hair, or footwear by name.
/// </summary>
internal static class OblivionNpcBodySkinMaterialResolver
{
    internal static void ApplyTextureOverride(
        RenderableSubmesh submesh,
        NpcAppearance appearance,
        NifTextureResolver textureResolver,
        string textureOverride)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(textureOverride);

        var originalDiffuse = submesh.AuthoredOblivionBodySkinDiffusePath;
        // Preserve the established atlas override even when the shader specialization is unavailable.
        submesh.DiffuseTexturePath = textureOverride;
        if (appearance.Game != BethesdaGame.Oblivion ||
            string.IsNullOrWhiteSpace(textureOverride) ||
            NifTexturePathUtility.Normalize(textureOverride).StartsWith("textures\\body_skin\\", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(originalDiffuse) ||
            !submesh.HasAuthoredOblivionBodySkinInputs ||
            HasUnsupportedMaterial(submesh) || !HasUsableBasis(submesh) ||
            OblivionNpcBodyMaterialPolicy.ResolveTexturePart(submesh) is not { } part)
        {
            return;
        }

        // Preparation requires the original material's normal before the biped binder can replace
        // it with an available race-family normal. Missing race normals retain the original one.
        var preparedNormal = FaceGenHeadShaderFamilyResolver.BuildSiblingPath(originalDiffuse, "_n");
        if (!HasTexture(textureResolver, preparedNormal) ||
            HasTexture(textureResolver, FaceGenHeadShaderFamilyResolver.BuildSiblingPath(originalDiffuse, "_g")))
        {
            return;
        }

        var diffuse = textureResolver.GetTexture(textureOverride);
        if (!IsUsableTexture(diffuse))
        {
            return;
        }

        var raceDiffuse = OblivionNpcBodyMaterialPolicy.ResolveTextureOverride(
            submesh, NpcBodyTextureSet.FromAppearance(appearance));
        var raceNormal = FaceGenHeadShaderFamilyResolver.BuildSiblingPath(raceDiffuse, "_n");
        var finalNormal = HasTexture(textureResolver, raceNormal) ? raceNormal : preparedNormal;
        var key = NpcTextureHelpers.BuildNpcBodySkinTextureKey(appearance, part);

        // Always compose from the caller's unmodified atlas, never from a prior body_skin result.
        // Shared shapes and repeated compositions therefore cannot compound the 256/255 gain.
        // A distinct actor/part/variant key leaves generic consumers of body_egt untouched and is
        // included in the common generated-texture capture/eviction list.
        textureResolver.InjectTexture(key, FaceGenHeadShaderFamilyResolver.ApplyDefaultDetailModulation(diffuse!));
        submesh.DiffuseTexturePath = key;
        submesh.NormalMapTexturePath = finalNormal;
        submesh.IsFaceGen = true;
    }

    private static bool HasUnsupportedMaterial(RenderableSubmesh submesh)
    {
        return submesh.ShaderMetadata != null || submesh.HasAlphaBlend || submesh.HasAlphaTest ||
               !submesh.MaterialAlpha.Equals(1f) || submesh.MaterialAlphaController != null ||
               submesh.IsEmissive || submesh.AnimatedEmissiveColor != null ||
               submesh.UsesClassicHairMaterial || submesh.IsEyeEnvmap || HasUnsupportedTextures(submesh);
    }

    private static bool HasUnsupportedTextures(RenderableSubmesh submesh)
    {
        return submesh.EnvironmentMapTexturePath != null || submesh.ClassicEnvironmentMapTexturePath != null ||
               submesh.ClassicParallaxHeightMapTexturePath != null || submesh.Lighting30GlowMapTexturePath != null ||
               submesh.BgsmGlowMapTexturePath != null || submesh.SpecularMapTexturePath != null ||
               submesh.GradientMapTexturePath != null;
    }

    private static bool HasUsableBasis(RenderableSubmesh submesh)
    {
        var count = submesh.VertexCount;
        return count > 0 && submesh.Positions.Length == count * 3 &&
               submesh.Positions.All(float.IsFinite) &&
               submesh.UVs is { } uvs && uvs.Length == count * 2 && uvs.All(float.IsFinite) &&
               HasUsableVectors(submesh.Normals, count) &&
               HasUsableVectors(submesh.Tangents, count) && HasUsableVectors(submesh.Bitangents, count);
    }

    private static bool HasUsableVectors(float[]? values, int count)
    {
        if (values == null || values.Length != count * 3)
        {
            return false;
        }

        for (var offset = 0; offset < values.Length; offset += 3)
        {
            var length = new Vector3(values[offset], values[offset + 1], values[offset + 2]).LengthSquared();
            if (!float.IsFinite(length) || length <= 0.000001f)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasTexture(NifTextureResolver resolver, string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && IsUsableTexture(resolver.GetTexture(path));
    }

    private static bool IsUsableTexture(DecodedTexture? texture)
    {
        return texture is { MipLevels.Count: > 0, Width: > 0, Height: > 0 } &&
               (long)texture.Width * texture.Height * 4 == texture.Pixels.Length;
    }
}
