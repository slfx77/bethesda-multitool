using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12.Shader;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class OblivionClassicSkinIndependentAlbedoTests
{
    private const string BasePath = @"textures\characters\imperial\headhuman.dds";
    private const string DeltaPath = @"textures\faces\oblivion.esm\000222A8_0.dds";

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(" 1", false)]
    [InlineData("1 ", false)]
    [InlineData("01", false)]
    [InlineData("1", true)]
    public void OnlyExplicitRequestAdmitsTheStockNpcHead(string? request, bool expected)
    {
        var source = ClassicSkinAuthoredAlbedo.Create(BethesdaGame.Oblivion, BasePath, DeltaPath);
        Assert.Equal(expected, BethesdaViewerClassicSkinAlbedoPolicy.IsEnabledFor(
            BethesdaViewerClassicSkinAlbedoPolicy.IsRequested(request), BethesdaGame.Oblivion,
            BethesdaViewerScenePurpose.NpcAppearance, true, source));
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, BethesdaViewerScenePurpose.NpcAppearance, true, true)]
    [InlineData(BethesdaGame.Fallout3, BethesdaViewerScenePurpose.NpcAppearance, true, true)]
    [InlineData(BethesdaGame.Unknown, BethesdaViewerScenePurpose.NpcAppearance, true, true)]
    [InlineData(BethesdaGame.Oblivion, BethesdaViewerScenePurpose.RawNif, true, true)]
    [InlineData(BethesdaGame.Oblivion, BethesdaViewerScenePurpose.CreatureAppearance, true, true)]
    [InlineData(BethesdaGame.Oblivion, BethesdaViewerScenePurpose.WorldReference, true, true)]
    [InlineData(BethesdaGame.Oblivion, BethesdaViewerScenePurpose.NpcAppearance, false, true)]
    [InlineData(BethesdaGame.Oblivion, BethesdaViewerScenePurpose.NpcAppearance, true, false)]
    internal void RequestCannotAdmitOtherMaterialsOrMissingSourceMaps(
        BethesdaGame game, BethesdaViewerScenePurpose purpose, bool faceGen, bool hasSource)
    {
        var source = hasSource ? ClassicSkinAuthoredAlbedo.Create(BethesdaGame.Oblivion, BasePath, DeltaPath) : null;
        Assert.False(BethesdaViewerClassicSkinAlbedoPolicy.IsEnabledFor(true, game, purpose, faceGen, source));
    }

    [Theory]
    [InlineData(null, DeltaPath)]
    [InlineData("", DeltaPath)]
    [InlineData(BasePath, null)]
    [InlineData(BasePath, "")]
    [InlineData(BasePath, " ")]
    public void IncompleteTexturePairsCannotBecomeAuthoredInputs(string? basePath, string? deltaPath)
    {
        Assert.Null(ClassicSkinAuthoredAlbedo.Create(BethesdaGame.Oblivion, basePath, deltaPath));
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion, true, true)]
    [InlineData(BethesdaGame.Oblivion, false, false)]
    [InlineData(BethesdaGame.Fallout3, true, false)]
    [InlineData(BethesdaGame.FalloutNewVegas, true, false)]
    public void ComposerRetainsOriginalMapsWithoutChangingTheExistingComposite(
        BethesdaGame game, bool applyEgt, bool expectedAuthored)
    {
        var textures = Textures();
        var originalBase = textures[BasePath].Pixels.ToArray();
        var originalDelta = textures[DeltaPath].Pixels.ToArray();
        using var resolver = new NifTextureResolver(path => textures.GetValueOrDefault(path));
        var npc = Npc(game);
        var result = NpcHeadTextureComposer.Resolve(npc, resolver, BasePath, applyEgt,
            () => throw new InvalidOperationException("This cohort must not request EGT."));
        Assert.Equal(expectedAuthored, result.AuthoredAlbedo is not null);
        if (result.AuthoredAlbedo is { } source)
        {
            Assert.Equal(BasePath, source.BaseTexturePath);
            Assert.Equal(DeltaPath, source.DeltaTexturePath);
            Assert.NotEqual(result.EffectiveTexturePath, source.BaseTexturePath);
            Assert.NotEqual(result.EffectiveTexturePath, source.DeltaTexturePath);
        }

        var composite = Assert.IsType<DecodedTexture>(resolver.GetTexture(result.EffectiveTexturePath!));
        byte[] expected = applyEgt ? [100, 111, 120, 37] : [100, 110, 120, 37];
        Assert.Equal(expected, composite.Pixels);
        Assert.Equal(originalBase, textures[BasePath].Pixels);
        Assert.Equal(originalDelta, textures[DeltaPath].Pixels);
        Assert.Equal(1, textures[BasePath].Width);
        Assert.Equal(2, textures[DeltaPath].Width);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void MissingMapsOrComparisonVariantDoNotPublishAuthoredInputs(
        bool missingBase, bool missingDelta, bool comparison)
    {
        var textures = Textures();
        if (missingBase) textures.Remove(BasePath);
        if (missingDelta) textures.Remove(DeltaPath);
        using var resolver = new NifTextureResolver(path => textures.GetValueOrDefault(path));
        var npc = Npc(BethesdaGame.Oblivion, comparison ? "controlled-coefficients" : null);
        var result = NpcHeadTextureComposer.Resolve(npc, resolver, BasePath, true,
            () => throw new InvalidOperationException("No coefficient array was supplied."));
        Assert.Null(result.AuthoredAlbedo);
    }

    [Fact]
    public void CloneAndNativePoseRetainTheImmutableSourcePairAndComposedDiffuse()
    {
        var pair = ClassicSkinAuthoredAlbedo.Create(BethesdaGame.Oblivion, BasePath, DeltaPath)!;
        var source = new RenderableSubmesh
        {
            Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0],
            Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1],
            UVs = [0, 0, 1, 0, 0, 1],
            Triangles = [0, 1, 2],
            DiffuseTexturePath = "facegen_egt/composed.dds",
            IsFaceGen = true,
            AuthoredSkinAlbedo = pair
        };
        var clone = RenderableSubmeshCloner.DeepClone(source);
        source.AuthoredSkinAlbedo = null;
        Assert.Same(pair, clone.AuthoredSkinAlbedo);
        var scene = new BethesdaViewerScene("independent-skin", BethesdaViewerScenePurpose.NpcAppearance,
            game: BethesdaGame.Oblivion);
        var node = scene.AddNode("head", BethesdaViewerScene.RootNodeIndex, Matrix4x4.Identity,
            Matrix4x4.Identity, BethesdaViewerNodeRole.Attachment);
        scene.MeshParts.Add(new BethesdaViewerMeshPart { Name = "head", NodeIndex = node, Submesh = clone });
        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        clone.AuthoredSkinAlbedo = null;
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        Assert.Same(pair, Assert.Single(posed.Source.MeshParts).NativeSemantics.AuthoredSkinAlbedo);
        Assert.Equal("facegen_egt/composed.dds", Assert.Single(posed.Mesh.Submeshes).DiffuseTexturePath);
        Assert.Empty(posed.UnsupportedMeshParts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", TestCategories.ShaderCompile)]
    public void CompiledIndependentInputsLinkToTheAcceptedSkinVertexShader(bool factorOne)
    {
        ShaderCompileTestGuard.SkipUnlessEnabled();
        using var vertex = Compiler.Reflect<ID3D12ShaderReflection>(GpuShaderCompiler12.Compile(
            "reference.vert.hlsl", "main", "vs_5_1", new ShaderMacro("REFERENCE_OBLIVION_CLASSIC_SKIN", "1")));
        using var pixel = Compiler.Reflect<ID3D12ShaderReflection>(GpuShaderCompiler12.Compile(
            factorOne
                ? "reference_classic_skin_independent_factor_one.frag.hlsl"
                : "reference_classic_skin_independent.frag.hlsl",
            factorOne ? "mainIndependentFactorOne" : "mainIndependent", "ps_5_1"));
        var outputs = new List<ShaderParameterDescription>();
        for (var index = 0u; index < vertex.Description.OutputParameters; index++)
            outputs.Add(vertex.GetOutputParameterDescription(index));
        var consumed = 0;
        var sourceTextureInputsObserved = false;
        for (var index = 0u; index < pixel.Description.InputParameters; index++)
        {
            var input = pixel.GetInputParameterDescription(index);
            if (input.SystemValueType != SystemValueType.Undefined || (int)input.ReadWriteMask == 0) continue;
            var output = Assert.Single(outputs, candidate => candidate.SemanticIndex == input.SemanticIndex &&
                                                             string.Equals(candidate.SemanticName, input.SemanticName,
                                                                 StringComparison.OrdinalIgnoreCase));
            Assert.Equal(output.Register, input.Register);
            Assert.Equal(output.ComponentType, input.ComponentType);
            Assert.Equal((int)input.ReadWriteMask, (int)input.ReadWriteMask & (int)output.UsageMask);
            if (input.SemanticIndex == 8 && input.SemanticName.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal(5, (int)input.ReadWriteMask & 5); // Actual BaseMap X and independent Map0 Z.
                sourceTextureInputsObserved = true;
            }

            consumed++;
        }

        Assert.True(consumed > 0);
        Assert.True(sourceTextureInputsObserved);
        Assert.Equal(1u, pixel.Description.OutputParameters);
        Assert.Equal(15, (int)pixel.GetOutputParameterDescription(0).UsageMask);
    }

    private static Dictionary<string, DecodedTexture> Textures()
    {
        return new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase)
        {
            [BasePath] = TestTextures.Single(100, 110, 120, 37),
            [DeltaPath] = TestTextures.FromTexels(2, 1, (127, 127, 0, 0), (128, 129, 255, 255))
        };
    }

    private static NpcAppearance Npc(BethesdaGame game, string? variant = null)
    {
        return new NpcAppearance
        {
            NpcFormId = 0x222A8,
            Game = game,
            AuthoredFaceGenMap0Path = DeltaPath,
            RenderVariantLabel = variant
        };
    }
}