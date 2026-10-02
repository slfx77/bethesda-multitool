using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

internal enum NpcBodyTexturePart
{
    UpperBody,
    LowerBody,
    Hands,
    Feet,
    Tail
}

/// <summary>
///     Composes TES4 body atlases without changing head or later-game FaceGen policy.
///     Installed PC Oblivion 0052D2C0 selects sex-specific UpperBody models and the
///     shared Body model for the remaining body parts; 00557E60 caps its default body input at 30.
/// </summary>
internal static class OblivionNpcBodyTextureComposer
{
    private const int BodyMorphLimit = 30;
    private const string ModelDirectory = @"meshes\characters\_male\";
    private static readonly Logger Log = Logger.Instance;

    internal static string ResolveEgtPath(bool isFemale, NpcBodyTexturePart part)
    {
        return part switch
        {
            NpcBodyTexturePart.UpperBody => ModelDirectory +
                                            (isFemale ? "upperbodyhumanfemale.egt" : "upperbodyhumanmale.egt"),
            NpcBodyTexturePart.LowerBody or NpcBodyTexturePart.Hands or
                NpcBodyTexturePart.Feet or NpcBodyTexturePart.Tail => ModelDirectory + "body.egt",
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unsupported body atlas.")
        };
    }

    internal static NpcBodyTextureSet Compose(
        NpcAppearance appearance,
        MeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(meshArchives);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(egtCache);

        return Compose(appearance, textureResolver, LoadEgt);

        EgtParser? LoadEgt(string path)
        {
            if (!egtCache.TryGetValue(path, out var egt))
            {
                egt = NpcMeshHelpers.LoadEgtFromBsa(path, meshArchives);
                egtCache[path] = egt;
            }

            return egt;
        }
    }

    internal static NpcBodyTextureSet Compose(
        NpcAppearance appearance,
        NifTextureResolver textureResolver,
        Func<string, EgtParser?> loadEgt)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(loadEgt);

        var textures = NpcBodyTextureSet.FromAppearance(appearance);
        if (appearance.Game != BethesdaGame.Oblivion || appearance.FaceGenTextureCoeffs == null)
        {
            return textures;
        }

        // Retain the complete shared appearance coefficients for heads and other consumers.
        // The default TES4 body call passes a zero limit, meaning min(authored count, 30).
        var coefficients = appearance.FaceGenTextureCoeffs.AsSpan(
            0, Math.Min(BodyMorphLimit, appearance.FaceGenTextureCoeffs.Length)).ToArray();
        return new NpcBodyTextureSet(
            ComposeAtlas(textures.UpperBody, NpcBodyTexturePart.UpperBody, "upperbody"),
            ComposeAtlas(textures.LowerBody, NpcBodyTexturePart.LowerBody, "lowerbody"),
            ComposeAtlas(textures.Hands, NpcBodyTexturePart.Hands, "hands"),
            ComposeAtlas(textures.Feet, NpcBodyTexturePart.Feet, "feet"),
            ComposeAtlas(textures.Tail, NpcBodyTexturePart.Tail, "tail"));

        string? ComposeAtlas(string? baseTexture, NpcBodyTexturePart part, string label)
        {
            if (string.IsNullOrWhiteSpace(baseTexture))
            {
                return baseTexture;
            }

            // The Body delta may be shared in retail, but a precomposed diffuse must retain
            // this atlas's base pixels/UVs and a distinct resolver key. Failed assets keep it intact.
            var egtPath = ResolveEgtPath(appearance.IsFemale, part);
            var egt = loadEgt(egtPath);
            if (egt == null)
            {
                return baseTexture;
            }

            var decodedBase = textureResolver.GetTexture(baseTexture);
            if (decodedBase == null)
            {
                Log.Warn("Base body texture not found for EGT morph: {0}", baseTexture);
                return baseTexture;
            }

            var composed = FaceGenTextureMorpher.Apply(decodedBase, egt, coefficients);
            if (composed == null)
            {
                return baseTexture;
            }

            var key = NpcTextureHelpers.BuildNpcBodyEgtTextureKey(
                appearance.NpcFormId, label, appearance.RenderVariantLabel);
            textureResolver.InjectTexture(key, composed, new("FaceGen EGT", [baseTexture, egtPath], true, [new(baseTexture, [.. decodedBase.AssetReadReceipts])]));
            Log.Debug("Body EGT morph applied: NPC 0x{0:X8} {1} -> {2}", appearance.NpcFormId, label, egtPath);
            return key;
        }
    }
}
