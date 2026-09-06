using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     Resolves the FaceGen head shader's texture family (diffuse, normal, subsurface) and subsurface tint for an NPC
///     head.
/// </summary>
internal static class FaceGenHeadShaderFamilyResolver
{
    // Installed PC Oblivion 00553140 fills every RGB channel of the 32x32 manager+DB8
    // default detail texture with 0x40; 004783A0 returns it to the head/body binders.
    private const byte OblivionDefaultFaceGenMap1Channel = 0x40;

    // Preserve the existing generic-family fallback from the retained Fallout Xenon oracle.
    // It is not the PC Oblivion default consumed by the game-gated head/ear composers below.
    private static readonly DecodedTexture DefaultFaceGenMap1Texture =
        CreateSolidFaceGenMap1Texture(0x3E, 0x41, 0x3E, 0x40);

    private static readonly (float R, float G, float B) DefaultSubsurfaceColor =
        (24f / 255f, 8f / 255f, 8f / 255f);

    /// <summary>
    ///     Applies the texture family used by Oblivion's SKIN2000 permutation without changing
    ///     the already-precomposed BaseMap/Map0/Map1 albedo. The original family diffuse remains
    ///     the authoritative source for the sibling normal map even when the effective diffuse is
    ///     a generated texture key.
    /// </summary>
    internal static void ApplyClassicSkin2000Material(
        IEnumerable<RenderableSubmesh> submeshes,
        NifTextureResolver textureResolver,
        string? familySourceDiffusePath,
        string? effectiveDiffusePath)
    {
        ArgumentNullException.ThrowIfNull(submeshes);
        ArgumentNullException.ThrowIfNull(textureResolver);

        foreach (var submesh in submeshes)
        {
            _ = OblivionNpcFacePartMaterialResolver.ApplyClassicSkin2000(
                submesh,
                textureResolver,
                familySourceDiffusePath,
                effectiveDiffusePath);
        }
    }

    internal static string ApplyToSubmeshes(
        IEnumerable<RenderableSubmesh> submeshes,
        NifTextureResolver textureResolver,
        string familySourceDiffusePath,
        string effectiveDiffusePath,
        string generatedDiffuseTextureKey)
    {
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(familySourceDiffusePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveDiffusePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDiffuseTextureKey);

        var submeshList = submeshes as IList<RenderableSubmesh> ?? submeshes.ToList();
        if (submeshList.Count == 0)
        {
            return effectiveDiffusePath;
        }

        var finalDiffusePath = ComposeAndInjectDiffuseTexture(
            textureResolver,
            effectiveDiffusePath,
            generatedDiffuseTextureKey);

        foreach (var submesh in submeshList)
        {
            var family = ResolveSubmeshFamily(submesh, textureResolver, familySourceDiffusePath, finalDiffusePath);
            submesh.DiffuseTexturePath = family.DiffuseTexturePath;
            submesh.NormalMapTexturePath = family.NormalMapTexturePath;
            submesh.IsFaceGen = true;
            submesh.SubsurfaceColor = family.SubsurfaceColor;
        }

        return finalDiffusePath;
    }

    internal static string? BuildSiblingPath(string? diffuseTexturePath, string suffix)
    {
        if (string.IsNullOrWhiteSpace(diffuseTexturePath) ||
            string.IsNullOrWhiteSpace(suffix))
        {
            return null;
        }

        var extensionIndex = diffuseTexturePath.LastIndexOf('.');
        if (extensionIndex <= 0)
        {
            return null;
        }

        return diffuseTexturePath[..extensionIndex] + suffix + ".dds";
    }

