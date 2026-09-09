using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcEarTextureComposerTests
{
    private const string BaseTexturePath = @"textures\characters\imperial\earshuman.dds";

    [Fact]
    public void Resolve_NoEgtBakesDefaultMap1ExactlyOnce()
    {
        var baseTexture = TestTextures.Single(128, 192, 254, 137);
        using var resolver = CreateResolver(baseTexture);
        var npc = CreateNpc([1f]);

        var result = NpcEarTextureComposer.Resolve(
            npc,
            resolver,
            BaseTexturePath,
            false,
            () => throw new InvalidOperationException("No-EGT must not request an EGT."));
        var repeatedResult = NpcEarTextureComposer.Resolve(
            npc,
            resolver,
            BaseTexturePath,
            false,
            () => throw new InvalidOperationException("No-EGT must not request an EGT."));

        Assert.Equal(NpcHeadTextureSource.BaseDiffuse, result.Source);
        Assert.Equal(NpcFaceGenMap1Source.DefaultDetailModFaceGenTexture, result.FaceGenMap1Source);
        Assert.Equal(@"body_egt\000222A8_ears.dds", result.EffectiveTexturePath);
        Assert.Equal(result, repeatedResult);
        Assert.Equal(result.EffectiveTexturePath, result.FaceGenMap1EffectivePath);
        var composite = Assert.IsType<DecodedTexture>(resolver.GetTexture(result.EffectiveTexturePath!));
        Assert.Equal<byte>([129, 193, 255, 137], composite.Pixels);
        Assert.Equal<byte>([128, 192, 254, 137], baseTexture.Pixels);
    }

    [Fact]
    public void Resolve_EgtMorphsBeforeBakingDefaultMap1()
    {
        var baseTexture = TestTextures.Uniform(1, 1, 100, 110, 120);
        using var resolver = CreateResolver(baseTexture);
        var npc = CreateNpc([1f]);
        var egtRequested = false;
        var egt = CreateEgt();

        var result = NpcEarTextureComposer.Resolve(
            npc,
            resolver,
            BaseTexturePath,
            true,
            () =>
            {
                egtRequested = true;
                return egt;
            });

        Assert.True(egtRequested);
        Assert.Equal(NpcHeadTextureSource.GeneratedEgt, result.Source);
        Assert.Equal(NpcFaceGenMap1Source.DefaultDetailModFaceGenTexture, result.FaceGenMap1Source);
        var composite = Assert.IsType<DecodedTexture>(resolver.GetTexture(result.EffectiveTexturePath!));
        var morphed = Assert.IsType<DecodedTexture>(FaceGenTextureMorpher.Apply(baseTexture, egt, [1f]));
        // The retained EGT encoding yields +127/-1/-1, followed by the PC RGB64 Map1 multiplier.
        Assert.Equal<byte>([227, 109, 119, 255], morphed.Pixels);
        Assert.Equal<byte>([228, 109, 119, 255], composite.Pixels);
        Assert.Equal<byte>([100, 110, 120, 255], baseTexture.Pixels);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    public void Resolve_NonOblivionLeavesDiffuseAndLoaderUntouched(BethesdaGame game)
    {
        using var resolver = CreateResolver(TestTextures.Uniform(1, 1, 100, 110, 120));
        var npc = CreateNpc([1f], game);

        var result = NpcEarTextureComposer.Resolve(
            npc,
            resolver,
            BaseTexturePath,
            true,
            () => throw new InvalidOperationException("Other games must not request an Oblivion EGT."));

        Assert.Equal(BaseTexturePath, result.EffectiveTexturePath);
        Assert.Equal(NpcFaceGenMap1Source.None, result.FaceGenMap1Source);
    }

    private static NpcAppearance CreateNpc(
        float[] faceGenTextureCoeffs,
        BethesdaGame game = BethesdaGame.Oblivion)
    {
        return new NpcAppearance
        {
            Game = game,
            NpcFormId = 0x000222A8,
            FaceGenTextureCoeffs = faceGenTextureCoeffs
        };
    }

    private static NifTextureResolver CreateResolver(DecodedTexture baseTexture)
    {
        return new NifTextureResolver(path =>
            string.Equals(path, BaseTexturePath, StringComparison.OrdinalIgnoreCase)
                ? baseTexture
                : null);
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