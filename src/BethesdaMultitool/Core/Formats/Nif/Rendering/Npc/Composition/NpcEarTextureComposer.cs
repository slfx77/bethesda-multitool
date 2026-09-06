using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>
///     Composes the diffuse texture used by a detached Oblivion ear mesh. Retail prepares the
///     head and ear face-part slots with the same skin shader family, so their generated albedos
///     must both include the fallback FaceGenMap1 term before entering the one-texture scene contract.
/// </summary>
internal static class NpcEarTextureComposer
{
    internal static NpcEarTextureResolution Resolve(
        NpcAppearance npc,
        NifTextureResolver textureResolver,
        string? baseTexturePath,
        bool applyEgt,
        Func<EgtParser?> loadEgt)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(loadEgt);

        if (npc.Game != BethesdaGame.Oblivion || string.IsNullOrWhiteSpace(baseTexturePath))
        {
            return new NpcEarTextureResolution(baseTexturePath, NpcHeadTextureSource.BaseDiffuse);
        }

        var baseTexture = textureResolver.GetTexture(baseTexturePath);
        if (baseTexture == null)
        {
            return new NpcEarTextureResolution(baseTexturePath, NpcHeadTextureSource.BaseDiffuse);
        }

        var texture = baseTexture;
        var source = NpcHeadTextureSource.BaseDiffuse;
        if (applyEgt && npc.FaceGenTextureCoeffs != null)
        {
            var egt = loadEgt();
            var morphed = egt == null
                ? null
                : FaceGenTextureMorpher.Apply(baseTexture, egt, npc.FaceGenTextureCoeffs);
            if (morphed != null)
            {
                texture = morphed;
                source = NpcHeadTextureSource.GeneratedEgt;
            }
        }

        // The source shader samples Map1 once after BaseMap + Map0. The scene format carries a
        // single diffuse, so bake that final multiplication once here, after the optional ear EGT.
        texture = FaceGenHeadShaderFamilyResolver.ApplyDefaultDetailModulation(texture);
        var generatedTextureKey = NpcTextureHelpers.BuildNpcEarEgtTextureKey(npc);
        textureResolver.InjectTexture(generatedTextureKey, texture);
        return new NpcEarTextureResolution(
            generatedTextureKey,
            source,
            NpcFaceGenMap1Source.DefaultDetailModFaceGenTexture,
            generatedTextureKey);
    }
}

/// <summary>The effective ear diffuse and its FaceGen texture provenance.</summary>
internal readonly record struct NpcEarTextureResolution(
    string? EffectiveTexturePath,
    NpcHeadTextureSource Source,
    NpcFaceGenMap1Source FaceGenMap1Source = NpcFaceGenMap1Source.None,
    string? FaceGenMap1EffectivePath = null);
