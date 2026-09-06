namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     Restores the implicit normal-map family used by classic Oblivion face parts and hair, including a tangent
///     frame when the source NIF does not carry one.
/// </summary>
internal static class OblivionNpcFacePartMaterialResolver
{
    /// <summary>
    ///     Applies the normal-map family and selects Oblivion's classic FaceGen skin shader.
    ///     A missing or unusable normal map disables only bump mapping; it does not change the
    ///     retail material family.
    /// </summary>
    internal static bool ApplyClassicSkin2000(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver,
        string? familyDiffusePath,
        string? effectiveDiffusePath)
    {
        var bumpReady = Apply(
            submesh,
            textureResolver,
            familyDiffusePath,
            effectiveDiffusePath);
        submesh.IsFaceGen = true;
        return bumpReady;
    }

    /// <summary>
    ///     Applies a resolved diffuse override and its existing <c>_n.dds</c> sibling. Returns <see langword="true" />
    ///     only when the submesh has every input required by the native bump-mapping route.
    /// </summary>
    internal static bool Apply(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver,
        string? familyDiffusePath,
        string? effectiveDiffusePath)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        ArgumentNullException.ThrowIfNull(textureResolver);

        var originalDiffusePath = submesh.DiffuseTexturePath;
        var normalMapPath = ResolveExistingTexturePath(
            textureResolver,
            FaceGenHeadShaderFamilyResolver.BuildSiblingPath(familyDiffusePath, "_n"),
            FaceGenHeadShaderFamilyResolver.BuildSiblingPath(originalDiffusePath, "_n"),
            submesh.NormalMapTexturePath,
            submesh.ShaderMetadata?.NormalMapPath);

        if (!string.IsNullOrWhiteSpace(effectiveDiffusePath))
        {
            submesh.DiffuseTexturePath = effectiveDiffusePath;
        }

        submesh.NormalMapTexturePath = normalMapPath;
        if (normalMapPath != null && NpcTangentSpaceBuilder.TryRebuild(submesh))
        {
            return true;
        }

        // A normal texture without a finite tangent frame is not a usable material input. Clear
        // the whole bump route so CPU, native, and glTF consumers all fail closed in the same way.
        submesh.NormalMapTexturePath = null;
        submesh.Tangents = null;
        submesh.Bitangents = null;
        return false;
    }

    private static string? ResolveExistingTexturePath(
        NifTextureResolver textureResolver,
        params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && textureResolver.GetTexture(candidate) != null)
            {
                return candidate;
            }
        }

        return null;
    }
}
