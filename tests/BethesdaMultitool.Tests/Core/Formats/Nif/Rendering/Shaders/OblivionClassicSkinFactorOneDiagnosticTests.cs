using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12.Shader;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

public sealed class OblivionClassicSkinFactorOneDiagnosticTests
{
    private const string DiagnosticFile = "reference_classic_skin_factor_one.frag.hlsl";
    private const string DiagnosticEntry = "mainFactorOne";

    private const string DiagnosticHeader =
        "// Diagnostic only: final RGB lighting factor one; no new retail/default behavior.\n" +
        "// All other source is pinned to the ordinary skin entry by an exact output-scope contract.\n";

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("01", false)]
    [InlineData("true", false)]
    [InlineData("TRUE", false)]
    [InlineData(" 1", false)]
    [InlineData("1 ", false)]
    [InlineData("1\n", false)]
    [InlineData("1\0", false)]
    [InlineData("-1", false)]
    public void RequestRequiresTheExactExplicitOptIn(string? value, bool expected)
    {
        Assert.Equal(expected, BethesdaViewerClassicSkinDiagnosticPolicy.IsRequested(value));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EveryGameAndFaceGenCombinationRemainsIsolated(bool requested, bool isFaceGen)
    {
        foreach (var game in Enum.GetValues<BethesdaGame>())
        {
            var actual = BethesdaViewerClassicSkinDiagnosticPolicy.IsEnabledFor(requested, game, isFaceGen);
            Assert.Equal(requested && isFaceGen && game == BethesdaGame.Oblivion, actual);
        }

        Assert.False(BethesdaViewerClassicSkinDiagnosticPolicy.IsEnabledFor(requested, (BethesdaGame)(-1), isFaceGen));
    }

    [Fact]
    public void DiagnosticChangesOnlyTheFinalRgbFactorAndEntryName()
    {
        // Whole-program equivalence outside the intervention catches changes to tint, addressing,
        // clipping, normal/varying equations, fog and alpha, including unrelated new source edits.
        var ordinary = SourceContract.ReadShaderSource("reference_classic_skin.frag.hlsl");
        var diagnostic = SourceContract.ReadShaderSource(DiagnosticFile);
        Assert.Equal(1, SourceContract.CountOccurrences(ordinary, "float4 main(PSInput input) : SV_Target"));
        Assert.Equal(1, SourceContract.CountOccurrences(ordinary, "float3 lit = albedo * lighting;"));
        var expected = DiagnosticHeader + ordinary
            .Replace("float4 main(PSInput input) : SV_Target", "float4 mainFactorOne(PSInput input) : SV_Target",
                StringComparison.Ordinal)
            .Replace("float3 lit = albedo * lighting;", "float3 lit = albedo * float3(1.0, 1.0, 1.0);",
                StringComparison.Ordinal);
        Assert.Equal(expected, diagnostic);
        SourceContract.AssertOrder(diagnostic,
            "float3 albedo = baseMap.rgb * input.vVertexColor.rgb;",
            "float3 lit = albedo * float3(1.0, 1.0, 1.0);",
            "float3 outputRgb = ApplyClassicSkinFog(lit, input.vWorldPos);",
            "return float4(outputRgb, baseMap.a);");
    }

    [Fact]
    public void SessionCapturesOneImmutableRequestAndReportsActualSelection()
    {
        var session = RenderingSource("D3D12", "Viewer", "BethesdaViewerRenderSession12.cs");
        Assert.Contains("private readonly bool _classicSkinFactorOneRequested =", session, StringComparison.Ordinal);
        Assert.Equal(1, SourceContract.CountOccurrences(session, "_classicSkinFactorOneRequested ="));
        Assert.Equal(1,
            SourceContract.CountOccurrences(session, "BethesdaViewerClassicSkinDiagnosticPolicy.EnvironmentVariable"));
        SourceContract.AssertOrder(session,
            "_staticRenderer = new BethesdaViewerStaticRenderer12(",
            "_textureCache.WhitePixel.BindlessIndex,",
            "_classicSkinFactorOneRequested,",
            "_independentSkinAlbedoRequested,",
            "AcquireIndependentSkinAlbedo,",
            "BethesdaSceneViewer: skin-factor-one diagnostic game={0} requested={1}",
            "_staticRenderer.ClassicSkinEligibleDrawCount,",
            "_staticRenderer.ClassicSkinFactorOneDrawCount);");
        var factory = RenderingSource("D3D12", "ReferencePipelineFactory12.cs");
        Assert.DoesNotContain("BethesdaViewerClassicSkinDiagnosticPolicy.EnvironmentVariable", factory,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticCannotBypassExistingOpaqueOrGameAdmission()
    {
        var renderer = RenderingSource("D3D12", "Viewer", "BethesdaViewerStaticRenderer12.cs");
        var routing = SourceContract.Extract(renderer,
            "private static OpaqueSpecializationRoute ResolveOpaqueSpecialization(",
            "var facts = new ModernStandardOpaqueShaderFacts(");
        SourceContract.AssertOrder(routing,
            "draw.NativeSemantics.SkyType is not null",
            "submesh.AlphaBlend",
            "submesh.IsDecal",
            "submesh.DepthTestOff",
            "draw.NativeSemantics.NativeAlphaRenderMode == NifAlphaRenderMode.AlphaToCoverage",
            "submesh.MaterialAlphaController is not null",
            "return default;",
            "game == Core.Games.BethesdaGame.Oblivion && draw.NativeSemantics.IsFaceGen",
            "BethesdaViewerClassicSkinDiagnosticPolicy.IsEnabledFor(",
            "OpaqueSpecializationFamily.ClassicSkinFactorOne",
            "pipelines.GetDirectClassicSkinFactorOnePso(submesh.DoubleSided)",
            "pipelines.TryGetDirectClassicSkinPso(submesh.DoubleSided");
        Assert.Equal(1, SourceContract.CountOccurrences(renderer, "pipelines.GetDirectClassicSkinFactorOnePso("));
        Assert.Contains("bool classicSkinFactorOneRequested = false", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void SeparateDiagnosticPairReusesTheOrdinaryVertexAndFailsClosed()
    {
        var factory = RenderingSource("D3D12", "ReferencePipelineFactory12.cs");
        var resolve = SourceContract.Extract(factory,
            "public ID3D12PipelineState GetDirectClassicSkinFactorOnePso(",
            "/// <summary>Depth-only shadow-pass PSO");
        SourceContract.AssertOrder(resolve,
            "ObjectDisposedException.ThrowIf(_disposed, this);",
            "!DirectClassicSkinRequested || !DirectClassicSkinAvailable",
            "throw new InvalidOperationException(",
            "CreateDirectClassicSkinFactorOnePipelines(_directClassicSkinVertexShader);");
        var creation = SourceContract.Extract(factory,
            "private void CreateDirectClassicSkinFactorOnePipelines(",
            "private void TryCreateModernStandardOpaquePipelines()");
        Assert.DoesNotContain("catch", creation, StringComparison.Ordinal);
        Assert.DoesNotContain("new ShaderMacro", creation, StringComparison.Ordinal);
        Assert.DoesNotContain("vs_5_1", creation, StringComparison.Ordinal);
        SourceContract.AssertOrder(creation,
            "\"reference_classic_skin_factor_one.frag.hlsl\", \"mainFactorOne\", \"ps_5_1\"",
            "back = CreatePipelineState(",
            "vertexShader, pixelShader, doubleSided: false, blendAttachment: null,",
            "depthWriteEnabled: true);",
            "doubleSided = CreatePipelineState(",
            "vertexShader, pixelShader, doubleSided: true, blendAttachment: null,",
            "depthWriteEnabled: true);",
            "_directClassicSkinFactorOneBackPso = back;",
            "_directClassicSkinFactorOneDoublePso = doubleSided;",
            "back = null;",
            "doubleSided = null;",
            "finally",
            "DisposeAbandonedConstructionPipeline(ref doubleSided);",
            "DisposeAbandonedConstructionPipeline(ref back);");
        Assert.Equal(1, SourceContract.CountOccurrences(factory, "_directClassicSkinFactorOneDoublePso?.Dispose();"));
        Assert.Equal(1, SourceContract.CountOccurrences(factory, "_directClassicSkinFactorOneBackPso?.Dispose();"));
    }

    [Fact]
    public void DiagnosticAddsOneDistinctPixelEntryWithoutReplacingTheOrdinaryPair()
    {
        var entry = Assert.Single(ShaderPermutations.Other, p => p.File == DiagnosticFile);
        Assert.Equal(DiagnosticEntry, entry.EntryPoint);
        Assert.Equal("ps_5_1", entry.Profile);
        Assert.Empty(entry.Macros);
        Assert.Single(ShaderPermutations.Other, p => p.File == "reference_classic_skin.frag.hlsl");
        Assert.Single(ShaderPermutations.All, p => p.Macros.Any(m => m.Name == "REFERENCE_OBLIVION_CLASSIC_SKIN"));
        Assert.Contains(GpuShaderCompiler12.BuildCacheKey(DiagnosticFile, DiagnosticEntry, "ps_5_1", []),
            GpuShaderBytecodePack12.CurrentPermutationKeys());
    }

    [Fact]
    [Trait("Category", TestCategories.ShaderCompile)]
    public void DiagnosticConsumedInputsLinkToTheExactOrdinaryVertexRegisters()
    {
        ShaderCompileTestGuard.SkipUnlessEnabled();
        using var vertex = Compiler.Reflect<ID3D12ShaderReflection>(GpuShaderCompiler12.Compile(
            "reference.vert.hlsl", "main", "vs_5_1", new ShaderMacro("REFERENCE_OBLIVION_CLASSIC_SKIN", "1")));
        using var pixel = Compiler.Reflect<ID3D12ShaderReflection>(GpuShaderCompiler12.Compile(
            DiagnosticFile, DiagnosticEntry, "ps_5_1"));
        var outputs = new List<ShaderParameterDescription>();
        for (var index = 0u; index < vertex.Description.OutputParameters; index++)
        {
            outputs.Add(vertex.GetOutputParameterDescription(index));
        }

        var consumed = 0;
        for (var index = 0u; index < pixel.Description.InputParameters; index++)
        {
            var input = pixel.GetInputParameterDescription(index);
            if (input.SystemValueType != SystemValueType.Undefined || (int)input.ReadWriteMask == 0) continue;
            var output = Assert.Single(outputs, candidate =>
                candidate.SemanticIndex == input.SemanticIndex &&
                string.Equals(candidate.SemanticName, input.SemanticName, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(output.Register, input.Register);
            Assert.Equal(output.ComponentType, input.ComponentType);
            Assert.Equal((int)input.ReadWriteMask, (int)input.ReadWriteMask & (int)output.UsageMask);
            consumed++;
        }

        Assert.True(consumed > 0);
        Assert.Equal(1u, pixel.Description.OutputParameters);
        var target = pixel.GetOutputParameterDescription(0);
        Assert.Equal("SV_Target", target.SemanticName, true);
        Assert.Equal(15, (int)target.UsageMask); // RGB result plus unchanged BaseMap alpha.
    }

    private static string RenderingSource(params string[] path)
    {
        return SourceContract.ReadSource(
            ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", .. path]);
    }
}