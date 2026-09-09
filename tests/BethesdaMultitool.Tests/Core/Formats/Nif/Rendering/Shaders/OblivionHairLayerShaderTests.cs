using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>Independent SM3002/SM3LL LayerMap equations and the native submission boundary.</summary>
public sealed class OblivionHairLayerShaderTests
{
    [Theory]
    [InlineData(0f, 0.25f)]
    [InlineData(0.5f, 0.625f)]
    [InlineData(1f, 1f)]
    public void Sm3LayerAlpha_ControlsRgbOnlyBeforeHairTint(float layerAlpha, float expectedRed)
    {
        var baseSample = new Vector4(0.25f, 0.5f, 0.75f, 0.2f);
        var layerSample = new Vector4(1f, 0.25f, 0.125f, layerAlpha);
        var layered = RecoveredLayer(baseSample, layerSample);
        Assert.Equal(expectedRed, layered.X, 6);
        Assert.Equal(baseSample.W, layered.W);
        var tint = new Vector3(384f / 255f); // Installed Hannibal HCLR C0C0C0, source green1.
        var tinted = new Vector3(layered.X, layered.Y, layered.Z) * tint;
        Assert.Equal(expectedRed * 384f / 255f, tinted.X, 6);
        if (layerAlpha.Equals(1f))
        {
            Assert.True(tinted.X > 1f); // No intermediate byte clamp or guessed brightness multiplier.
        }
    }

    [Fact]
    public void IndependentlyFilteredLayer_CannotBeReplacedWithPrecompositedTexels()
    {
        var baseSample = new Vector4(0f, 0f, 0f, 0.25f);
        var firstLayer = new Vector4(0f, 0f, 0f, 1f);
        var secondLayer = new Vector4(1f, 1f, 1f, 0f);
        var independentlyFiltered = RecoveredLayer(baseSample, Vector4.Lerp(firstLayer, secondLayer, 0.5f));
        var flattened = Vector4.Lerp(RecoveredLayer(baseSample, firstLayer), RecoveredLayer(baseSample, secondLayer),
            0.5f);
        VectorAssert.Equal(new Vector4(0.25f), independentlyFiltered);
        VectorAssert.Equal(new Vector4(0f, 0f, 0f, 0.25f), flattened);
        Assert.Equal(baseSample.W, independentlyFiltered.W);
    }

    [Fact]
    public void InstalledGreyPixel_RequiresTheLayerAndKeepsBaseCoverage()
    {
        // Retained installed Grey/Grey_hl DDS census at pixel(108,272), LayerMap alpha255.
        var baseline = new Vector4(132f / 255f, 130f / 255f, 123f / 255f, 0.3f);
        var layer = new Vector4(247f / 255f, 235f / 255f, 222f / 255f, 1f);
        var result = RecoveredLayer(baseline, layer);
        Assert.Equal(115f / 255f, result.X - baseline.X, 6);
        Assert.Equal(baseline.W, result.W);
    }

    [Fact]
    public void NativeShader_UsesIndependentFilteredLayerBeforeTintAndNeverAsCoverage()
    {
        var source = SourceContract.ReadShaderSource("reference.frag.hlsl");
        var operation = SourceContract.Extract(source,
            "    if (HasOblivionHairLayer(input.vTextureState.z))", "    // FO4/FO76 grayscale-to-palette");
        Assert.Contains("input.vTexIndices.z, materialUv, input.vTextureState.z", operation, StringComparison.Ordinal);
        Assert.Contains("sample.rgb = lerp(sample.rgb, hairLayer.rgb, hairLayer.a);", operation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("sample.a =", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("saturate", operation, StringComparison.Ordinal);
        Assert.Equal(1, SourceContract.CountOccurrences(source, "if (HasOblivionHairLayer("));
        SourceContract.AssertOrder(source,
            "float4 sample = SampleMaterialTexture(",
            "if (HasOblivionHairLayer(",
            "sample.rgb *= input.vEffectTint.rgb;",
            "saturate(sample.a * vertexCoverageAlpha)");
        Assert.Equal(524288u, OblivionHairLayerPolicy.TextureFlag);
        Assert.Contains("& 524288u", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDraws_ReadLiveLayerStateThroughTheNamedDescriptorUnion()
    {
        var cache = ReadRenderingSource("D3D12/CachedSubmesh12.cs");
        var state = SourceContract.Extract(cache, "    public Vector4 TextureState", "    public bool TexturesReady");
        Assert.Contains("return WithResidentHairLayer(_textureState);", state, StringComparison.Ordinal);
        SourceContract.AssertOrder(state, "_textureState = state;", "return WithResidentHairLayer(state);");
        Assert.Contains("compatible && HasResidentOblivionHairLayer", state, StringComparison.Ordinal);
        Assert.Contains("OblivionHairLayer is not { IsReady: false }", cache, StringComparison.Ordinal);
        var renderer = ReadRenderingSource("D3D12/ReferenceRenderer12.cs");
        Assert.Equal(2, SourceContract.CountOccurrences(renderer, ".ResolveAuxiliaryTextureIndex(0)"));
        var viewer = ReadRenderingSource("D3D12/Viewer/BethesdaViewerStaticRenderer12.cs");
        Assert.Contains("submesh.ResolveAuxiliaryTextureIndex(_neutralTextureIndex)", viewer, StringComparison.Ordinal);
        var owner = ReadRenderingSource("D3D12/ReferenceMeshCache12.cs");
        Assert.Contains("Acquire(textureCache.GetOrUpload(sub.OblivionHairLayerTexturePath!))", owner,
            StringComparison.Ordinal);
        Assert.Contains("textureCache.Release(submesh.OblivionHairLayer);", owner, StringComparison.Ordinal);
        Assert.Contains("OblivionHairLayer = oblivionHairLayer", owner, StringComparison.Ordinal);
        var mapper = ReadRenderingSource("D3D12/ReferenceMeshDecoder12.cs");
        Assert.Equal(2,
            SourceContract.CountOccurrences(mapper, "OblivionHairLayerTexturePath: sub.OblivionHairLayerTexturePath"));
    }

    private static Vector4 RecoveredLayer(Vector4 baseSample, Vector4 layer)
    {
        var rgb = Vector3.Lerp(new Vector3(baseSample.X, baseSample.Y, baseSample.Z),
            new Vector3(layer.X, layer.Y, layer.Z), layer.W);
        return new Vector4(rgb, baseSample.W);
    }

    private static string ReadRenderingSource(string relative)
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", relative);
    }
}