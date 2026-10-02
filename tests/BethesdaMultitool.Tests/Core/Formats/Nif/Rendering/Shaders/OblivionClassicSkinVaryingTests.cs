using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>Recovered SKIN2000/2001 interpolation boundaries, distinct from photometric matching.</summary>
public sealed class OblivionClassicSkinVaryingTests
{
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(10f, 0.9924039f)]
    [InlineData(30f, 0.9330127f)]
    [InlineData(60f, 0.75f)]
    public void CurvedTriangle_PreservesInterpolatedLightLength(float degrees, float expectedDirect)
    {
        var radians = degrees * MathF.PI / 180f;
        var first = new Vector3(-MathF.Sin(radians), 0f, MathF.Cos(radians));
        var second = new Vector3(MathF.Sin(radians), 0f, MathF.Cos(radians));
        // Each input is the raw VS result after complete tangent-space normalization.
        var interpolated = 0.25f * first + 0.25f * second + 0.5f * Vector3.UnitZ;
        var direct = Vector3.Dot(Vector3.UnitZ, interpolated);
        Assert.Equal(expectedDirect, direct, 6);
        var pixelRenormalized = Vector3.Dot(Vector3.UnitZ, Vector3.Normalize(interpolated));
        Assert.Equal(1f, pixelRenormalized, 6);
        Assert.Equal(0.4f + 0.6f * expectedDirect,
            ClassicSkin2000Lighting.Compute(direct, 1f, 0.6f, 0.4f), 6);
        if (degrees > 0f) Assert.True(direct < pixelRenormalized);
    }

    [Fact]
    public void ValidVertexLightsMayCancel_AndMustRemainZeroAtThePixel()
    {
        var interpolated = 0.5f * Vector3.UnitZ + 0.5f * -Vector3.UnitZ;
        Assert.Equal(Vector3.Zero, interpolated);
        Assert.Equal(0.4f, ClassicSkin2000Lighting.Compute(
            Vector3.Dot(Vector3.UnitZ, interpolated), 1f, 0.6f, 0.4f), 6);
        var pixel = SourceContract.ReadShaderSource("reference_classic_skin.frag.hlsl");
        Assert.DoesNotContain("classicLightLengthSquared", pixel, StringComparison.Ordinal);
        Assert.Contains("all(isfinite(input.vClassicSkinLight))", pixel, StringComparison.Ordinal);
    }

    [Fact]
    public void EyeInterpolation_ReconstructsTheVertexNormalizedDirectionBeforeRim()
    {
        var eye = new Vector3(1f, 1f, 1f);
        var first = new Vector3(-2f, 0f, 0f);
        var second = new Vector3(2f, 0f, 0f);
        var third = new Vector3(0f, 2f, 0f);
        var recovered = Vector3.Normalize(
            0.25f * Vector3.Normalize(eye - first) +
            0.25f * Vector3.Normalize(eye - second) +
            0.5f * Vector3.Normalize(eye - third));
        var perPixel = Vector3.Normalize(eye - (0.25f * first + 0.25f * second + 0.5f * third));
        Assert.Equal(0.803369f, recovered.Z, 6);
        Assert.Equal(0.7071068f, perPixel.Z, 6);
        var recoveredRim = ClassicSkin2000Lighting.Compute(0f, recovered.Z, 1f, 0f);
        var previousRim = ClassicSkin2000Lighting.Compute(0f, perPixel.Z, 1f, 0f);
        Assert.Equal(0.003801246f, recoveredRim, 6);
        Assert.Equal(0.01256313f, previousRim, 6);
        Assert.True(recoveredRim < previousRim);
    }

    [Theory]
    [InlineData(0.125f)]
    [InlineData(1f)]
    [InlineData(8f)]
    public void VertexProjection_CancelsUniformPlacementScaleWithoutNormalizingBasisAxes(float scale)
    {
        var direction = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var projected = Vector3.Normalize(new Vector3(
            Vector3.Dot(2f * scale * Vector3.UnitX, direction),
            Vector3.Dot(scale * Vector3.UnitY, direction),
            Vector3.Dot(scale * Vector3.UnitZ, direction)));
        Assert.Equal(0.8944272f, projected.X, 6);
        Assert.Equal(0.4472136f, projected.Z, 6);
        Assert.True(projected.Z < direction.Z); // Independent axis normalization would lose this.
    }

    [Fact]
    public void VertexAndPixelSources_PreserveBothRecoveredCentroidDirections()
    {
        var vertex = SourceContract.ReadShaderSource("reference.vert.hlsl");
        var pixel = SourceContract.ReadShaderSource("reference_classic_skin.frag.hlsl");
        foreach (var source in new[] { vertex, pixel })
        {
            Assert.Contains("centroid float3 vClassicSkinLight : TEXCOORD18;", source, StringComparison.Ordinal);
            Assert.Contains("centroid float3 vClassicSkinEye : TEXCOORD19;", source, StringComparison.Ordinal);
        }

        SourceContract.AssertOrder(vertex,
            "o.vClassicSkinLight = ProjectClassicSkinDirection(",
            "float3 classicEye = NormalizeClassicSkinDirection(uCameraPosFogPower.xyz - worldPos.xyz);",
            "o.vClassicSkinEye = ProjectClassicSkinDirection(");
        var recovered = SourceContract.Extract(pixel,
            "        NdotL = max(dot(tangentNormal, input.vClassicSkinLight), 0.0);",
            "    float oneMinusNdotV = 1.0 - NdotV;");
        Assert.Contains("float3 interpolatedEye = normalize(input.vClassicSkinEye);", recovered,
            StringComparison.Ordinal);
        Assert.Contains("NdotV = max(dot(tangentNormal, interpolatedEye), 0.0);", recovered, StringComparison.Ordinal);
        Assert.DoesNotContain("normalize(input.vClassicSkinLight)", pixel, StringComparison.Ordinal);
        Assert.DoesNotContain("input.vWorldPos", recovered, StringComparison.Ordinal);
        Assert.DoesNotContain("flipTangentBasis", recovered, StringComparison.Ordinal);
        Assert.Contains("return float4(outputRgb, baseMap.a);", pixel, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveredVertexPermutation_IsRestrictedToTheTes4DirectFaceGenPso()
    {
        var factory = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "D3D12", "ReferencePipelineFactory12.cs");
        var renderer = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "D3D12", "Viewer", "BethesdaViewerStaticRenderer12.cs");
        Assert.Contains("DirectClassicSkinRequested = game == BethesdaGame.Oblivion;", factory,
            StringComparison.Ordinal);
        Assert.Contains("game == Core.Games.BethesdaGame.Oblivion && draw.NativeSemantics.IsFaceGen", renderer,
            StringComparison.Ordinal);
        Assert.Equal(1,
            SourceContract.CountOccurrences(factory, "new ShaderMacro(\"REFERENCE_OBLIVION_CLASSIC_SKIN\", \"1\")"));
        var pair = SourceContract.Extract(factory, "private void TryCreateDirectClassicSkinPipelines()",
            "private void TryCreateModernStandardOpaquePipelines()");
        SourceContract.AssertOrder(pair,
            "\"reference.vert.hlsl\", \"main\", \"vs_5_1\",",
            "new ShaderMacro(\"REFERENCE_OBLIVION_CLASSIC_SKIN\", \"1\")",
            "\"reference_classic_skin.frag.hlsl\", \"main\", \"ps_5_1\"");
        Assert.Single(ShaderPermutations.All,
            permutation => permutation.Macros.Any(macro => macro.Name == "REFERENCE_OBLIVION_CLASSIC_SKIN"));
        var key = GpuShaderCompiler12.BuildCacheKey("reference.vert.hlsl", "main", "vs_5_1",
            [new ShaderMacro("REFERENCE_OBLIVION_CLASSIC_SKIN", "1")]);
        Assert.Contains(key, GpuShaderBytecodePack12.CurrentPermutationKeys());
    }

    [Fact]
    [Trait("Category", TestCategories.ShaderCompile)]
    public void RecoveredPairAndUnchangedGenericVertex_CompileThroughTheProductionCompiler()
    {
        ShaderCompileTestGuard.SkipUnlessEnabled();
        Assert.False(GpuShaderCompiler12.Compile("reference.vert.hlsl", "main", "vs_5_1",
            new ShaderMacro("REFERENCE_OBLIVION_CLASSIC_SKIN", "1")).IsEmpty);
        Assert.False(GpuShaderCompiler12.Compile("reference_classic_skin.frag.hlsl", "main", "ps_5_1").IsEmpty);
        Assert.False(GpuShaderCompiler12.Compile("reference.vert.hlsl", "main", "vs_5_1").IsEmpty);
    }
}
