using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>
///     Selects and composites the texture delta used for an NPC head. Stock Oblivion actors prefer
///     their shipped FaceGenMap0; EGT regeneration remains the fallback and explicit comparison path.
/// </summary>
internal static class NpcHeadTextureComposer
{
    internal static NpcHeadTextureResolution Resolve(
        NpcAppearance npc,
        NifTextureResolver textureResolver,
        string? baseTexturePath,
        bool applyEgt,
        Func<EgtParser?> loadGeneratedEgt, string? egtPath = null)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(loadGeneratedEgt);

        if (string.IsNullOrWhiteSpace(baseTexturePath))
        {
            return new NpcHeadTextureResolution(baseTexturePath, NpcHeadTextureSource.BaseDiffuse);
        }

        var baseTexture = textureResolver.GetTexture(baseTexturePath);
        if (baseTexture == null)
        {
            return new NpcHeadTextureResolution(baseTexturePath, NpcHeadTextureSource.BaseDiffuse);
        }

        if (!applyEgt)
        {
            return ResolveBaseDiffuse(npc, textureResolver, baseTexturePath, baseTexture);
        }

        // A comparison variant explicitly asks the renderer to rebake from its supplied FGTS array.
        // The ordinary stock-NPC path instead consumes the exact authored retail delta.
        if (string.IsNullOrWhiteSpace(npc.RenderVariantLabel) &&
            !string.IsNullOrWhiteSpace(npc.AuthoredFaceGenMap0Path))
        {
            var authoredDelta = textureResolver.GetTexture(npc.AuthoredFaceGenMap0Path);
            if (authoredDelta != null)
            {
                var authoredComposite = FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture, authoredDelta);
                if (authoredComposite != null)
                {
                    return Inject(
                        npc,
                        textureResolver,
                        authoredComposite,
                        NpcHeadTextureSource.AuthoredMap0,
                        ClassicSkinAuthoredAlbedo.Create(npc.Game, baseTexturePath, npc.AuthoredFaceGenMap0Path),
                        new("Authored FaceGen Map0", [baseTexturePath, npc.AuthoredFaceGenMap0Path], ObservedInputs: [new(baseTexturePath, [.. baseTexture.AssetReadReceipts]), new(npc.AuthoredFaceGenMap0Path, [.. authoredDelta.AssetReadReceipts])]));
                }
            }
        }

        if (npc.FaceGenTextureCoeffs == null)
        {
            return ResolveBaseDiffuse(npc, textureResolver, baseTexturePath, baseTexture);
        }

        var egt = loadGeneratedEgt();
        if (egt == null)
        {
            return ResolveBaseDiffuse(npc, textureResolver, baseTexturePath, baseTexture);
        }

        var generatedComposite = FaceGenTextureMorpher.Apply(baseTexture, egt, npc.FaceGenTextureCoeffs);
        return generatedComposite == null
            ? ResolveBaseDiffuse(npc, textureResolver, baseTexturePath, baseTexture)
            : Inject(npc, textureResolver, generatedComposite, NpcHeadTextureSource.GeneratedEgt, dependency:
                new("FaceGen EGT", egtPath is null ? [baseTexturePath] : [baseTexturePath, egtPath], true, [new(baseTexturePath, [.. baseTexture.AssetReadReceipts])]));
    }

    private static NpcHeadTextureResolution ResolveBaseDiffuse(
        NpcAppearance npc,
        NifTextureResolver textureResolver,
        string baseTexturePath,
        DecodedTexture baseTexture)
    {
        // --no-egt removes Map0/EGT only. Oblivion still binds the source-proven Map1 fallback,
        // so the control retains the same SKIN2000 texture family and lighting permutation.
        return npc.Game == BethesdaGame.Oblivion
            ? Inject(npc, textureResolver, baseTexture, NpcHeadTextureSource.BaseDiffuse, dependency:
                new("Base diffuse", [baseTexturePath], ObservedInputs: [new(baseTexturePath, [.. baseTexture.AssetReadReceipts])]))
            : new NpcHeadTextureResolution(baseTexturePath, NpcHeadTextureSource.BaseDiffuse);
    }

    private static NpcHeadTextureResolution Inject(
        NpcAppearance npc,
        NifTextureResolver textureResolver,
        DecodedTexture texture,
        NpcHeadTextureSource source,
        ClassicSkinAuthoredAlbedo? authoredAlbedo = null,
        BethesdaMultitool.Core.Assets.GeneratedAssetDependency? dependency = null)
    {
        // SKIN2000 samples BaseMap, FaceGenMap0, and FaceGenMap1 separately. The current scene
        // contract carries one diffuse texture, so preserve the exact texel-center algebra in the
        // generated albedo: (Base + 2 * (Map0 - 0.5)) * (4 * Map1). Map0 was applied by the caller;
        // this is the missing final term. Other games do not own Oblivion's fallback Map1.
        var map1Source = NpcFaceGenMap1Source.None;
        if (npc.Game == BethesdaGame.Oblivion)
        {
            texture = FaceGenHeadShaderFamilyResolver.ApplyDefaultDetailModulation(texture);
            map1Source = NpcFaceGenMap1Source.DefaultDetailModFaceGenTexture;
            if (dependency is not null)
                dependency = dependency with { Recipe = dependency.Recipe + " + Oblivion default FaceGenMap1 modulation" };
        }

        var generatedTextureKey = NpcTextureHelpers.BuildNpcFaceEgtTextureKey(npc);
        textureResolver.InjectTexture(generatedTextureKey, texture, dependency);
        return new NpcHeadTextureResolution(
            generatedTextureKey,
            source,
            map1Source,
            map1Source == NpcFaceGenMap1Source.None ? null : generatedTextureKey,
            authoredAlbedo);
    }
}

/// <summary>The effective diffuse path and provenance selected for an NPC head.</summary>
internal readonly record struct NpcHeadTextureResolution(
    string? EffectiveTexturePath,
    NpcHeadTextureSource Source,
    NpcFaceGenMap1Source FaceGenMap1Source = NpcFaceGenMap1Source.None,
    string? FaceGenMap1EffectivePath = null,
    ClassicSkinAuthoredAlbedo? AuthoredAlbedo = null);
