using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class OblivionNpcBodyTextureComposerTests
{
    private const string FemaleEgt = @"meshes\characters\_male\upperbodyhumanfemale.egt";
    private const string MaleEgt = @"meshes\characters\_male\upperbodyhumanmale.egt";
    private const string BodyEgt = @"meshes\characters\_male\body.egt";
    private const string Upper = @"textures\characters\orc\female\UpperBodyFemale.dds";
    private const string Lower = @"textures\characters\orc\female\LegFemale.dds";
    private const string Hand = @"textures\characters\orc\female\HandFemale.dds";
    private const string Foot = @"textures\characters\orc\female\FootFemale.dds";
    private const string Tail = @"textures\characters\argonian\female\Tail.dds";
    private static readonly byte[] ExpectedLowerPixels = [109, 109, 141, 77, 109, 109, 79, 99];
    private static readonly byte[] ExpectedHandPixels = [119, 119, 151, 77, 119, 119, 89, 99];
    private static readonly byte[] ExpectedFootPixels = [129, 129, 161, 77, 129, 129, 99, 99];
    private static readonly byte[] ExpectedTailPixels = [139, 139, 171, 77, 139, 139, 109, 99];
    private static readonly byte[] ExpectedBasePixels = [100, 100, 100, 77, 100, 100, 100, 99];
    private static readonly byte[] ExpectedCappedPixels = [111, 99, 99, 77];
    private static readonly byte[] ExpectedUncappedPixels = [253, 99, 99, 77];

    [Theory]
    [InlineData(false, 0, MaleEgt)]
    [InlineData(true, 0, FemaleEgt)]
    [InlineData(false, 1, BodyEgt)]
    [InlineData(true, 1, BodyEgt)]
    [InlineData(false, 2, BodyEgt)]
    [InlineData(true, 2, BodyEgt)]
    [InlineData(false, 3, BodyEgt)]
    [InlineData(true, 3, BodyEgt)]
    [InlineData(false, 4, BodyEgt)]
    [InlineData(true, 4, BodyEgt)]
    public void RetailModelSelection_IsSexSpecificOnlyForUpperBody(bool female, int part, string expected)
    {
        Assert.Equal(expected, OblivionNpcBodyTextureComposer.ResolveEgtPath(female, (NpcBodyTexturePart)part));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compose_UsesDistinctSpatialModelsAndEachAtlasWithoutMutatingInputs(bool female)
    {
        var coefficients = new[] { 1f };
        var npc = Appearance(female, coefficients);
        var source = BaseTexture(100);
        var bases = new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase)
        {
            ["UpperBodyFemale.dds"] = source, ["LegFemale.dds"] = BaseTexture(110),
            ["HandFemale.dds"] = BaseTexture(120), ["FootFemale.dds"] = BaseTexture(130),
            ["Tail.dds"] = BaseTexture(140)
        };
        var originalPixels = bases.ToDictionary(static p => p.Key, static p => p.Value.Pixels.ToArray());
        using var resolver = new NifTextureResolver(path =>
            bases.FirstOrDefault(pair => path.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase)).Value);
        var femaleEgt = SpatialMorph(11, 0, 0);
        var maleEgt = SpatialMorph(0, 21, 0);
        var bodyEgt = SpatialMorph(0, 0, 31);
        var paths = new List<string>();

        var result = OblivionNpcBodyTextureComposer.Compose(npc, resolver, path =>
        {
            paths.Add(path);
            return path switch { FemaleEgt => femaleEgt, MaleEgt => maleEgt, BodyEgt => bodyEgt, _ => null };
        });

        Assert.Contains(female ? FemaleEgt : MaleEgt, paths);
        Assert.DoesNotContain(female ? MaleEgt : FemaleEgt, paths);
        Assert.Contains(BodyEgt, paths);
        var upper = AssertTexture(resolver, result.UpperBody);
        // Independent literal results: signed odd deltas survive the existing half-scale encoding;
        // zero becomes byte 127, whose shader inverse is -1. No second production helper is the oracle.
        byte[] expectedUpper = female
            ? [111, 99, 99, 77, 89, 99, 99, 99]
            : [99, 121, 99, 77, 99, 79, 99, 99];
        Assert.Equal(expectedUpper, upper.Pixels);
        Assert.Equal(ExpectedLowerPixels, AssertTexture(resolver, result.LowerBody).Pixels);
        Assert.Equal(ExpectedHandPixels, AssertTexture(resolver, result.Hands).Pixels);
        Assert.Equal(ExpectedFootPixels, AssertTexture(resolver, result.Feet).Pixels);
        Assert.Equal(ExpectedTailPixels, AssertTexture(resolver, result.Tail).Pixels);
        Assert.Equal(5,
            new[] { result.UpperBody, result.LowerBody, result.Hands, result.Feet, result.Tail }.Distinct().Count());
        Assert.Equal(ExpectedBasePixels, source.Pixels);
        foreach (var pair in bases)
        {
            Assert.Equal(originalPixels[pair.Key], pair.Value.Pixels);
        }

        Assert.Single(coefficients);
        Assert.Equal(1f, coefficients[0]);
    }

    [Fact]
    public void Compose_CapsOnlyBodyCoefficientsAtThirtyWithoutMutatingTheSharedHeadArray()
    {
        var coefficients = new float[50];
        coefficients[29] = 1;
        coefficients[30] = 1;
        coefficients[49] = 1;
        var npc = Appearance(true, coefficients);
        var morphs = Enumerable.Range(0, 50).Select(static _ => SingleMorph(0)).ToArray();
        morphs[29] = SingleMorph(11);
        morphs[30] = SingleMorph(61);
        morphs[49] = SingleMorph(81);
        var egt = EgtParser.CreateFromMorphs(1, 1, morphs);
        var source = DecodedTexture.FromBaseLevel([100, 100, 100, 77], 1, 1);
        using var resolver = new NifTextureResolver(_ => source);

        var result = OblivionNpcBodyTextureComposer.Compose(npc, resolver, _ => egt);

        foreach (var path in new[] { result.UpperBody, result.LowerBody, result.Hands, result.Feet, result.Tail })
        {
            Assert.Equal(ExpectedCappedPixels, AssertTexture(resolver, path).Pixels);
        }

        Assert.Equal(50, coefficients.Length);
        Assert.Equal(1f, coefficients[29]);
        Assert.Equal(1f, coefficients[30]);
        Assert.Equal(1f, coefficients[49]);
        var uncapped = FaceGenTextureMorpher.Apply(source, egt, coefficients);
        Assert.NotNull(uncapped);
        Assert.Equal(ExpectedUncappedPixels, uncapped.Pixels);
    }

    [Fact]
    public void MissingFemaleModel_DoesNotSubstituteMaleOrBodyModelForUpperAtlas()
    {
        var npc = Appearance(true, [1]);
        using var resolver = new NifTextureResolver(_ => BaseTexture(100));
        var result = OblivionNpcBodyTextureComposer.Compose(npc, resolver,
            path => path == FemaleEgt ? null : SpatialMorph(0, 0, 31));

        Assert.Equal(Upper, result.UpperBody);
        Assert.NotEqual(Hand, result.Hands);
        Assert.NotEqual(Lower, result.LowerBody);
        Assert.NotEqual(Foot, result.Feet);
        Assert.NotEqual(Tail, result.Tail);
    }

    [Fact]
    public void MissingBodyModel_PreservesAllFourOriginalAtlases()
    {
        using var resolver = new NifTextureResolver(_ => BaseTexture(100));
        var result = OblivionNpcBodyTextureComposer.Compose(Appearance(true, [1]), resolver,
            path => path == BodyEgt ? null : SpatialMorph(11, 0, 0));
        Assert.NotEqual(Upper, result.UpperBody);
        Assert.Equal(Lower, result.LowerBody);
        Assert.Equal(Hand, result.Hands);
        Assert.Equal(Foot, result.Feet);
        Assert.Equal(Tail, result.Tail);
    }

    [Fact]
    public void MissingHandBase_DoesNotSubstituteAnotherAtlasOrAffectOtherParts()
    {
        // The resolver retries normalized/root-stripped paths. Reject every spelling of this
        // missing asset rather than accidentally supplying it from the fixture's catch-all.
        using var resolver = new NifTextureResolver(path =>
            path.EndsWith("HandFemale.dds", StringComparison.OrdinalIgnoreCase) ? null : BaseTexture(100));
        var result =
            OblivionNpcBodyTextureComposer.Compose(Appearance(true, [1]), resolver, _ => SpatialMorph(11, 0, 0));
        Assert.Equal(Hand, result.Hands);
        Assert.NotEqual(Upper, result.UpperBody);
        Assert.NotEqual(Lower, result.LowerBody);
        Assert.NotEqual(Foot, result.Feet);
        Assert.NotEqual(Tail, result.Tail);
    }

    [Theory]
    [InlineData((int)BethesdaGame.Fallout3)]
    [InlineData((int)BethesdaGame.FalloutNewVegas)]
    [InlineData((int)BethesdaGame.Skyrim)]
    public void OtherGames_AreNotRoutedThroughTheTes4Composer(int game)
    {
        var npc = Appearance(true, [1], (BethesdaGame)game);
        using var resolver =
            new NifTextureResolver(_ => throw new InvalidOperationException("No texture load expected"));
        var result = OblivionNpcBodyTextureComposer.Compose(npc, resolver,
            _ => throw new InvalidOperationException("No TES4 EGT load expected"));
        Assert.Equal(NpcBodyTextureSet.FromAppearance(npc), result);
    }

    [Fact]
    public void MissingCoefficients_DoNotLoadOrTintTextures()
    {
        var npc = Appearance(true, null);
        using var resolver =
            new NifTextureResolver(_ => throw new InvalidOperationException("No texture load expected"));
        var result = OblivionNpcBodyTextureComposer.Compose(npc, resolver,
            _ => throw new InvalidOperationException("No EGT load expected"));
        Assert.Equal(NpcBodyTextureSet.FromAppearance(npc), result);
    }

    [Fact]
    public void GeneratedTextureKeys_CoverAllAtlasesAndPreserveFalloutSplitHands()
    {
        var keys = NpcTextureHelpers.BuildNpcGeneratedTextureKeys(Appearance(true, [1]));
        Assert.Equal(12, keys.Length);
        Assert.Equal(12, keys.Distinct().Count());
        Assert.Contains(@"facegen_egt\00085969.dds", keys);
        Assert.Contains(@"body_egt\00085969_ears.dds", keys);
        Assert.Contains(@"body_egt\00085969_upperbody.dds", keys);
        Assert.Contains(@"body_egt\00085969_lowerbody.dds", keys);
        Assert.Contains(@"body_egt\00085969_hands.dds", keys);
        Assert.Contains(@"body_egt\00085969_feet.dds", keys);
        Assert.Contains(@"body_egt\00085969_tail.dds", keys);
        Assert.Contains(@"body_skin\00085969_upperbody.dds", keys);
        Assert.Contains(@"body_skin\00085969_lowerbody.dds", keys);
        Assert.Contains(@"body_skin\00085969_hands.dds", keys);
        Assert.Contains(@"body_skin\00085969_feet.dds", keys);
        Assert.Contains(@"body_skin\00085969_tail.dds", keys);
        var fallout =
            NpcTextureHelpers.BuildNpcGeneratedTextureKeys(Appearance(true, [1], BethesdaGame.FalloutNewVegas));
        Assert.Equal(5, fallout.Length);
        Assert.Contains(@"body_egt\00085969_lefthand.dds", fallout);
        Assert.Contains(@"body_egt\00085969_righthand.dds", fallout);
        Assert.DoesNotContain(@"body_egt\00085969_hands.dds", fallout);
    }

    [Fact]
    public void EquipmentAndBarePartPlans_UseTheFiveResolvedAtlasesAndKeepCoverage()
    {
        var npc = Appearance(true, [1]);
        var textures =
            new NpcBodyTextureSet("tinted-upper", "tinted-lower", "tinted-hands", "tinted-feet", "tinted-tail");
        Assert.Equal("tinted-upper", NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, Upper, textures));
        Assert.Equal("tinted-lower", NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, Lower, textures));
        Assert.Equal("tinted-hands", NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, Hand, textures));
        Assert.Equal("tinted-feet", NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, Foot, textures));
        Assert.Equal("tinted-tail", NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, Tail, textures));
        Assert.Null(
            NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, @"textures\armor\iron\f\Greaves.dds", textures));
        var parts = NpcCompositionPlanner.BuildBodyParts(npc, new NpcCompositionOptions(), 0, textures);
        Assert.Equal(5, parts.Count);
        Assert.Contains(parts, static p => p.MeshPath == "upper.nif" && p.TextureOverride == "tinted-upper");
        Assert.Contains(parts, static p => p.MeshPath == "lower.nif" && p.TextureOverride == "tinted-lower");
        Assert.Contains(parts, static p => p.MeshPath == "hands.nif" && p.TextureOverride == "tinted-hands");
        Assert.Contains(parts, static p => p.MeshPath == "feet.nif" && p.TextureOverride == "tinted-feet");
        Assert.Contains(parts, static p => p.MeshPath == "tail.nif" && p.TextureOverride == "tinted-tail");
        Assert.Empty(NpcCompositionPlanner.BuildBodyParts(npc, new NpcCompositionOptions(), 0x803c, textures));
        Assert.Empty(NpcCompositionPlanner.BuildBodyParts(npc, new NpcCompositionOptions { HeadOnly = true }, 0,
            textures));
    }

    private static NpcAppearance Appearance(bool female, float[]? coefficients,
        BethesdaGame game = BethesdaGame.Oblivion)
    {
        return new NpcAppearance
        {
            Game = game, NpcFormId = 0x85969, IsFemale = female, FaceGenTextureCoeffs = coefficients,
            BodyTexturePath = Upper, LowerBodyTexturePath = Lower, HandTexturePath = Hand,
            FootTexturePath = Foot, TailTexturePath = Tail, UpperBodyNifPath = "upper.nif",
            LowerBodyNifPath = "lower.nif", HandNifPath = "hands.nif", FootNifPath = "feet.nif",
            TailNifPath = "tail.nif"
        };
    }

    private static DecodedTexture AssertTexture(NifTextureResolver resolver, string? path)
    {
        Assert.NotNull(path);
        return Assert.IsType<DecodedTexture>(resolver.GetTexture(path));
    }

    private static DecodedTexture BaseTexture(byte value)
    {
        return DecodedTexture.FromBaseLevel([value, value, value, 77, value, value, value, 99], 2, 1);
    }

    private static EgtParser SpatialMorph(sbyte red, sbyte green, sbyte blue)
    {
        return EgtParser.CreateFromMorphs(2, 1,
        [
            new EgtMorph
            {
                Scale = 1, DeltaR = [red, (sbyte)-red], DeltaG = [green, (sbyte)-green], DeltaB = [blue, (sbyte)-blue]
            }
        ]);
    }

    private static EgtMorph SingleMorph(sbyte red)
    {
        return new EgtMorph { Scale = 1, DeltaR = [red], DeltaG = [0], DeltaB = [0] };
    }
}