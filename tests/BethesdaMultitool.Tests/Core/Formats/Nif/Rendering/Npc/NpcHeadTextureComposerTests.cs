using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcHeadTextureComposerTests
{
    private const string BaseTexturePath = @"textures\characters\imperial\headhuman.dds";
    private const string AuthoredMap0Path = @"textures\faces\oblivion.esm\000222A8_0.dds";

    [Fact]
    public void Resolve_PrefersAuthoredMap0WithoutRequiringFgtsOrLoadingEgt()
    {
        var textures = CreateBaseAndAuthoredTextures();
        using var resolver = CreateResolver(textures);
        var egtRequested = false;
        var npc = CreateNpc(faceGenTextureCoeffs: null);

        var result = NpcHeadTextureComposer.Resolve(
            npc,
            resolver,
            BaseTexturePath,
            true,
            () =>
            {
                egtRequested = true;
                return CreateEgt();
            });

        Assert.Equal(NpcHeadTextureSource.AuthoredMap0, result.Source);
        Assert.Equal(
            NpcFaceGenMap1Source.DefaultDetailModFaceGenTexture,
            result.FaceGenMap1Source);
        Assert.False(egtRequested);
        Assert.Equal(@"facegen_egt\000222A8.dds", result.EffectiveTexturePath);
        Assert.Equal(result.EffectiveTexturePath, result.FaceGenMap1EffectivePath);
        var composite = Assert.IsType<DecodedTexture>(resolver.GetTexture(result.EffectiveTexturePath!));
        // (Base + 2 * (Map0 - 0.5)) * (4 * DefaultFaceGenMap1).
        Assert.Equal<byte>([118, 103, 118, 255], composite.Pixels);
    }

    [Fact]
    public void Resolve_MissingAuthoredMap0FallsBackToGeneratedEgt()
    {
        var textures = new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase)
        {
            [BaseTexturePath] = TestTextures.Uniform(1, 1, 100, 110, 120)
        };
        using var resolver = CreateResolver(textures);
        var egtRequested = false;
        var npc = CreateNpc([1f / 256f]);

        var result = NpcHeadTextureComposer.Resolve(
            npc,
            resolver,
            BaseTexturePath,
            true,
            () =>
            {
                egtRequested = true;
                return CreateEgt();
            });

        Assert.Equal(NpcHeadTextureSource.GeneratedEgt, result.Source);
        Assert.True(egtRequested);
        Assert.Equal(@"facegen_egt\000222A8.dds", result.EffectiveTexturePath);
        Assert.NotNull(resolver.GetTexture(result.EffectiveTexturePath!));
    }

    [Fact]
    public void Resolve_NoEgtOmitsMap0ButRetainsRetailMap1Fallback()
    {
        var textures = CreateBaseAndAuthoredTextures();
        using var resolver = CreateResolver(textures);
        var npc = CreateNpc([1f]);

        var result = NpcHeadTextureComposer.Resolve(
            npc,
            resolver,
            BaseTexturePath,
            false,
            () => throw new InvalidOperationException("No-EGT must not request an EGT."));

        Assert.Equal(NpcHeadTextureSource.BaseDiffuse, result.Source);
        Assert.Equal(
            NpcFaceGenMap1Source.DefaultDetailModFaceGenTexture,
            result.FaceGenMap1Source);
        Assert.Equal(@"facegen_egt\000222A8.dds", result.EffectiveTexturePath);
        Assert.Equal(result.EffectiveTexturePath, result.FaceGenMap1EffectivePath);
        var composite = Assert.IsType<DecodedTexture>(resolver.GetTexture(result.EffectiveTexturePath!));
        Assert.Equal<byte>([97, 112, 117, 255], composite.Pixels);
    }

    [Fact]
    public void Resolve_ExplicitCoefficientComparisonVariantUsesGeneratedEgt()
    {
        var textures = CreateBaseAndAuthoredTextures();
        using var resolver = CreateResolver(textures);
        var egtRequested = false;
        var npc = CreateNpc([1f / 256f], "npc_plus_race");

        var result = NpcHeadTextureComposer.Resolve(
            npc,
            resolver,
            BaseTexturePath,
            true,
            () =>
            {
                egtRequested = true;
                return CreateEgt();
            });

        Assert.Equal(NpcHeadTextureSource.GeneratedEgt, result.Source);
        Assert.True(egtRequested);
        Assert.Equal(@"facegen_egt\000222A8_npc_plus_race.dds", result.EffectiveTexturePath);
    }

    private static NpcAppearance CreateNpc(
        float[]? faceGenTextureCoeffs,
        string? renderVariantLabel = null)
    {
        return new NpcAppearance
        {
            Game = BethesdaGame.Oblivion,
            NpcFormId = 0x000222A8,
            RenderVariantLabel = renderVariantLabel,
            AuthoredFaceGenMap0Path = AuthoredMap0Path,
            FaceGenTextureCoeffs = faceGenTextureCoeffs
        };
    }

    private static Dictionary<string, DecodedTexture> CreateBaseAndAuthoredTextures()
    {
        return new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase)
        {
            [BaseTexturePath] = TestTextures.Uniform(1, 1, 100, 110, 120),
            [AuthoredMap0Path] = TestTextures.Uniform(1, 1, 138, 123, 128)
        };
    }

    private static NifTextureResolver CreateResolver(
        Dictionary<string, DecodedTexture> textures)
    {
        return new NifTextureResolver(path => textures.TryGetValue(path, out var texture) ? texture : null);
    }

    private static EgtParser CreateEgt()
    {
        return EgtParser.CreateFromMorphs(
            1,
            1,
            [
                new EgtMorph
                {
                    Scale = 1f,
                    DeltaR = [127],
                    DeltaG = [0],
                    DeltaB = [0]
                }
            ]);
    }
}
