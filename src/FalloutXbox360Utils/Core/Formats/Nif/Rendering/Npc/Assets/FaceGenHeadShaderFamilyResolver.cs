using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

internal static class FaceGenHeadShaderFamilyResolver
{
    private static readonly DecodedTexture DefaultFaceGenMap1Texture = CreateDefaultFaceGenMap1Texture();

    private static readonly (float R, float G, float B) DefaultSubsurfaceColor =
        (24f / 255f, 8f / 255f, 8f / 255f);

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
                var (mr, mg, mb, _) = NifScanlineRasterizer.SampleTexture(detailModulationTexture, u, v);

                outputPixels[pixelIndex] = ApplyModulation(sourcePixels[pixelIndex], mr);
                outputPixels[pixelIndex + 1] = ApplyModulation(sourcePixels[pixelIndex + 1], mg);
                outputPixels[pixelIndex + 2] = ApplyModulation(sourcePixels[pixelIndex + 2], mb);
                outputPixels[pixelIndex + 3] = sourcePixels[pixelIndex + 3];
            }
        }

        return DecodedTexture.FromBaseLevel(outputPixels, width, height);
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

    private static DecodedTexture CreateDefaultFaceGenMap1Texture()
    {
        var pixels = new byte[32 * 32 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 62;
            pixels[index + 1] = 65;
            pixels[index + 2] = 62;
            pixels[index + 3] = 64;
        }

        return DecodedTexture.FromBaseLevel(pixels, 32, 32);
    }
}