    internal static DecodedTexture ApplyDetailModulation(
        DecodedTexture diffuseTexture,
        DecodedTexture detailModulationTexture)
    {
        ArgumentNullException.ThrowIfNull(diffuseTexture);
        ArgumentNullException.ThrowIfNull(detailModulationTexture);

        var sourcePixels = diffuseTexture.Pixels;
        var outputPixels = new byte[sourcePixels.Length];
        var width = diffuseTexture.Width;
        var height = diffuseTexture.Height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = (y * width + x) * 4;
                var u = (x + 0.5f) / width;
                var v = (y + 0.5f) / height;
                var (mr, mg, mb, _) = NifTextureSampler.SampleTexture(detailModulationTexture, u, v);

                outputPixels[pixelIndex] = ApplyModulation(sourcePixels[pixelIndex], mr);
                outputPixels[pixelIndex + 1] = ApplyModulation(sourcePixels[pixelIndex + 1], mg);
                outputPixels[pixelIndex + 2] = ApplyModulation(sourcePixels[pixelIndex + 2], mb);
                outputPixels[pixelIndex + 3] = sourcePixels[pixelIndex + 3];
            }
        }

        return DecodedTexture.FromBaseLevel(outputPixels, width, height);
    }

    /// <summary>
    ///     Applies Oblivion's retail fallback <c>FaceGenMap1</c>. The shader multiplies the
    ///     BaseMap/Map0 aggregate by four times this texture; keeping that operation beside the
    ///     source-proven fallback payload prevents production composition from silently omitting
    ///     the final SKIN2000 albedo term.
    /// </summary>
    internal static DecodedTexture ApplyDefaultDetailModulation(DecodedTexture diffuseTexture)
    {
        ArgumentNullException.ThrowIfNull(diffuseTexture);

        var sourcePixels = diffuseTexture.Pixels;
        var outputPixels = new byte[sourcePixels.Length];
        for (var index = 0; index < sourcePixels.Length; index += 4)
        {
            // A constant tile needs no filtering. Sampling it through the byte-valued CPU
            // bilinear path can truncate 63.999996 to 63 for non-power-of-two inputs.
            outputPixels[index] = ApplyModulation(sourcePixels[index], OblivionDefaultFaceGenMap1Channel);
            outputPixels[index + 1] = ApplyModulation(sourcePixels[index + 1], OblivionDefaultFaceGenMap1Channel);
            outputPixels[index + 2] = ApplyModulation(sourcePixels[index + 2], OblivionDefaultFaceGenMap1Channel);
            outputPixels[index + 3] = sourcePixels[index + 3];
        }

        return DecodedTexture.FromBaseLevel(outputPixels, diffuseTexture.Width, diffuseTexture.Height);
    }

    private static FaceGenHeadShaderFamilyResult ResolveSubmeshFamily(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver,
        string familySourceDiffusePath,
        string finalDiffusePath)
    {
        var shaderMetadata = submesh.ShaderMetadata;
        var normalMapPath = ResolveExistingTexturePath(
            textureResolver,
            BuildSiblingPath(familySourceDiffusePath, "_n"),
            submesh.NormalMapTexturePath,
            shaderMetadata?.NormalMapPath);
        var subsurfaceTexturePath = ResolveExistingTexturePath(
            textureResolver,
            BuildSiblingPath(familySourceDiffusePath, "_sk"),
            shaderMetadata?.GlowMapPath);
        var subsurfaceColor = ResolveSubsurfaceColor(textureResolver, subsurfaceTexturePath);

        return new FaceGenHeadShaderFamilyResult(
            finalDiffusePath,
            normalMapPath,
            subsurfaceTexturePath,
            subsurfaceColor);
    }

    private static string ComposeAndInjectDiffuseTexture(
        NifTextureResolver textureResolver,
        string effectiveDiffusePath,
        string generatedDiffuseTextureKey)
    {
        var effectiveDiffuseTexture = textureResolver.GetTexture(effectiveDiffusePath);
        if (effectiveDiffuseTexture == null)
        {
            return effectiveDiffusePath;
        }

        var composed = ApplyDetailModulation(effectiveDiffuseTexture, DefaultFaceGenMap1Texture);
        textureResolver.InjectTexture(generatedDiffuseTextureKey, composed);
        return generatedDiffuseTextureKey;
    }

    private static string? ResolveExistingTexturePath(
        NifTextureResolver textureResolver,
        params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (textureResolver.GetTexture(candidate) != null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static (float R, float G, float B) ResolveSubsurfaceColor(
        NifTextureResolver textureResolver,
        string? subsurfaceTexturePath)
    {
        var subsurfaceTexture = string.IsNullOrWhiteSpace(subsurfaceTexturePath)
            ? null
            : textureResolver.GetTexture(subsurfaceTexturePath);
        return subsurfaceTexture == null
            ? DefaultSubsurfaceColor
            : ComputeAverageVisibleRgb(subsurfaceTexture);
    }

    private static (float R, float G, float B) ComputeAverageVisibleRgb(DecodedTexture texture)
    {
        long weightedSumR = 0;
        long weightedSumG = 0;
        long weightedSumB = 0;
        long weightSum = 0;

        var pixels = texture.Pixels;
        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            if (alpha == 0)
            {
                continue;
            }

            weightedSumR += pixels[index] * alpha;
            weightedSumG += pixels[index + 1] * alpha;
            weightedSumB += pixels[index + 2] * alpha;
            weightSum += alpha;
        }

        if (weightSum <= 0)
        {
            return DefaultSubsurfaceColor;
        }

        return (
            weightedSumR / (255f * weightSum),
            weightedSumG / (255f * weightSum),
            weightedSumB / (255f * weightSum));
    }

    private static byte ApplyModulation(byte channelValue, byte modulationValue)
    {
        var scaled = channelValue * (modulationValue / 255f) * 4f;
        return (byte)Math.Clamp((int)MathF.Round(scaled), 0, 255);
    }

    private static DecodedTexture CreateSolidFaceGenMap1Texture(byte red, byte green, byte blue, byte alpha)
    {
        var pixels = new byte[32 * 32 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = red;
            pixels[index + 1] = green;
            pixels[index + 2] = blue;
            pixels[index + 3] = alpha;
        }

        return DecodedTexture.FromBaseLevel(pixels, 32, 32);
    }
}
