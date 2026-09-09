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
        var npc = CreateNpc(null);

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
        Assert.Equal<byte>([121, 101, 121, 255], composite.Pixels);
        Assert.Equal<byte>([100, 110, 120, 255], textures[BaseTexturePath].Pixels);
        Assert.Equal<byte>([138, 123, 128, 255], textures[AuthoredMap0Path].Pixels);
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    public void Resolve_AuthoredMap0PreservesFractionalSampling(BethesdaGame game)
    {
        var textures = new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase)
        {
            [BaseTexturePath] = TestTextures.Single(100, 110, 120, 37),
            [AuthoredMap0Path] = TestTextures.FromTexels(2, 1,
                (127, 127, 0, 0), (128, 129, 255, 255))
        };
        using var resolver = CreateResolver(textures);
        var npc = CreateNpc(null, game: game);

        var result = NpcHeadTextureComposer.Resolve(
            npc, resolver, BaseTexturePath, true,
            () => throw new InvalidOperationException("The authored map must not request EGT."));

        Assert.Equal(NpcHeadTextureSource.AuthoredMap0, result.Source);
        var composite = Assert.IsType<DecodedTexture>(resolver.GetTexture(result.EffectiveTexturePath!));
        // Filtered Map0 gives deltas 0, 1, 0. Oblivion's existing 256/255 Map1 gain does not
        // change these low-channel byte results; all callers preserve the same base alpha.
        Assert.Equal<byte>([100, 111, 120, 37], composite.Pixels);
        Assert.Equal<byte>([100, 110, 120, 37], textures[BaseTexturePath].Pixels);
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
        textures[BaseTexturePath] = TestTextures.Single(128, 192, 254, 137);
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
        Assert.Equal<byte>([129, 193, 255, 137], composite.Pixels);
        var repeated = NpcHeadTextureComposer.Resolve(
            npc,
            resolver,
            BaseTexturePath,
            false,
            () => throw new InvalidOperationException("No-EGT must not request an EGT."));
        Assert.Equal(result, repeated);
        Assert.Equal<byte>([129, 193, 255, 137], resolver.GetTexture(repeated.EffectiveTexturePath!)!.Pixels);
        Assert.Equal<byte>([128, 192, 254, 137], textures[BaseTexturePath].Pixels);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3, false)]
    [InlineData(BethesdaGame.FalloutNewVegas, false)]
    [InlineData(BethesdaGame.Fallout3, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, true)]
    public void Resolve_FalloutDoesNotAcquireOblivionDefaultDetail(BethesdaGame game, bool applyEgt)
    {
        var textures = CreateBaseAndAuthoredTextures();
        textures[BaseTexturePath] = TestTextures.Single(128, 192, 254, 23);
        textures[AuthoredMap0Path] = TestTextures.Single(128, 128, 128, 255);
        using var resolver = CreateResolver(textures);
        var npc = CreateNpc(null, game: game);

        var result = NpcHeadTextureComposer.Resolve(
            npc, resolver, BaseTexturePath, applyEgt,
            () => throw new InvalidOperationException("The supplied authored map or no-EGT route must not load EGT."));

        Assert.Equal(NpcFaceGenMap1Source.None, result.FaceGenMap1Source);
        Assert.Null(result.FaceGenMap1EffectivePath);
        Assert.Equal(applyEgt ? NpcHeadTextureSource.AuthoredMap0 : NpcHeadTextureSource.BaseDiffuse, result.Source);
        var composite = Assert.IsType<DecodedTexture>(resolver.GetTexture(result.EffectiveTexturePath!));
        byte[] expected = applyEgt ? [129, 193, 255, 23] : [128, 192, 254, 23];
        Assert.Equal(expected, composite.Pixels);
        Assert.Equal<byte>([128, 192, 254, 23], textures[BaseTexturePath].Pixels);
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
        string? renderVariantLabel = null,
        BethesdaGame game = BethesdaGame.Oblivion)
    {
        return new NpcAppearance
        {
            Game = game,
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