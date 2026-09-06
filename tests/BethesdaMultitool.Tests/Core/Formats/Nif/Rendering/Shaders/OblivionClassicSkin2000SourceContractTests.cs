using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Production-route and source-order pins for the retail Oblivion SKIN2000 specialization.
/// </summary>
public sealed class OblivionClassicSkin2000SourceContractTests
{
    [Fact]
    public void DeterministicCpuAndGpuRoutesSelectClassicSkinOnlyFromFaceGen()
    {
        var sprite = RenderingSource("Rasterization", "NifSpriteRenderer.cs");
        var rasterizer = RenderingSource("Rasterization", "NifScanlineRasterizer.cs");
        var gpu = D3D12Source("Gpu", "D3D12", "GpuSpriteRenderer12.cs");
        var shader = SourceContract.ReadShaderSource("skin.frag.hlsl");

        Assert.Equal(2, SourceContract.CountOccurrences(
            sprite, "tri.IsFaceGen, tri.HasTintColor, tri.IsDoubleSided"));
        SourceContract.AssertOrder(
            sprite,
            "internal static float ComputeMaterialShade(",
            "if (isFaceGen)",
            "return ComputeClassicSkinShade(nx, ny, nz);",
            "if (hasTintColor)",
            "return ComputeTintedShade(nx, ny, nz);",
            "return ComputeShade(nx, ny, nz, twoSidedLighting);");
        Assert.Contains(
            "var normalStrength = tri.IsFaceGen ? 1f : NifSpriteRenderer.BumpStrength;",
            rasterizer,
            StringComparison.Ordinal);
        Assert.Contains(
            "shade = NifSpriteRenderer.ComputeMaterialShade(\n" +
            "                        nx, ny, nz, tri.IsFaceGen, tri.HasTintColor, tri.IsDoubleSided);",
            rasterizer,
            StringComparison.Ordinal);
        Assert.Contains("if (tri.IsEmissive && !tri.IsFaceGen)", rasterizer,
            StringComparison.Ordinal);
        Assert.Contains("if (!tri.IsFaceGen && tri.IsEyeEnvmap)", rasterizer,
            StringComparison.Ordinal);
        Assert.Contains("else if (!tri.IsFaceGen &&", rasterizer, StringComparison.Ordinal);

        Assert.Contains("if (!tri.IsFaceGen && tri.HasEffectTint)", rasterizer,
            StringComparison.Ordinal);
        Assert.Contains("emissiveMask[idx] = tri.IsEmissive && !tri.IsFaceGen", rasterizer,
            StringComparison.Ordinal);
        Assert.Contains("if (!tri.IsFaceGen &&\n            (tri.EmissiveR > 0f", rasterizer,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            rasterizer,
            "if (!tri.IsFaceGen && tri.IsStarfieldVertexLerp)",
            "else if (!tri.IsFaceGen && tri.HasTintColor)",
            "else\n        {\n            fr = r * shade;");

        Assert.Contains("new ShaderMacro(\"CLASSIC_SKIN2000\", \"1\")", gpu,
            StringComparison.Ordinal);
        Assert.Contains("sub.IsDoubleSided,\n                sub.IsFaceGen);", gpu,
            StringComparison.Ordinal);
        Assert.Contains(
            "PixelShader = key.ClassicSkin ? _classicSkinPsBytecode : _psBytecode",
            gpu,
            StringComparison.Ordinal);

        Assert.Contains("#ifndef CLASSIC_SKIN2000\n        mapN.xy *= uAmbient.w;", shader,
            StringComparison.Ordinal);
        Assert.Contains("#if CLASSIC_SKIN2000\n    // IsFaceGen owns this PSO permutation",
            shader,
            StringComparison.Ordinal);
        var classicColorLane = SourceContract.Extract(
            shader,
            "#if CLASSIC_SKIN2000\n    // Retail SKIN2000's only optional colour multiplier",
            "#else");
        Assert.Contains("if ((flags & HAS_VCOL) != 0u)", classicColorLane,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HAS_TINT", classicColorLane, StringComparison.Ordinal);
        Assert.DoesNotContain("IS_STARFIELD_VERTEX_LERP", classicColorLane, StringComparison.Ordinal);
        Assert.Contains("#ifndef CLASSIC_SKIN2000\n    texColor.rgb *= uEffectTint.rgb;", shader,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            shader,
            "#if CLASSIC_SKIN2000\n    // IsFaceGen owns this PSO permutation",
            "shade = computeClassicSkin2000Shade(normal);",
            "#else\n    if ((flags & IS_EMISSIVE) != 0u)");
        Assert.DoesNotContain("0.25 + 0.75", SourceContract.Extract(
            shader,
            "float computeClassicSkin2000Shade",
            "float computeTintedShade"), StringComparison.Ordinal);
    }

    [Fact]
    public void BothPixelPermutationsAreAuthoritativeShippedPackEntries()
    {
        var classicMacro = new ShaderMacro("CLASSIC_SKIN2000", "1");
        var spriteKey = GpuShaderCompiler12.BuildCacheKey(
            "skin.frag.hlsl", "main", "ps_5_1", [classicMacro]);
        var viewerKey = GpuShaderCompiler12.BuildCacheKey(
            "reference_classic_skin.frag.hlsl", "main", "ps_5_1", []);
        var shippedKeys = GpuShaderBytecodePack12.CurrentPermutationKeys();

        Assert.Contains(spriteKey, shippedKeys);
        Assert.Contains(viewerKey, shippedKeys);
        Assert.Single(ShaderPermutations.Other, permutation =>
            permutation.File == "skin.frag.hlsl" &&
            permutation.Macros.Any(macro => macro.Name == "CLASSIC_SKIN2000"));
        Assert.Single(ShaderPermutations.Other, permutation =>
            permutation.File == "reference_classic_skin.frag.hlsl");
    }

    [Fact]
    public void NativeActorsRouteUsesFaceGenAndReportsClassicSkinTelemetry()
    {
        var renderer = D3D12Source("D3D12", "Viewer", "BethesdaViewerStaticRenderer12.cs");
        var factory = D3D12Source("D3D12", "ReferencePipelineFactory12.cs");
        var materializer = D3D12Source(
            "D3D12", "Viewer", "BethesdaViewerScenePoseMaterializer12.cs");
        var session = D3D12Source(
            "D3D12", "Viewer", "BethesdaViewerRenderSession12.cs");
        var buildGpuScene = SourceContract.Extract(
            session,
            "private void BuildGpuScene()",
            "private BethesdaViewerAnimationClip? SelectedClip");

        SourceContract.AssertOrder(
            renderer,
            "game == Core.Games.BethesdaGame.Oblivion && draw.NativeSemantics.IsFaceGen",
            "pipelines.TryGetDirectClassicSkinPso(submesh.DoubleSided",
            "OpaqueSpecializationFamily.ClassicSkin");
        Assert.Contains("\"classic-skin SKIN2000\"", renderer, StringComparison.Ordinal);
        Assert.Contains("DirectClassicSkinRequested = game == BethesdaGame.Oblivion;", factory,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"reference_classic_skin.frag.hlsl\", \"main\", \"ps_5_1\"",
            factory,
            StringComparison.Ordinal);
        Assert.DoesNotContain("skin light transmission", materializer,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "submesh.StarfieldMaterialColor.IsVertexLerp ||\n                    part.NativeSemantics.IsFaceGen",
            materializer,
            StringComparison.Ordinal);
        Assert.Equal(1, SourceContract.CountOccurrences(
            session,
            "BethesdaSceneViewer: opaque-specialization census game={0}"));
        SourceContract.AssertOrder(
            buildGpuScene,
            "_staticRenderer = new BethesdaViewerStaticRenderer12(",
            "_staticRenderer.DescribeOpaqueSpecialization() is { } specializationCensus",
            "Log.Info(",
            "BethesdaSceneViewer: opaque-specialization census game={0}",
            "classicSkinRequested={1} classicSkinDirectAvailable={2} detail={3}",
            "posed.Source.Game",
            "_pipelines.DirectClassicSkinRequested",
            "_pipelines.DirectClassicSkinAvailable",
            "specializationCensus);");
    }

    [Fact]
    public void NativeShaderPreservesRetailNormalLightVertexAndFogOrder()
    {
        var shader = SourceContract.ReadShaderSource("reference_classic_skin.frag.hlsl");

        Assert.Contains("mapNormal = normalSample.rgb * 2.0 - 1.0;", shader,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            shader,
            "float3 normal = geometricNormal;",
            "if (input.vRenderState.y > 0.5)",
            "normalSample = SampleClassicSkinTexture(",
            "mapNormal = normalize(mapNormal);");
        Assert.DoesNotContain("bump strength", shader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mapNormal.xy *=", shader, StringComparison.Ordinal);
        Assert.Contains("float NdotL = max(dot(normal, lightDirection), 0.0);", shader,
            StringComparison.Ordinal);
        Assert.Contains("float NdotV = max(dot(normal, viewDirection), 0.0);", shader,
            StringComparison.Ordinal);
        Assert.Contains(
            "float3 rim = 0.5 * lightColor *\n        oneMinusNdotV * oneMinusNdotV * oneMinusNdotV;",
            shader,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            shader,
            "ambientColor + lightColor * NdotL + rim",
            "float3 albedo = baseMap.rgb * input.vVertexColor.rgb;",
            "float3 lit = albedo * lighting;",
            "float3 outputRgb = ApplyClassicSkinFog(lit, input.vWorldPos);",
            "return float4(outputRgb, baseMap.a);");
    }

    [Fact]
    public void ProductionHeadCompositionIncludesMap1AndKeepsNoEgtOnSkinShader()
    {
        var composer = RenderingSource("Npc", "Composition", "NpcHeadTextureComposer.cs");
        var planner = RenderingSource("Npc", "Composition", "NpcCompositionPlanner.cs");
        var cpuHead = SourceContract.ReadSource(
            ["src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcHeadBuilder.cs"]);
        var nativeHead = RenderingSource("Npc", "Assembly", "NpcExportHeadAssembler.cs");

        SourceContract.AssertOrder(
            composer,
            "FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture, authoredDelta)",
            "Inject(");
        Assert.Contains(
            "FaceGenHeadShaderFamilyResolver.ApplyDefaultDetailModulation(texture)",
            composer,
            StringComparison.Ordinal);
        Assert.Contains("map1Source={4} map1EffectivePath={5}", planner,
            StringComparison.Ordinal);
        Assert.Contains("var classicSkin2000 = npc.Game == BethesdaGame.Oblivion", cpuHead,
            StringComparison.Ordinal);
        Assert.Contains("submesh.IsFaceGen = classicSkin2000", cpuHead, StringComparison.Ordinal);
        Assert.Contains("part.Submesh.IsFaceGen = classicSkin2000", nativeHead,
            StringComparison.Ordinal);
        Assert.Contains("FaceGenHeadShaderFamilyResolver.ApplyClassicSkin2000Material(", cpuHead,
            StringComparison.Ordinal);
        Assert.Contains("FaceGenHeadShaderFamilyResolver.ApplyClassicSkin2000Material(", nativeHead,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EffectiveHeadTextureSource != NpcHeadTextureSource.BaseDiffuse",
            cpuHead + nativeHead,
            StringComparison.Ordinal);
    }

    private static string RenderingSource(params string[] path) => SourceContract.ReadSource(
        ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", .. path]);

    private static string D3D12Source(params string[] path) => RenderingSource(path);
}
